/*
 * Copyright © 2016 - 2025 VMware, Inc. All Rights Reserved.
 *
 * Licensed under the Apache License, Version 2.0 (the "License"); you may not use this file
 * except in compliance with the License at
 * http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software distributed under the
 * License is distributed on an "AS IS" BASIS, without warranties or conditions of any kind,
 * EITHER EXPRESS OR IMPLIED. See the License for the specific language governing
 * permissions and limitations under the License.
 */

using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rapid.Messaging;
using Rapid.Monitoring;
using Rapid.Pb;

namespace Rapid;

/// <summary>
/// Membership server class that implements the Rapid protocol.
/// </summary>
internal sealed class MembershipService : IMembershipServiceHandler
{
    private readonly ILogger<MembershipService> _logger;
    private readonly MembershipView _membershipView;
    private readonly MultiNodeCutDetector _cutDetection;
    private readonly Endpoint _myAddr;
    private readonly IBroadcaster _broadcaster;
    private readonly Dictionary<Endpoint, Channel<TaskCompletionSource<RapidResponse>>> _joinersToRespondTo = [];
    private readonly Dictionary<Endpoint, NodeId> _joinerUuid = [];
    private readonly Dictionary<Endpoint, Metadata> _joinerMetadata = [];
    private readonly IMessagingClient _messagingClient;
    private readonly MetadataManager _metadataManager;
    private readonly Dictionary<ClusterEvents, List<Action<ClusterStatusChange>>> _subscriptions;
    private FastPaxos? _fastPaxosInstance;

    // Fields used by batching logic
    private readonly Channel<AlertMessage> _sendQueue;
    private readonly Lock _batchSchedulerLock = new();
    private readonly SharedResources _sharedResources;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly List<IDisposable> _failureDetectors = [];
    private readonly IEdgeFailureDetectorFactory _fdFactory;
    private bool _announcedProposal = false;
    private readonly Lock _membershipUpdateLock = new();
    private readonly Settings _settings;

    public MembershipService(
        Endpoint myAddr,
        MultiNodeCutDetector cutDetection,
        MembershipView membershipView,
        SharedResources sharedResources,
        Settings settings,
        IMessagingClient messagingClient,
        IEdgeFailureDetectorFactory edgeFailureDetector,
        ILoggerFactory? loggerFactory = null)
        : this(myAddr, cutDetection, membershipView, sharedResources, settings, messagingClient,
              edgeFailureDetector, [],
              [], loggerFactory)
    {
    }

    public MembershipService(Endpoint myAddr, MultiNodeCutDetector cutDetection,
                            MembershipView membershipView, SharedResources sharedResources,
                            Settings settings, IMessagingClient messagingClient,
                            IEdgeFailureDetectorFactory edgeFailureDetector,
                            Dictionary<Endpoint, Metadata> metadataMap,
                            Dictionary<ClusterEvents, List<Action<ClusterStatusChange>>> subscriptions,
                            ILoggerFactory? loggerFactory = null)
    {
        _myAddr = myAddr;
        _settings = settings;
        _membershipView = membershipView;
        _cutDetection = cutDetection;
        _sharedResources = sharedResources;
        _metadataManager = new MetadataManager();
        _metadataManager.AddMetadata(metadataMap);
        _messagingClient = messagingClient;
        _broadcaster = new UnicastToAllBroadcaster(messagingClient);
        _subscriptions = subscriptions;
        _fdFactory = edgeFailureDetector;
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<MembershipService>();
        _sendQueue = Channel.CreateUnbounded<AlertMessage>();

        // Make sure there is an empty list for every enum type
        foreach (var evt in Enum.GetValues<ClusterEvents>())
        {
            if (!_subscriptions.ContainsKey(evt))
            {
                _subscriptions[evt] = [];
            }
        }

        // Start background jobs
        _ = Task.Run(AlertBatcherAsync, _shutdownCts.Token);

        _broadcaster.SetMembership(_membershipView.GetRing(0));

        // Prepare consensus instance
        _fastPaxosInstance = new FastPaxos(_myAddr, _membershipView.GetCurrentConfigurationId(),
                                          _membershipView.GetMembershipSize(), _messagingClient,
                                          _broadcaster, _sharedResources, DecideViewChange,
                                          _settings, loggerFactory);

        CreateFailureDetectorsForCurrentConfiguration();

        // Execute all VIEW_CHANGE callbacks
        var configurationId = _membershipView.GetCurrentConfigurationId();
        var currentMembership = _membershipView.GetRing(0);
        var nodeStatusChanges = GetInitialViewChange();
        var clusterStatusChange = new ClusterStatusChange(configurationId, currentMembership, nodeStatusChanges);

        foreach (var cb in _subscriptions[ClusterEvents.ViewChange])
        {
            cb(clusterStatusChange);
        }
    }

