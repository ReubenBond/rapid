using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Rapid.Messaging;
using Rapid.Monitoring;
using Rapid.Pb;

namespace Rapid;

/// <summary>
/// Membership server class that implements the Rapid protocol.
/// </summary>
internal sealed partial class MembershipService : IMembershipServiceHandler, IDisposable
{
    private readonly ILogger<MembershipService> _logger;
    private readonly MembershipView _membershipView;
    private readonly MultiNodeCutDetector _cutDetection;
    private readonly Endpoint _myAddr;
    private readonly UnicastToAllBroadcaster _broadcaster;
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
    private bool _announcedProposal;
    private readonly Lock _membershipUpdateLock = new();
    private readonly RapidProtocolOptions _options;
    private readonly IOptions<RapidProtocolOptions> _protocolOptions;

    private readonly struct LoggableEndpoint(Endpoint endpoint)
    {
        private readonly Endpoint _endpoint = endpoint;
        public override readonly string ToString() => RapidUtils.Loggable(_endpoint);
    }

    private readonly struct LoggableEndpoints(IEnumerable<Endpoint> endpoints)
    {
        private readonly IEnumerable<Endpoint> _endpoints = endpoints;
        public override readonly string ToString() => RapidUtils.Loggable(_endpoints);
    }

    private readonly struct CurrentConfigId(MembershipView view)
    {
        private readonly MembershipView _view = view;
        public override readonly string ToString() => _view.GetCurrentConfigurationId().ToString();
    }

