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
///
/// Note: This class is not thread-safe yet. RpcServer.start() uses a single threaded messagingExecutor during the server
/// initialization to make sure that only a single thread runs the process* methods.
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

    // Event subscriptions
    private readonly Dictionary<ClusterEvents, List<Action<ClusterStatusChange>>> _subscriptions;

    //
    private FastPaxos? _fastPaxosInstance;

    // Fields used by batching logic.
    private readonly Channel<AlertMessage> _sendQueue;
    private readonly Lock _batchSchedulerLock = new();
    private readonly SharedResources _sharedResources;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly List<IDisposable> _failureDetectors = [];

    // Failure detector
    private readonly IEdgeFailureDetectorFactory _fdFactory;

    // Fields used by consensus protocol
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
        // this::edgeFailureNotification is invoked by the failure detector whenever an edge
        // to an observer is marked faulty.

        // Prepare consensus instance
        _fastPaxosInstance = new FastPaxos(_myAddr, _membershipView.GetCurrentConfigurationId(),
                                          _membershipView.GetMembershipSize(), _messagingClient,
                                          _broadcaster,
                                          _protocolOptions, _sharedResources, loggerFactory);
        _fastPaxosInstance.Decided.ContinueWith(t => DecideViewChange(t.Result), scheduler: TaskScheduler.Default);

        CreateFailureDetectorsForCurrentConfiguration();

        // Execute all VIEW_CHANGE callbacks. This informs applications that a start/join has successfully completed.
        var configurationId = _membershipView.GetCurrentConfigurationId();
        var currentMembership = _membershipView.GetRing(0);
        var nodeStatusChanges = GetInitialViewChange();
        var clusterStatusChange = new ClusterStatusChange(configurationId, currentMembership, nodeStatusChanges);

        foreach (var cb in _subscriptions[ClusterEvents.ViewChange])
        {
            cb(clusterStatusChange);
        }
    }

    /// <summary>
    /// Entry point for all messages.
    /// </summary>
    public async Task<RapidResponse> HandleMessageAsync(RapidRequest msg, CancellationToken cancellationToken)
    {
        return msg.ContentCase switch
        {
            RapidRequest.ContentOneofCase.PreJoinMessage => HandlePreJoinMessage(msg.PreJoinMessage, cancellationToken),
            RapidRequest.ContentOneofCase.JoinMessage => await HandleJoinMessageAsync(msg.JoinMessage, cancellationToken).ConfigureAwait(false),
            RapidRequest.ContentOneofCase.BatchedAlertMessage => HandleBatchedAlertMessage(msg.BatchedAlertMessage, cancellationToken),
            RapidRequest.ContentOneofCase.ProbeMessage => HandleProbeMessage(msg.ProbeMessage, cancellationToken),
            RapidRequest.ContentOneofCase.FastRoundPhase2BMessage or
            RapidRequest.ContentOneofCase.Phase1AMessage or
            RapidRequest.ContentOneofCase.Phase1BMessage or
            RapidRequest.ContentOneofCase.Phase2AMessage or
            RapidRequest.ContentOneofCase.Phase2BMessage => HandleConsensusMessages(msg, cancellationToken),
            RapidRequest.ContentOneofCase.LeaveMessage => HandleLeaveMessage(msg, cancellationToken),
            _ => throw new ArgumentException($"Unidentified RapidRequest type {msg.ContentCase}")
        };
    }

    /// <summary>
    /// This is invoked by a new node joining the network at a seed node.
    /// The seed responds with the current configuration ID and a list of observers
    /// for the joiner, who then moves on to phase 2 of the protocol with its observers.
    /// </summary>
    private RapidResponse HandlePreJoinMessage(PreJoinMessage msg, CancellationToken cancellationToken)
    {
        lock (_membershipUpdateLock)
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

            return RapidUtils.ToRapidResponse(builder);
        }
    }

    /// <summary>
    /// Invoked by gatekeepers of a joining node. They perform any failure checking
    /// required before propagating a AlertMessage with the status UP. After the cut detection
    /// and full agreement succeeds, the observer informs the joiner about the new configuration it
    /// is now a part of.
    /// </summary>
    private async Task<RapidResponse> HandleJoinMessageAsync(JoinMessage joinMessage, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<RapidResponse>();

        lock (_membershipUpdateLock)
        {
            var currentConfiguration = _membershipView.GetCurrentConfigurationId();

            if (currentConfiguration == joinMessage.ConfigurationId)
            {
                LogEnqueueingSafeToJoin(new LoggableEndpoint(joinMessage.Sender), new CurrentConfigId(_membershipView),
                    new MembershipSize(_membershipView));

                ref var channel = ref CollectionsMarshal.GetValueRefOrAddDefault(_joinersToRespondTo, joinMessage.Sender, out var _);
                channel ??= Channel.CreateUnbounded<TaskCompletionSource<RapidResponse>>();
                channel.Writer.TryWrite(tcs);

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
                // This handles the corner case where the configuration changed between phase 1 and phase 2
                // of the joining node's bootstrap. It should attempt to rejoin the network.
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
                    // Race condition where a observer already crossed H messages for the joiner and changed
                    // the configuration, but the JoinPhase2 messages show up at the observer
                    // after it has already added the joiner. In this case, we simply
                    // tell the sender that they're safe to join.
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
        }

        return await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// This method receives edge update events and delivers them to
    /// the cut detector to check if it will return a valid
    /// proposal.
    ///
    /// Edge update messages that do not affect an ongoing proposal
    /// needs to be dropped.
    /// </summary>
    private RapidResponse HandleBatchedAlertMessage(BatchedAlertMessage messageBatch, CancellationToken cancellationToken)
    {
        lock (_membershipUpdateLock)
        {
            if (!FilterAlertMessages(messageBatch, _membershipView.GetCurrentConfigurationId()))
            {
                return RapidUtils.ToRapidResponse(new ConsensusResponse());
            }

            // We already have a proposal for this round
            // => we have initiated consensus and cannot go back on our proposal.
            var proposals = new List<Endpoint>();
            foreach (var msg in messageBatch.Messages)
            {
                // For valid UP alerts, extract the joiner details (UUID and metadata) which is going to be needed
                // when the node is added to the rings
                var extractedMessage = ExtractJoinerUuidAndMetadata(msg);
                proposals.AddRange(_cutDetection.AggregateForProposal(extractedMessage));
            }

            // Lastly, we apply implicit detections
            proposals.AddRange(_cutDetection.InvalidateFailingEdges(_membershipView));

            // If we have a proposal for this stage, start an instance of consensus on it.
            lock (_membershipUpdateLock)
            {
                if (proposals.Count > 0 && !_announcedProposal)
                {
                    _announcedProposal = true;
                    var currentConfigurationId = _membershipView.GetCurrentConfigurationId();
                    LogInitiatingConsensus(new LoggableEndpoints(proposals));

                    // Inform subscribers that a proposal has been announced.
                    var nodeStatusChanges = CreateNodeStatusChangeList(proposals);
                    var currentMembership = _membershipView.GetRing(0);
                    var clusterStatusChange = new ClusterStatusChange(currentConfigurationId, currentMembership, nodeStatusChanges);

                    foreach (var cb in _subscriptions[ClusterEvents.ViewChangeProposal])
                    {
                        cb(clusterStatusChange);
                    }

                    _fastPaxosInstance?.Propose(proposals, cancellationToken);
                }
            }

            return RapidUtils.ToRapidResponse(new ConsensusResponse());
        }
    }

    /// <summary>
    /// Receives proposal for the one-step consensus (essentially phase 2 of Fast Paxos).
    ///
    /// XXX: Implement recovery for the extremely rare possibility of conflicting proposals.
    /// </summary>
    private RapidResponse HandleConsensusMessages(RapidRequest request, CancellationToken cancellationToken)
    {
        _fastPaxosInstance?.HandleMessages(request, cancellationToken);
        return RapidUtils.ToRapidResponse(new ConsensusResponse());
    }

    /// <summary>
    /// Propagates the intent of a node to leave the group
    /// </summary>
    private RapidResponse HandleLeaveMessage(RapidRequest request, CancellationToken cancellationToken)
    {
        var leaveMessage = request.LeaveMessage;
        LogReceivedLeaveMessage(new LoggableEndpoint(leaveMessage.Sender), new LoggableEndpoint(_myAddr));
        EdgeFailureNotification(leaveMessage.Sender, _membershipView.GetCurrentConfigurationId());
        return RapidUtils.ToRapidResponse(new ConsensusResponse());
    }

    /// <summary>
    /// Invoked by observers of a node for failure detection.
    /// </summary>
    private static RapidResponse HandleProbeMessage(ProbeMessage probeMessage, CancellationToken cancellationToken) => RapidUtils.ToRapidResponse(new ProbeResponse());

    /// <summary>
    /// This is invoked by FastPaxos modules when they arrive at a decision.
    ///
    /// Any node that is not in the membership list will be added to the cluster,
    /// and any node that is currently in the membership list will be removed from it.
    /// </summary>
    private void DecideViewChange(List<Endpoint> proposal)
    {
        lock (_membershipUpdateLock)
        {
            _announcedProposal = false;
        }

        foreach (var node in proposal)
        {
            // If the node is already in the ring, remove it. Else, add it.
            // XXX: Maybe there's a cleaner way to do this in the future because
            // this ties us to just two states a node can be in.
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

                // Send new configuration to all nodes joining through us
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

        // Clear data structures for the next round.
        _cutDetection.Clear();
        _broadcaster.SetMembership(_membershipView.GetRing(0));

        // Recreate failure detectors
        foreach (var fd in _failureDetectors)
        {
            fd.Dispose();
        }
        _failureDetectors.Clear();

        _fastPaxosInstance = new FastPaxos(
            _myAddr,
            _membershipView.GetCurrentConfigurationId(),
            _membershipView.GetMembershipSize(),
            _messagingClient,
            _broadcaster,
            _protocolOptions,
            _sharedResources);
        _fastPaxosInstance.Decided.ContinueWith(t => DecideViewChange(t.Result), scheduler: TaskScheduler.Default);

        // Inform EdgeFailureDetector about membership change
        CreateFailureDetectorsForCurrentConfiguration();

        // Publish an event to the listeners.
        var configurationId = _membershipView.GetCurrentConfigurationId();
        var currentMembership = _membershipView.GetRing(0);
        var nodeStatusChanges = CreateNodeStatusChangeList(proposal);
        var clusterStatusChange = new ClusterStatusChange(configurationId, currentMembership, nodeStatusChanges);

        foreach (var cb in _subscriptions[ClusterEvents.ViewChange])
        {
            cb(clusterStatusChange);
        }
    }

    /// <summary>
    /// Invoked by subscribers waiting for event notifications.
    /// </summary>
    /// <param name="evt">Cluster event to subscribe to</param>
    /// <param name="callback">Callback to be executed when <paramref name="evt"/> occurs.</param>
    public void RegisterSubscription(ClusterEvents evt, Action<ClusterStatusChange> callback) => _subscriptions[evt].Add(callback);

    /// <summary>
    /// Gets the list of endpoints currently in the membership view.
    /// </summary>
    /// <returns>list of endpoints in the membership view</returns>
    public List<Endpoint> GetMembershipView() => _membershipView.GetRing(0);

    /// <summary>
    /// Gets the list of endpoints currently in the membership view.
    /// </summary>
    /// <returns>list of endpoints in the membership view</returns>
    public int GetMembershipSize() => _membershipView.GetMembershipSize();

    /// <summary>
    /// Gets the list of endpoints currently in the membership view.
    /// </summary>
    /// <returns>list of endpoints in the membership view</returns>
    public Dictionary<Endpoint, Metadata> GetMetadata() => new Dictionary<Endpoint, Metadata>(_metadataManager.GetAllMetadata());

    /// <summary>
    /// Shuts down all the executors.
    /// </summary>
    public void Shutdown()
    {
        _shutdownCts.Cancel();
        foreach (var fd in _failureDetectors)
        {
            fd.Dispose();
        }
        _failureDetectors.Clear();
    }

    /// <summary>
    /// Leaves the cluster by telling all the observers to proactively trigger failure.
    /// This operation is blocking, as we need to wait to send the alert messages before shutting down the rest
    /// </summary>
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
            // we already were removed, so that's fine
            LogNodeAlreadyRemoved();
            throw;
        }
    }

    /// <summary>
    /// Queues a AlertMessage to be broadcasted after potentially being batched.
    /// </summary>
    /// <param name="msg">the AlertMessage to be broadcasted</param>
    private void EnqueueAlertMessage(AlertMessage msg)
    {
        lock (_batchSchedulerLock)
        {
            _sendQueue.Writer.TryWrite(msg);
        }
    }

    /// <summary>
    /// Batches outgoing AlertMessages into a single BatchAlertMessage.
    /// </summary>
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
                        _broadcaster.Broadcast(request, _shutdownCts.Token);

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

    /// <summary>
    /// A filter for removing invalid edge update messages. These include messages that were for a
    /// configuration that the current node is not a part of, and messages that violate the semantics
    /// of a node being a part of a configuration.
    /// </summary>
    private static bool FilterAlertMessages(BatchedAlertMessage batchedAlertMessage, long currentConfigurationId) => batchedAlertMessage.Messages.Any(m => m.ConfigurationId == currentConfigurationId);

    private AlertMessage ExtractJoinerUuidAndMetadata(AlertMessage alertMessage)
    {
        if (alertMessage.EdgeStatus == EdgeStatus.Up && alertMessage.NodeId != null)
        {
            // Both the UUID and Metadata are saved only after the node is done being added.
            _joinerUuid[alertMessage.EdgeDst] = alertMessage.NodeId;
            _joinerMetadata[alertMessage.EdgeDst] = alertMessage.Metadata;
        }
        return alertMessage;
    }

    /// <summary>
    /// Formats a proposal or a view change for application subscriptions.
    /// </summary>
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

    /// <summary>
    /// Prepares a view change notification for a node that has just become part of a cluster. This is invoked when the
    /// membership service is first initialized by a new node, which only happens on a Cluster.join() or Cluster.start().
    /// Therefore, all EdgeStatus values will be UP.
    /// </summary>
    private List<NodeStatusChange> GetInitialViewChange()
    {
        var list = new List<NodeStatusChange>();
        foreach (var node in _membershipView.GetRing(0))
        {
            list.Add(new NodeStatusChange(node, EdgeStatus.Up, _metadataManager.Get(node) ?? new Metadata()));
        }
        return list;
    }

    /// <summary>
    /// Creates and schedules failure detector instances based on the fdFactory instance.
    /// </summary>
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

    /// <summary>
    /// This is a notification from a local edge failure detector at an observer. This changes
    /// the status of the edge between the observer and the subject to DOWN.
    /// </summary>
    /// <param name="subject">The subject that has failed.</param>
    /// <param name="configurationId">Configuration ID when the failure was detected</param>
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
                // Note: setUuid is deliberately missing here because it does not affect leaves.
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