    public async Task<RapidResponse> HandleMessageAsync(RapidRequest msg)
    {
        return msg.ContentCase switch
        {
            RapidRequest.ContentOneofCase.PreJoinMessage => await HandleMessageAsync(msg.PreJoinMessage),
            RapidRequest.ContentOneofCase.JoinMessage => await HandleMessageAsync(msg.JoinMessage),
            RapidRequest.ContentOneofCase.BatchedAlertMessage => await HandleMessageAsync(msg.BatchedAlertMessage),
            RapidRequest.ContentOneofCase.ProbeMessage => await HandleMessageAsync(msg.ProbeMessage),
            RapidRequest.ContentOneofCase.FastRoundPhase2BMessage or
            RapidRequest.ContentOneofCase.Phase1AMessage or
            RapidRequest.ContentOneofCase.Phase1BMessage or
            RapidRequest.ContentOneofCase.Phase2AMessage or
            RapidRequest.ContentOneofCase.Phase2BMessage => await HandleConsensusMessagesAsync(msg),
            RapidRequest.ContentOneofCase.LeaveMessage => await HandleLeaveMessageAsync(msg),
            _ => throw new ArgumentException($"Unidentified RapidRequest type {msg.ContentCase}")
        };
    }

    private async Task<RapidResponse> HandleMessageAsync(PreJoinMessage msg)
    {
        var tcs = new TaskCompletionSource<RapidResponse>();

        await _sharedResources.GetProtocolExecutor().Writer.WriteAsync(async () =>
        {
            var joiningEndpoint = msg.Sender;
            var statusCode = _membershipView.IsSafeToJoin(joiningEndpoint, msg.NodeId);
            var builder = new JoinResponse
            {
                Sender = _myAddr,
                ConfigurationId = _membershipView.GetCurrentConfigurationId(),
                StatusCode = statusCode
            };

            _logger.LogInformation("Join at seed for {{seed:{Seed}, sender:{Sender}, config:{Config}, size:{Size}}}",
                Utils.Loggable(_myAddr), Utils.Loggable(msg.Sender),
                _membershipView.GetCurrentConfigurationId(), _membershipView.GetMembershipSize());

            if (statusCode == JoinStatusCode.SafeToJoin || statusCode == JoinStatusCode.HostnameAlreadyInRing)
            {
                builder.Endpoints.AddRange(_membershipView.GetExpectedObserversOf(joiningEndpoint));
            }

            tcs.SetResult(Utils.ToRapidResponse(builder));
        });

        return await tcs.Task;
    }

    private async Task<RapidResponse> HandleMessageAsync(JoinMessage joinMessage)
    {
        var tcs = new TaskCompletionSource<RapidResponse>();

        await _sharedResources.GetProtocolExecutor().Writer.WriteAsync(async () =>
        {
            var currentConfiguration = _membershipView.GetCurrentConfigurationId();

            if (currentConfiguration == joinMessage.ConfigurationId)
            {
                _logger.LogTrace("Enqueuing SAFE_TO_JOIN for {{sender:{Sender}, config:{Config}, size:{Size}}}",
                    Utils.Loggable(joinMessage.Sender), currentConfiguration,
                    _membershipView.GetMembershipSize());

                ref var channel = ref CollectionsMarshal.GetValueRefOrAddDefault(_joinersToRespondTo, joinMessage.Sender, out var _);
                channel ??= Channel.CreateUnbounded<TaskCompletionSource<RapidResponse>>();
                await channel.Writer.WriteAsync(tcs);

                var alertMsg = new AlertMessage
                {
                    EdgeSrc = _myAddr,
                    EdgeDst = joinMessage.Sender,
                    EdgeStatus = EdgeStatus.Up,
                    ConfigurationId = currentConfiguration,
                    NodeId = joinMessage.NodeId,
                    Metadata = joinMessage.Metadata
                };
                alertMsg.RingNumber.AddRange(joinMessage.RingNumber);

                EnqueueAlertMessage(alertMsg);
            }
            else
            {
                var configuration = _membershipView.GetConfiguration();
                _logger.LogInformation("Wrong configuration for {{sender:{Sender}, config:{Config}, myConfig:{MyConfig}, size:{Size}}}",
                    Utils.Loggable(joinMessage.Sender), joinMessage.ConfigurationId,
                    currentConfiguration, _membershipView.GetMembershipSize());

                var responseBuilder = new JoinResponse
                {
                    Sender = _myAddr,
                    ConfigurationId = configuration.GetConfigurationId()
                };

                if (_membershipView.IsHostPresent(joinMessage.Sender) &&
                    _membershipView.IsIdentifierPresent(joinMessage.NodeId))
                {
                    responseBuilder.StatusCode = JoinStatusCode.SafeToJoin;
                    responseBuilder.Endpoints.AddRange(configuration.Endpoints);
                    responseBuilder.Identifiers.AddRange(configuration.NodeIds);
                    var allMetadata = _metadataManager.GetAllMetadata();
                    responseBuilder.MetadataKeys.AddRange(allMetadata.Keys);
                    responseBuilder.MetadataValues.AddRange(allMetadata.Values);
                }
                else
                {
                    responseBuilder.StatusCode = JoinStatusCode.ConfigChanged;
                }

                tcs.SetResult(Utils.ToRapidResponse(responseBuilder));
            }
        });

        return await tcs.Task;
    }