    private readonly struct MembershipSize(MembershipView view)
    {
        private readonly MembershipView _view = view;
        public override readonly string ToString() => _view.GetMembershipSize().ToString();
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Initiating consensus for {Proposal}")]
    private partial void LogInitiatingConsensus(LoggableEndpoints Proposal);

    [LoggerMessage(Level = LogLevel.Information, Message = "Received leave message from {Sender} at {MyAddr}")]
    private partial void LogReceivedLeaveMessage(LoggableEndpoint Sender, LoggableEndpoint MyAddr);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Removing node {Node}")]
    private partial void LogRemovingNode(LoggableEndpoint Node);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Decided on a node without UUID: {Node}")]
    private partial void LogDecidedNodeWithoutUuid(LoggableEndpoint Node);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Adding node {Node}")]
    private partial void LogAddingNode(LoggableEndpoint Node);

    [LoggerMessage(Level = LogLevel.Information, Message = "Leaving: {MyAddr} has {Count} observers: {Observers}")]
    private partial void LogLeavingWithObservers(LoggableEndpoint MyAddr, int Count, LoggableEndpoints Observers);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Timeout while leaving")]
    private partial void LogTimeoutWhileLeaving();

    [LoggerMessage(Level = LogLevel.Trace, Message = "Exception while leaving")]
    private partial void LogExceptionWhileLeaving(Exception ex);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Node was already removed prior to leaving")]
    private partial void LogNodeAlreadyRemoved();

    [LoggerMessage(Level = LogLevel.Information, Message = "Ignoring failure notification from old configuration {Subject}, config: {CurrentConfig}, oldConfiguration: {OldConfig}")]
    private partial void LogIgnoringOldConfigNotification(LoggableEndpoint Subject, CurrentConfigId CurrentConfig, long OldConfig);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Announcing EdgeFail event {Subject}, observer: {MyAddr}, config: {Config}, size: {Size}")]
    private partial void LogAnnouncingEdgeFail(LoggableEndpoint Subject, LoggableEndpoint MyAddr, long Config, MembershipSize Size);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error in EdgeFailureNotification for {Subject}")]
    private partial void LogErrorInEdgeFailureNotification(Exception ex, LoggableEndpoint Subject);

    [LoggerMessage(Level = LogLevel.Information, Message = "Join at seed for {{seed:{Seed}, sender:{Sender}, config:{Config}, size:{Size}}}")]
    private partial void LogJoinAtSeed(LoggableEndpoint Seed, LoggableEndpoint Sender, CurrentConfigId Config, MembershipSize Size);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Enqueuing SAFE_TO_JOIN for {{sender:{Sender}, config:{Config}, size:{Size}}}")]
    private partial void LogEnqueueingSafeToJoin(LoggableEndpoint Sender, CurrentConfigId Config, MembershipSize Size);

    [LoggerMessage(Level = LogLevel.Information, Message = "Wrong configuration for {{sender:{Sender}, config:{Config}, myConfig:{MyConfig}, size:{Size}}}")]
    private partial void LogWrongConfiguration(LoggableEndpoint Sender, long Config, CurrentConfigId MyConfig, MembershipSize Size);

    public MembershipService(
        Endpoint myAddr,
        MultiNodeCutDetector cutDetection,
        MembershipView membershipView,
        SharedResources sharedResources,
        IOptions<RapidProtocolOptions> options,
        IMessagingClient messagingClient,
        IEdgeFailureDetectorFactory edgeFailureDetector,
        ILoggerFactory? loggerFactory = null)
        : this(myAddr, cutDetection, membershipView, sharedResources, options, messagingClient,
              edgeFailureDetector, [],
              [], loggerFactory)
    {
    }

    public MembershipService(Endpoint myAddr, MultiNodeCutDetector cutDetection,
                            MembershipView membershipView, SharedResources sharedResources,
                            IOptions<RapidProtocolOptions> options, IMessagingClient messagingClient,
                            IEdgeFailureDetectorFactory edgeFailureDetector,
                            Dictionary<Endpoint, Metadata> metadataMap,
                            Dictionary<ClusterEvents, List<Action<ClusterStatusChange>>> subscriptions,
                            ILoggerFactory? loggerFactory = null)
    {
        _myAddr = myAddr;
        _protocolOptions = options;
        _options = options.Value;
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

        // Start background jobs and track them
        var alertBatcherTask = Task.Run(AlertBatcherAsync, _shutdownCts.Token);
        _sharedResources.TrackBackgroundTask(alertBatcherTask);

        _broadcaster.SetMembership(_membershipView.GetRing(0));

        // Prepare consensus instance
        _fastPaxosInstance = new FastPaxos(_myAddr, _membershipView.GetCurrentConfigurationId(),
                                          _membershipView.GetMembershipSize(), _messagingClient,
                                          _broadcaster, DecideViewChange,
                                          _protocolOptions, _sharedResources, loggerFactory);

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

    public async Task<RapidResponse> HandleMessageAsync(RapidRequest msg, CancellationToken cancellationToken)
    {
        return msg.ContentCase switch
        {
            RapidRequest.ContentOneofCase.PreJoinMessage => await HandlePreJoinMessageAsync(msg.PreJoinMessage, cancellationToken).ConfigureAwait(false),
            RapidRequest.ContentOneofCase.JoinMessage => await HandleJoinMessageAsync(msg.JoinMessage, cancellationToken).ConfigureAwait(false),
            RapidRequest.ContentOneofCase.BatchedAlertMessage => await HandleBatchedAlertMessageAsync(msg.BatchedAlertMessage, cancellationToken).ConfigureAwait(false),
            RapidRequest.ContentOneofCase.ProbeMessage => await HandleProbeMessage(msg.ProbeMessage, cancellationToken).ConfigureAwait(false),
            RapidRequest.ContentOneofCase.FastRoundPhase2BMessage or
            RapidRequest.ContentOneofCase.Phase1AMessage or
            RapidRequest.ContentOneofCase.Phase1BMessage or
            RapidRequest.ContentOneofCase.Phase2AMessage or
            RapidRequest.ContentOneofCase.Phase2BMessage => await HandleConsensusMessagesAsync(msg, cancellationToken).ConfigureAwait(false),
            RapidRequest.ContentOneofCase.LeaveMessage => await HandleLeaveMessageAsync(msg, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentException($"Unidentified RapidRequest type {msg.ContentCase}")
        };
    }

    private async Task<RapidResponse> HandlePreJoinMessageAsync(PreJoinMessage msg, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<RapidResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

        await _sharedResources.ScheduleAsyncCallback(async () =>
        {
            var joiningEndpoint = msg.Sender;
            var statusCode = _membershipView.IsSafeToJoin(joiningEndpoint, msg.NodeId);
            var builder = new JoinResponse
            {
                Sender = _myAddr,
                ConfigurationId = _membershipView.GetCurrentConfigurationId(),
                StatusCode = statusCode
            };

            LogJoinAtSeed(new LoggableEndpoint(_myAddr), new LoggableEndpoint(msg.Sender),
                new CurrentConfigId(_membershipView), new MembershipSize(_membershipView));

            if (statusCode == JoinStatusCode.SafeToJoin || statusCode == JoinStatusCode.HostnameAlreadyInRing)
            {
                builder.Endpoints.AddRange(_membershipView.GetExpectedObserversOf(joiningEndpoint));
            }

            tcs.SetResult(RapidUtils.ToRapidResponse(builder));
        }).ConfigureAwait(false);

        return await tcs.Task.ConfigureAwait(false);
    }

    private async Task<RapidResponse> HandleJoinMessageAsync(JoinMessage joinMessage, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<RapidResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

        await _sharedResources.ScheduleAsyncCallback(async () =>
        {
            var currentConfiguration = _membershipView.GetCurrentConfigurationId();

            if (currentConfiguration == joinMessage.ConfigurationId)
            {
                LogEnqueueingSafeToJoin(new LoggableEndpoint(joinMessage.Sender), new CurrentConfigId(_membershipView),
                    new MembershipSize(_membershipView));

                ref var channel = ref CollectionsMarshal.GetValueRefOrAddDefault(_joinersToRespondTo, joinMessage.Sender, out var _);
                channel ??= Channel.CreateUnbounded<TaskCompletionSource<RapidResponse>>();
                await channel.Writer.WriteAsync(tcs).ConfigureAwait(false);

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
                LogWrongConfiguration(new LoggableEndpoint(joinMessage.Sender), joinMessage.ConfigurationId,
                    new CurrentConfigId(_membershipView), new MembershipSize(_membershipView));

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

                tcs.SetResult(RapidUtils.ToRapidResponse(responseBuilder));
            }
        }).ConfigureAwait(false);

        return await tcs.Task.ConfigureAwait(false);
    }

    private async Task<RapidResponse> HandleBatchedAlertMessageAsync(BatchedAlertMessage messageBatch, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<RapidResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

        await _sharedResources.ScheduleAsyncCallback(async () =>
        {
            if (!FilterAlertMessages(messageBatch, _membershipView.GetCurrentConfigurationId()))
            {
                tcs.SetResult(RapidUtils.ToRapidResponse(new ConsensusResponse()));
                return;
            }

            var proposals = new List<Endpoint>();
            foreach (var msg in messageBatch.Messages)
            {
                var extractedMessage = ExtractJoinerUuidAndMetadata(msg);
                proposals.AddRange(_cutDetection.AggregateForProposal(extractedMessage));
            }

            proposals.AddRange(_cutDetection.InvalidateFailingEdges(_membershipView));

            Task? proposeTask = null;
            lock (_membershipUpdateLock)
            {
                if (proposals.Count > 0 && !_announcedProposal)
                {
                    _announcedProposal = true;
                    var currentConfigurationId = _membershipView.GetCurrentConfigurationId();
                    LogInitiatingConsensus(new LoggableEndpoints(proposals));

                    // Notify subscribers about the proposal
                    var nodeStatusChanges = CreateNodeStatusChangeList(proposals);
                    var currentMembership = _membershipView.GetRing(0);
                    var clusterStatusChange = new ClusterStatusChange(currentConfigurationId, currentMembership, nodeStatusChanges);

                    foreach (var cb in _subscriptions[ClusterEvents.ViewChangeProposal])
                    {
                        cb(clusterStatusChange);
                    }

                    if (_fastPaxosInstance != null)
                    {
                        proposeTask = _fastPaxosInstance.ProposeAsync(proposals, cancellationToken);
                    }
                }
            }

            if (proposeTask != null)
            {
                await proposeTask.ConfigureAwait(false);
            }

            tcs.SetResult(RapidUtils.ToRapidResponse(new ConsensusResponse()));
        }).ConfigureAwait(false);

        return await tcs.Task.ConfigureAwait(false);
    }

    private async Task<RapidResponse> HandleConsensusMessagesAsync(RapidRequest request, CancellationToken cancellationToken)
    {
        if (_fastPaxosInstance != null)
        {
            return await _fastPaxosInstance.HandleMessagesAsync(request, cancellationToken).ConfigureAwait(false);
        }
        return RapidUtils.ToRapidResponse(new ConsensusResponse());
    }

    private async Task<RapidResponse> HandleLeaveMessageAsync(RapidRequest request, CancellationToken cancellationToken)
    {
        var leaveMessage = request.LeaveMessage;
        LogReceivedLeaveMessage(new LoggableEndpoint(leaveMessage.Sender), new LoggableEndpoint(_myAddr));
        EdgeFailureNotification(leaveMessage.Sender, _membershipView.GetCurrentConfigurationId());
        return RapidUtils.ToRapidResponse(new ConsensusResponse());
    }

    private static async Task<RapidResponse> HandleProbeMessage(ProbeMessage probeMessage, CancellationToken cancellationToken) => RapidUtils.ToRapidResponse(new ProbeResponse());

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
                LogRemovingNode(new LoggableEndpoint(node));
                _membershipView.RingDelete(node);
            }
            else
            {
                if (!_joinerUuid.TryGetValue(node, out var nodeId))
                {
                    LogDecidedNodeWithoutUuid(new LoggableEndpoint(node));
                    continue;
                }

                var metadata = _joinerMetadata.GetValueOrDefault(node, new Metadata());

                LogAddingNode(new LoggableEndpoint(node));
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

                    var rapidResponse = RapidUtils.ToRapidResponse(response);

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
                                          _broadcaster, DecideViewChange, _protocolOptions, _sharedResources);

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

    public void RegisterSubscription(ClusterEvents evt, Action<ClusterStatusChange> callback) => _subscriptions[evt].Add(callback);

    public List<Endpoint> GetMembershipView() => _membershipView.GetRing(0);

    public int GetMembershipSize() => _membershipView.GetMembershipSize();

    public Dictionary<Endpoint, Metadata> GetMetadata() => new Dictionary<Endpoint, Metadata>(_metadataManager.GetAllMetadata());

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
        var leave = RapidUtils.ToRapidRequest(leaveMessage);

        try
        {
            var observers = _membershipView.GetObserversOf(_myAddr);
            LogLeavingWithObservers(new LoggableEndpoint(_myAddr), observers.Count, new LoggableEndpoints(observers));

            var tasks = observers.Select(endpoint =>
                _messagingClient.SendMessageBestEffortAsync(endpoint, leave, CancellationToken.None).WithDefaultOnException());

            using var timeoutCts = new CancellationTokenSource(_options.LeaveMessageTimeout);
            try
            {
                await Task.WhenAll(tasks).WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                LogTimeoutWhileLeaving();
            }
            catch (Exception ex)
            {
                LogExceptionWhileLeaving(ex);
                throw;
            }
        }
        catch (Exception)
        {
            LogNodeAlreadyRemoved();
            throw;
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
                await Task.Delay(_options.BatchingWindow, _sharedResources.TimeProvider, _shutdownCts.Token).ConfigureAwait(false);

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

                        var request = RapidUtils.ToRapidRequest(batchedMessage);
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

    private static bool FilterAlertMessages(BatchedAlertMessage batchedAlertMessage, long currentConfigurationId) => batchedAlertMessage.Messages.Any(m => m.ConfigurationId == currentConfigurationId);

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

        for (var i = 0; i < subjects.Count; i++)
        {
            var subject = subjects[i];
            var ringNumber = i;
            var fd = _fdFactory.CreateInstance(subject, () => EdgeFailureNotification(subject, configurationId));

            fd.Start();
            _failureDetectors.Add(fd);
        }
    }

    private void EdgeFailureNotification(Endpoint subject, long configurationId)
    {
        _sharedResources.ScheduleCallback(async () =>
        {
            try
            {
                if (configurationId != _membershipView.GetCurrentConfigurationId())
                {
                    LogIgnoringOldConfigNotification(new LoggableEndpoint(subject), new CurrentConfigId(_membershipView), configurationId);
                    return;
                }

                LogAnnouncingEdgeFail(new LoggableEndpoint(subject), new LoggableEndpoint(_myAddr), configurationId, new MembershipSize(_membershipView));

                var ringNumbers = _membershipView.GetRingNumbers(_myAddr, subject);

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
            catch (Exception ex)
            {
                LogErrorInEdgeFailureNotification(ex, new LoggableEndpoint(subject));
                throw;
            }
        });
    }

    public void Dispose()
    {
        _shutdownCts.Dispose();
        _membershipView.Dispose();
        _fastPaxosInstance?.Dispose();
        foreach (var fd in _failureDetectors)
        {
            fd.Dispose();
        }
    }
}