    private async Task<RapidResponse> HandleMessageAsync(BatchedAlertMessage messageBatch)
    {
        var tcs = new TaskCompletionSource<RapidResponse>();

        await _sharedResources.GetProtocolExecutor().Writer.WriteAsync(async () =>
        {
            if (!FilterAlertMessages(messageBatch, _membershipView.GetCurrentConfigurationId()))
            {
                tcs.SetResult(Utils.ToRapidResponse(new ConsensusResponse()));
                return;
            }

            var proposals = new List<Endpoint>();
            foreach (var msg in messageBatch.Messages)
            {
                var extractedMessage = ExtractJoinerUuidAndMetadata(msg);
                proposals.AddRange(_cutDetection.AggregateForProposal(extractedMessage));
            }

            proposals.AddRange(_cutDetection.InvalidateFailingEdges(_membershipView));

            lock (_membershipUpdateLock)
            {
                if (proposals.Count > 0 && !_announcedProposal)
                {
                    _announcedProposal = true;
                    var currentConfigurationId = _membershipView.GetCurrentConfigurationId();
                    _logger.LogDebug("Initiating consensus for {Proposal}", Utils.Loggable(proposals));

                    // Notify subscribers about the proposal
                    var nodeStatusChanges = CreateNodeStatusChangeList(proposals);
                    var currentMembership = _membershipView.GetRing(0);
                    var clusterStatusChange = new ClusterStatusChange(currentConfigurationId, currentMembership, nodeStatusChanges);

                    foreach (var cb in _subscriptions[ClusterEvents.ViewChangeProposal])
                    {
                        cb(clusterStatusChange);
                    }

                    _fastPaxosInstance?.Propose(proposals);
                }
            }

            tcs.SetResult(Utils.ToRapidResponse(new ConsensusResponse()));
        });

        return await tcs.Task;
    }

    private Task<RapidResponse> HandleConsensusMessagesAsync(RapidRequest request)
    {
        return Task.Run(() => _fastPaxosInstance?.HandleMessages(request)
                             ?? Utils.ToRapidResponse(new ConsensusResponse()));
    }

    private async Task<RapidResponse> HandleLeaveMessageAsync(RapidRequest request)
    {
        var leaveMessage = request.LeaveMessage;
        _logger.LogInformation("Received leave message from {Sender} at {MyAddr}",
            Utils.Loggable(leaveMessage.Sender), Utils.Loggable(_myAddr));
        EdgeFailureNotification(leaveMessage.Sender, _membershipView.GetCurrentConfigurationId());
        return Utils.ToRapidResponse(new ConsensusResponse());
    }

    private async Task<RapidResponse> HandleMessageAsync(ProbeMessage probeMessage)
    {
        return Utils.ToRapidResponse(new ProbeResponse());
    }

    private void DecideViewChange(List<Endpoint> proposal)
    {
        lock (_membershipUpdateLock)
        {
            _announcedProposal = false;
        }

        foreach (var node in proposal)
        {
            if (_membershipView.IsHostPresent(node))
            {
                _logger.LogDebug("Removing node {Node}", Utils.Loggable(node));
                _membershipView.RingDelete(node);
            }
            else
            {
                if (!_joinerUuid.TryGetValue(node, out var nodeId))
                {
                    _logger.LogWarning("Decided on a node without UUID: {Node}", Utils.Loggable(node));
                    continue;
                }

                var metadata = _joinerMetadata.GetValueOrDefault(node, new Metadata());

                _logger.LogDebug("Adding node {Node}", Utils.Loggable(node));
                _membershipView.RingAdd(node, nodeId);
                _metadataManager.Add(node, metadata);

                _joinerUuid.Remove(node);
                _joinerMetadata.Remove(node);

                // Respond to joiners
                if (_joinersToRespondTo.TryGetValue(node, out var channel))
                {
                    var config = _membershipView.GetConfiguration();
                    var response = new JoinResponse
                    {
                        Sender = _myAddr,
                        StatusCode = JoinStatusCode.SafeToJoin,
                        ConfigurationId = config.GetConfigurationId()
                    };
                    response.Endpoints.AddRange(config.Endpoints);
                    response.Identifiers.AddRange(config.NodeIds);
                    var allMetadata = _metadataManager.GetAllMetadata();
                    response.MetadataKeys.AddRange(allMetadata.Keys);
                    response.MetadataValues.AddRange(allMetadata.Values);

                    var rapidResponse = Utils.ToRapidResponse(response);

                    // Send response to all waiting tasks
                    while (channel.Reader.TryRead(out var tcs))
                    {
                        tcs.SetResult(rapidResponse);
                    }

                    _joinersToRespondTo.Remove(node);
                }
            }
        }

        _cutDetection.Clear();
        _broadcaster.SetMembership(_membershipView.GetRing(0));

        // Recreate failure detectors
        foreach (var fd in _failureDetectors)
        {
            fd.Dispose();
        }
        _failureDetectors.Clear();

        _fastPaxosInstance = new FastPaxos(_myAddr, _membershipView.GetCurrentConfigurationId(),
                                          _membershipView.GetMembershipSize(), _messagingClient,
                                          _broadcaster, _sharedResources, DecideViewChange, _settings);

        CreateFailureDetectorsForCurrentConfiguration();

        // Notify subscribers
        var configurationId = _membershipView.GetCurrentConfigurationId();
        var currentMembership = _membershipView.GetRing(0);
        var nodeStatusChanges = CreateNodeStatusChangeList(proposal);
        var clusterStatusChange = new ClusterStatusChange(configurationId, currentMembership, nodeStatusChanges);

        foreach (var cb in _subscriptions[ClusterEvents.ViewChange])
        {
            cb(clusterStatusChange);
        }
    }

    public void RegisterSubscription(ClusterEvents evt, Action<ClusterStatusChange> callback)
    {
        _subscriptions[evt].Add(callback);
    }

    public List<Endpoint> GetMembershipView()
    {
        return _membershipView.GetRing(0);
    }

    public int GetMembershipSize()
    {
        return _membershipView.GetMembershipSize();
    }

    public Dictionary<Endpoint, Metadata> GetMetadata()
    {
        return new Dictionary<Endpoint, Metadata>(_metadataManager.GetAllMetadata());
    }

    public void Shutdown()
    {
        _shutdownCts.Cancel();
        foreach (var fd in _failureDetectors)
        {
            fd.Dispose();
        }
        _failureDetectors.Clear();
    }

    public async Task LeaveAsync()
    {
        var leaveMessage = new LeaveMessage { Sender = _myAddr };
        var leave = Utils.ToRapidRequest(leaveMessage);

        try
        {
            var observers = _membershipView.GetObserversOf(_myAddr);
            _logger.LogInformation("Leaving: {MyAddr} has {Count} observers: {Observers}",
                Utils.Loggable(_myAddr), observers.Count, string.Join(", ", observers.Select(Utils.Loggable)));
            
            var tasks = observers.Select(endpoint =>
                _messagingClient.SendMessageBestEffortAsync(endpoint, leave, CancellationToken.None));

            using var timeoutCts = new CancellationTokenSource(_settings.LeaveMessageTimeoutMs);
            try
            {
                await Task.WhenAll(tasks).WaitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                _logger.LogTrace("Timeout while leaving");
            }
            catch (Exception ex)
            {
                _logger.LogTrace(ex, "Exception while leaving");
            }
        }
        catch (MembershipView.NodeNotInRingException)
        {
            _logger.LogTrace("Node was already removed prior to leaving");
        }
    }

    private void EnqueueAlertMessage(AlertMessage msg)
    {
        lock (_batchSchedulerLock)
        {
            _sendQueue.Writer.TryWrite(msg);
        }
    }

    private async Task AlertBatcherAsync()
    {
        var buffer = new List<AlertMessage>();

        while (!_shutdownCts.Token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_settings.BatchingWindowMs, _shutdownCts.Token);

                lock (_batchSchedulerLock)
                {
                    while (_sendQueue.Reader.TryRead(out var msg))
                    {
                        buffer.Add(msg);
                    }

                    if (buffer.Count > 0)
                    {
                        var batchedMessage = new BatchedAlertMessage
                        {
                            Sender = _myAddr
                        };
                        batchedMessage.Messages.AddRange(buffer);

                        var request = Utils.ToRapidRequest(batchedMessage);
                        _ = _broadcaster.BroadcastAsync(request);

                        buffer.Clear();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private bool FilterAlertMessages(BatchedAlertMessage batchedAlertMessage, long currentConfigurationId)
    {
        return batchedAlertMessage.Messages.Any(m => m.ConfigurationId == currentConfigurationId);
    }

    private AlertMessage ExtractJoinerUuidAndMetadata(AlertMessage alertMessage)
    {
        if (alertMessage.EdgeStatus == EdgeStatus.Up && alertMessage.NodeId != null)
        {
            _joinerUuid[alertMessage.EdgeDst] = alertMessage.NodeId;
            _joinerMetadata[alertMessage.EdgeDst] = alertMessage.Metadata;
        }
        return alertMessage;
    }

    private List<NodeStatusChange> CreateNodeStatusChangeList(IEnumerable<Endpoint> proposal)
    {
        var list = new List<NodeStatusChange>();
        foreach (var node in proposal)
        {
            var status = _membershipView.IsHostPresent(node) ? EdgeStatus.Down : EdgeStatus.Up;
            list.Add(new NodeStatusChange(node, status, _metadataManager.Get(node) ?? new Metadata()));
        }
        return list;
    }

    private List<NodeStatusChange> GetInitialViewChange()
    {
        var list = new List<NodeStatusChange>();
        foreach (var node in _membershipView.GetRing(0))
        {
            list.Add(new NodeStatusChange(node, EdgeStatus.Up, _metadataManager.Get(node) ?? new Metadata()));
        }
        return list;
    }

    private void CreateFailureDetectorsForCurrentConfiguration()
    {
        var subjects = _membershipView.GetSubjectsOf(_myAddr);
        var configurationId = _membershipView.GetCurrentConfigurationId();

        for (int i = 0; i < subjects.Count; i++)
        {
            var subject = subjects[i];
            var ringNumber = i;
            var fd = _fdFactory.CreateInstance(subject, () =>
            {
                EdgeFailureNotification(subject, configurationId);
            });

            fd.Start();
            _failureDetectors.Add(fd);
        }
    }

    private void EdgeFailureNotification(Endpoint subject, long configurationId)
    {
        try
        {
            var ringNumbers = _membershipView.GetRingNumbers(_myAddr, subject);
            _logger.LogInformation("EdgeFailureNotification: {MyAddr} monitoring {Subject} on {RingCount} rings: {Rings}",
                Utils.Loggable(_myAddr), Utils.Loggable(subject), ringNumbers.Count, string.Join(",", ringNumbers));
            
            if (ringNumbers.Count == 0)
            {
                _logger.LogWarning("No monitoring relationship between {MyAddr} and {Subject} - skipping alert",
                    Utils.Loggable(_myAddr), Utils.Loggable(subject));
                return;
            }

            var msg = new AlertMessage
            {
                EdgeSrc = _myAddr,
                EdgeDst = subject,
                EdgeStatus = EdgeStatus.Down,
                ConfigurationId = configurationId
            };
            msg.RingNumber.AddRange(ringNumbers);

            EnqueueAlertMessage(msg);
        }
        catch (MembershipView.NodeNotInRingException ex)
        {
            _logger.LogWarning("Node {Subject} not in ring when processing edge failure notification: {Message}",
                Utils.Loggable(subject), ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in EdgeFailureNotification for {Subject}", Utils.Loggable(subject));
        }
    }
}
