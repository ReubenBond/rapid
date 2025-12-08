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
internal sealed partial class MembershipService : IMembershipServiceHandler, IAsyncDisposable, IDisposable
{
    private readonly ILogger<MembershipService> _logger;
    private readonly MultiNodeCutDetector _cutDetection;
    private readonly Endpoint _myAddr;
    private readonly IBroadcaster _broadcaster;
    private readonly Dictionary<Endpoint, Channel<TaskCompletionSource<RapidResponse>>> _joinersToRespondTo = [];
    private readonly Dictionary<Endpoint, NodeId> _joinerUuid = [];
    private readonly Dictionary<Endpoint, Metadata> _joinerMetadata = [];
    private readonly IMessagingClient _messagingClient;
    private readonly MetadataManager _metadataManager;
    private readonly IFastPaxosFactory _fastPaxosFactory;
    private MembershipView _membershipView;

    // Event subscriptions
    private readonly Dictionary<ClusterEvents, List<Action<ClusterStatusChange>>> _subscriptions;

    // Fields used by batching logic.
    private readonly Channel<AlertMessage> _sendQueue;
    private readonly SharedResources _sharedResources;
    private readonly List<IDisposable> _failureDetectors = [];
    private int _disposed;

    // Failure detector
    private readonly IEdgeFailureDetectorFactory _fdFactory;

    // Fields used by consensus protocol
    private readonly Lock _membershipUpdateLock = new();
    private readonly RapidProtocolOptions _options;
    private bool _announcedProposal;
    private FastPaxos? _fastPaxosInstance;

    // View change accessor for publishing updates
    private readonly MembershipViewAccessor _viewAccessor;

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
        public override readonly string ToString() => _view.ConfigurationId.ToString();
    }

    private readonly struct MembershipSize(MembershipView view)
    {
        private readonly MembershipView _view = view;
        public override readonly string ToString() => _view.Size.ToString();
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

    [LoggerMessage(Level = LogLevel.Debug, Message = "MembershipService initialized: myAddr={MyAddr}, configId={ConfigId}, membershipSize={MembershipSize}")]
    private partial void LogMembershipServiceInitialized(LoggableEndpoint MyAddr, CurrentConfigId ConfigId, MembershipSize MembershipSize);

    [LoggerMessage(Level = LogLevel.Debug, Message = "HandleMessageAsync: received {MessageType} from request")]
    private partial void LogHandleMessageReceived(RapidRequest.ContentOneofCase MessageType);

    [LoggerMessage(Level = LogLevel.Debug, Message = "HandlePreJoinMessage: joiner={Joiner}, statusCode={StatusCode}, observers count={ObserversCount}")]
    private partial void LogHandlePreJoinResult(LoggableEndpoint Joiner, JoinStatusCode StatusCode, int ObserversCount);

    [LoggerMessage(Level = LogLevel.Debug, Message = "HandleJoinMessageAsync: processing join from {Sender}, configId={ConfigId}")]
    private partial void LogHandleJoinMessage(LoggableEndpoint Sender, long ConfigId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "HandleJoinMessageAsync: joiner already in ring, responding SAFE_TO_JOIN")]
    private partial void LogJoinerAlreadyInRing();

    [LoggerMessage(Level = LogLevel.Debug, Message = "HandleBatchedAlertMessage: received batch with {Count} messages from {Sender}")]
    private partial void LogHandleBatchedAlertMessage(int Count, LoggableEndpoint Sender);

    [LoggerMessage(Level = LogLevel.Debug, Message = "HandleBatchedAlertMessage: filtered out messages not matching current config {ConfigId}")]
    private partial void LogBatchedAlertFiltered(CurrentConfigId ConfigId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "HandleBatchedAlertMessage: processing alert edgeSrc={EdgeSrc}, edgeDst={EdgeDst}, status={Status}")]
    private partial void LogProcessingAlert(LoggableEndpoint EdgeSrc, LoggableEndpoint EdgeDst, EdgeStatus Status);

    [LoggerMessage(Level = LogLevel.Debug, Message = "HandleBatchedAlertMessage: cut detection returned {Count} proposals")]
    private partial void LogCutDetectionProposals(int Count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "HandleBatchedAlertMessage: implicit edge invalidation returned {Count} proposals")]
    private partial void LogImplicitEdgeInvalidation(int Count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "HandleConsensusMessages: forwarding to FastPaxos instance")]
    private partial void LogHandleConsensusMessages();

    [LoggerMessage(Level = LogLevel.Debug, Message = "HandleProbeMessage: responding to probe")]
    private partial void LogHandleProbeMessage();

    [LoggerMessage(Level = LogLevel.Debug, Message = "DecideViewChange: processing {Count} nodes in proposal")]
    private partial void LogDecideViewChange(int Count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "DecideViewChange: notifying {Count} joiners waiting through us for node {Node}")]
    private partial void LogNotifyingJoiners(int Count, LoggableEndpoint Node);

    [LoggerMessage(Level = LogLevel.Debug, Message = "DecideViewChange: cleared cut detection, updated broadcaster, recreated failure detectors")]
    private partial void LogDecideViewChangeCleanup();

    [LoggerMessage(Level = LogLevel.Debug, Message = "DecideViewChange: publishing VIEW_CHANGE event, configId={ConfigId}, membershipSize={MembershipSize}")]
    private partial void LogPublishingViewChange(CurrentConfigId ConfigId, MembershipSize MembershipSize);

    [LoggerMessage(Level = LogLevel.Debug, Message = "RegisterSubscription: registered callback for event {Event}")]
    private partial void LogRegisterSubscription(ClusterEvents Event);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Shutdown: cancelling background tasks and disposing failure detectors")]
    private partial void LogShutdown();

    [LoggerMessage(Level = LogLevel.Debug, Message = "EnqueueAlertMessage: queued alert edgeSrc={EdgeSrc}, edgeDst={EdgeDst}, status={Status}")]
    private partial void LogEnqueueAlertMessage(LoggableEndpoint EdgeSrc, LoggableEndpoint EdgeDst, EdgeStatus Status);

    [LoggerMessage(Level = LogLevel.Debug, Message = "AlertBatcherAsync: broadcasting batch with {Count} messages")]
    private partial void LogAlertBatcherBroadcast(int Count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "AlertBatcherAsync: exiting due to cancellation")]
    private partial void LogAlertBatcherExit();

    [LoggerMessage(Level = LogLevel.Debug, Message = "ExtractJoinerUuidAndMetadata: saved UUID and metadata for joiner {Joiner}")]
    private partial void LogExtractJoinerUuidAndMetadata(LoggableEndpoint Joiner);

    [LoggerMessage(Level = LogLevel.Debug, Message = "CreateFailureDetectorsForCurrentConfiguration: creating {Count} failure detectors for subjects")]
    private partial void LogCreateFailureDetectors(int Count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "CreateFailureDetectorsForCurrentConfiguration: created detector for subject {Subject}, ringNumber={RingNumber}")]
    private partial void LogCreatedFailureDetector(LoggableEndpoint Subject, int RingNumber);

    [LoggerMessage(Level = LogLevel.Debug, Message = "CreateFailureDetectorsForCurrentConfiguration: skipping, this node is no longer in the ring")]
    private partial void LogSkippingFailureDetectorsNotInRing();

    [LoggerMessage(Level = LogLevel.Warning, Message = "FastPaxos Decided task faulted")]
    private partial void LogFastPaxosDecidedFaulted(Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "EdgeFailureNotification: scheduling callback for subject {Subject}, configId={ConfigId}")]
    private partial void LogEdgeFailureNotificationScheduled(LoggableEndpoint Subject, long ConfigId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "EdgeFailureNotification: enqueueing DOWN alert for subject {Subject}, ringNumbers={RingNumbers}")]
    private partial void LogEdgeFailureNotificationEnqueued(LoggableEndpoint Subject, LoggableRingNumbers RingNumbers);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Dispose: disposing MembershipService resources")]
    private partial void LogDispose();

    [LoggerMessage(Level = LogLevel.Debug, Message = "FastPaxos decided continuation skipped due to shutdown")]
    private partial void LogFastPaxosDecidedSkippedShutdown();

    private readonly struct LoggableRingNumbers(IEnumerable<int> ringNumbers)
    {
        private readonly IEnumerable<int> _ringNumbers = ringNumbers;
        public override readonly string ToString() => string.Join(",", _ringNumbers);
    }

    public MembershipService(
        Endpoint myAddr,
        MultiNodeCutDetector cutDetection,
        MembershipView membershipView,
        SharedResources sharedResources,
        IOptions<RapidProtocolOptions> options,
        IMessagingClient messagingClient,
        IBroadcaster broadcaster,
        IEdgeFailureDetectorFactory edgeFailureDetector,
        IFastPaxosFactory fastPaxosFactory,
        MembershipViewAccessor viewAccessor,
        ILoggerFactory? loggerFactory = null)
        : this(myAddr, cutDetection, membershipView, sharedResources, options, messagingClient,
              broadcaster, edgeFailureDetector, fastPaxosFactory, viewAccessor, [],
              [], loggerFactory)
    {
    }

    public MembershipService(Endpoint myAddr, MultiNodeCutDetector cutDetection,
                            MembershipView membershipView, SharedResources sharedResources,
                            IOptions<RapidProtocolOptions> options, IMessagingClient messagingClient,
                            IBroadcaster broadcaster,
                            IEdgeFailureDetectorFactory edgeFailureDetector,
                            IFastPaxosFactory fastPaxosFactory,
                            MembershipViewAccessor viewAccessor,
                            Dictionary<Endpoint, Metadata> metadataMap,
                            Dictionary<ClusterEvents, List<Action<ClusterStatusChange>>> subscriptions,
                            ILoggerFactory? loggerFactory = null)
    {
        _myAddr = myAddr;
        _options = options.Value;
        _membershipView = membershipView;
        _cutDetection = cutDetection;
        _sharedResources = sharedResources;
        _metadataManager = new MetadataManager();
        _metadataManager.AddMetadata(metadataMap);
        _messagingClient = messagingClient;
        _broadcaster = broadcaster;
        _subscriptions = subscriptions;
        _fdFactory = edgeFailureDetector;
        _fastPaxosFactory = fastPaxosFactory;
        _viewAccessor = viewAccessor;
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
        var alertBatcherTask = Task.Factory.StartNew(AlertBatcherAsync, _sharedResources.ShuttingDownToken, TaskCreationOptions.None, _sharedResources.TaskScheduler).Unwrap();
        _sharedResources.TrackBackgroundTask(alertBatcherTask);

        _broadcaster.SetMembership([.. _membershipView.GetRing(0)]);
        // this::edgeFailureNotification is invoked by the failure detector whenever an edge
        // to an observer is marked faulty.

        // Prepare consensus instance
        _fastPaxosInstance = _fastPaxosFactory.Create(_myAddr, _membershipView.ConfigurationId,
                                          _membershipView.Size, _broadcaster);
        RegisterFastPaxosDecidedContinuation(_fastPaxosInstance);

        CreateFailureDetectorsForCurrentConfiguration();

        // Execute all VIEW_CHANGE callbacks. This informs applications that a start/join has successfully completed.
        var configurationId = _membershipView.ConfigurationId;
        var currentMembership = _membershipView.GetRing(0);
        var nodeStatusChanges = GetInitialViewChange();
        var clusterStatusChange = new ClusterStatusChange(configurationId, [.. currentMembership], nodeStatusChanges);

        foreach (var cb in _subscriptions[ClusterEvents.ViewChange])
        {
            cb(clusterStatusChange);
        }

        // Publish the initial view to the accessor
        _viewAccessor.PublishView(_membershipView);

        LogMembershipServiceInitialized(new LoggableEndpoint(myAddr), new CurrentConfigId(_membershipView), new MembershipSize(_membershipView));
    }

    /// <summary>
    /// Entry point for all messages.
    /// </summary>
    public async Task<RapidResponse> HandleMessageAsync(RapidRequest msg, CancellationToken cancellationToken)
    {
        LogHandleMessageReceived(msg.ContentCase);

        return msg.ContentCase switch
        {
            RapidRequest.ContentOneofCase.PreJoinMessage => HandlePreJoinMessage(msg.PreJoinMessage, cancellationToken),
            RapidRequest.ContentOneofCase.JoinMessage => await HandleJoinMessageAsync(msg.JoinMessage, cancellationToken).ConfigureAwait(true),
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
                ConfigurationId = _membershipView.ConfigurationId,
                StatusCode = statusCode
            };

            LogJoinAtSeed(new LoggableEndpoint(_myAddr), new LoggableEndpoint(msg.Sender),
                new CurrentConfigId(_membershipView), new MembershipSize(_membershipView));

            var observersCount = 0;
            if (statusCode == JoinStatusCode.SafeToJoin || statusCode == JoinStatusCode.HostnameAlreadyInRing)
            {
                var observers = _membershipView.GetExpectedObserversOf(joiningEndpoint);
                builder.Endpoints.AddRange(observers);
                observersCount = observers.Length;
            }

            LogHandlePreJoinResult(new LoggableEndpoint(joiningEndpoint), statusCode, observersCount);

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

        LogHandleJoinMessage(new LoggableEndpoint(joinMessage.Sender), joinMessage.ConfigurationId);

        lock (_membershipUpdateLock)
        {
            var currentConfiguration = _membershipView.ConfigurationId;

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
                var configuration = _membershipView.Configuration;
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
                    LogJoinerAlreadyInRing();
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

        return await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(true);
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
        LogHandleBatchedAlertMessage(messageBatch.Messages.Count, new LoggableEndpoint(messageBatch.Sender));

        lock (_membershipUpdateLock)
        {
            if (!FilterAlertMessages(messageBatch, _membershipView.ConfigurationId))
            {
                LogBatchedAlertFiltered(new CurrentConfigId(_membershipView));
                return RapidUtils.ToRapidResponse(new ConsensusResponse());
            }

            // We already have a proposal for this round
            // => we have initiated consensus and cannot go back on our proposal.
            var proposals = new List<Endpoint>();
            foreach (var msg in messageBatch.Messages)
            {
                LogProcessingAlert(new LoggableEndpoint(msg.EdgeSrc), new LoggableEndpoint(msg.EdgeDst), msg.EdgeStatus);
                // For valid UP alerts, extract the joiner details (UUID and metadata) which is going to be needed
                // when the node is added to the rings
                var extractedMessage = ExtractJoinerUuidAndMetadata(msg);
                var cutProposals = _cutDetection.AggregateForProposal(extractedMessage);
                LogCutDetectionProposals(cutProposals.Count);
                proposals.AddRange(cutProposals);
            }

            // Lastly, we apply implicit detections
            var implicitProposals = _cutDetection.InvalidateFailingEdges(_membershipView);
            LogImplicitEdgeInvalidation(implicitProposals.Count);
            proposals.AddRange(implicitProposals);

            // If we have a proposal for this stage, start an instance of consensus on it.
            lock (_membershipUpdateLock)
            {
                if (proposals.Count > 0 && !_announcedProposal)
                {
                    _announcedProposal = true;
                    var currentConfigurationId = _membershipView.ConfigurationId;
                    LogInitiatingConsensus(new LoggableEndpoints(proposals));

                    // Inform subscribers that a proposal has been announced.
                    var nodeStatusChanges = CreateNodeStatusChangeList(proposals);
                    var currentMembership = _membershipView.GetRing(0);
                    var clusterStatusChange = new ClusterStatusChange(currentConfigurationId, [.. currentMembership], nodeStatusChanges);

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
        LogHandleConsensusMessages();
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
        EdgeFailureNotification(leaveMessage.Sender, _membershipView.ConfigurationId);
        return RapidUtils.ToRapidResponse(new ConsensusResponse());
    }

    /// <summary>
    /// Invoked by observers of a node for failure detection.
    /// </summary>
    private RapidResponse HandleProbeMessage(ProbeMessage probeMessage, CancellationToken cancellationToken)
    {
        LogHandleProbeMessage();
        return RapidUtils.ToRapidResponse(new ProbeResponse());
    }

    /// <summary>
    /// This is invoked by FastPaxos modules when they arrive at a decision.
    ///
    /// Any node that is not in the membership list will be added to the cluster,
    /// and any node that is currently in the membership list will be removed from it.
    /// </summary>
    private void DecideViewChange(List<Endpoint> proposal)
    {
        LogDecideViewChange(proposal.Count);

        lock (_membershipUpdateLock)
        {
            _announcedProposal = false;

            // Track nodes that were added so we can notify their joiners after ALL nodes are processed
            var addedNodes = new List<Endpoint>();

            // Create a builder from the current view to make modifications
            var builder = _membershipView.ToBuilder();

            foreach (var node in proposal)
            {
                // If the node is already in the ring, remove it. Else, add it.
                // XXX: Maybe there's a cleaner way to do this in the future because
                // this ties us to just two states a node can be in.
                if (_membershipView.IsHostPresent(node))
                {
                    LogRemovingNode(new LoggableEndpoint(node));
                    builder.RingDelete(node);
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
                    builder.RingAdd(node, nodeId);
                    _metadataManager.Add(node, metadata);

                    _joinerUuid.Remove(node);
                    _joinerMetadata.Remove(node);

                    // Track this node for later notification
                    addedNodes.Add(node);
                }
            }

            // Build the new immutable view
            _membershipView = builder.Build();

            // Publish the new view to the accessor
            _viewAccessor.PublishView(_membershipView);

            // Now that ALL nodes have been added, notify all joiners with the complete configuration
            foreach (var node in addedNodes)
            {
                if (_joinersToRespondTo.TryGetValue(node, out var channel))
                {
                    var waitingCount = 0;
                    var config = _membershipView.Configuration;
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
                        waitingCount++;
                        tcs.SetResult(rapidResponse);
                    }

                    LogNotifyingJoiners(waitingCount, new LoggableEndpoint(node));
                    _joinersToRespondTo.Remove(node);
                }
            }

            // Clear data structures for the next round.
            _cutDetection.Clear();
            _broadcaster.SetMembership([.. _membershipView.GetRing(0)]);

            // Recreate failure detectors
            foreach (var fd in _failureDetectors)
            {
                fd.Dispose();
            }
            _failureDetectors.Clear();

            LogDecideViewChangeCleanup();

            _fastPaxosInstance = _fastPaxosFactory.Create(
                _myAddr,
                _membershipView.ConfigurationId,
                _membershipView.Size,
                _broadcaster);
            RegisterFastPaxosDecidedContinuation(_fastPaxosInstance);

            // Inform EdgeFailureDetector about membership change
            CreateFailureDetectorsForCurrentConfiguration();

            // Publish an event to the listeners.
            var configurationId = _membershipView.ConfigurationId;
            var currentMembership = _membershipView.GetRing(0);
            var nodeStatusChanges = CreateNodeStatusChangeList(proposal);
            var clusterStatusChange = new ClusterStatusChange(configurationId, [.. currentMembership], nodeStatusChanges);

            LogPublishingViewChange(new CurrentConfigId(_membershipView), new MembershipSize(_membershipView));

            foreach (var cb in _subscriptions[ClusterEvents.ViewChange])
            {
                cb(clusterStatusChange);
            }
        }
    }

    /// <summary>
    /// Invoked by subscribers waiting for event notifications.
    /// </summary>
    /// <param name="evt">Cluster event to subscribe to</param>
    /// <param name="callback">Callback to be executed when <paramref name="evt"/> occurs.</param>
    public void RegisterSubscription(ClusterEvents evt, Action<ClusterStatusChange> callback)
    {
        LogRegisterSubscription(evt);
        _subscriptions[evt].Add(callback);
    }

    /// <summary>
    /// Gets the list of endpoints currently in the membership view.
    /// </summary>
    /// <returns>list of endpoints in the membership view</returns>
    public List<Endpoint> GetMembershipView() => [.. _membershipView.GetRing(0)];

    /// <summary>
    /// Gets the list of endpoints currently in the membership view.
    /// </summary>
    /// <returns>list of endpoints in the membership view</returns>
    public int GetMembershipSize() => _membershipView.Size;

    /// <summary>
    /// Gets the list of endpoints currently in the membership view.
    /// </summary>
    /// <returns>list of endpoints in the membership view</returns>
    public Dictionary<Endpoint, Metadata> GetMetadata() => new(_metadataManager.GetAllMetadata());

    /// <summary>
    /// Shuts down all the executors.
    /// </summary>
    public void Shutdown()
    {
        LogShutdown();
        _viewAccessor.Complete();
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
            LogLeavingWithObservers(new LoggableEndpoint(_myAddr), observers.Length, new LoggableEndpoints(observers));

            var tasks = observers.Select(endpoint =>
                _messagingClient.SendMessageBestEffortAsync(endpoint, leave, CancellationToken.None));

            try
            {
                await Task.WhenAll(tasks).WaitAsync(_options.LeaveMessageTimeout, _sharedResources.TimeProvider).ConfigureAwait(true);
            }
            catch (TimeoutException)
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
        LogEnqueueAlertMessage(new LoggableEndpoint(msg.EdgeSrc), new LoggableEndpoint(msg.EdgeDst), msg.EdgeStatus);
        _sendQueue.Writer.TryWrite(msg);
    }

    /// <summary>
    /// Batches outgoing AlertMessages into a single BatchAlertMessage.
    /// </summary>
    private async Task AlertBatcherAsync()
    {
        var buffer = new List<AlertMessage>();
        var shutdownToken = _sharedResources.ShuttingDownToken;

        while (!shutdownToken.IsCancellationRequested)
        {
            buffer.Clear();
            try
            {
                await Task.Delay(_options.BatchingWindow, _sharedResources.TimeProvider, shutdownToken).ConfigureAwait(true);
                await _sendQueue.Reader.WaitToReadAsync(shutdownToken);
                while (_sendQueue.Reader.TryRead(out var msg))
                {
                    buffer.Add(msg);
                }

                if (buffer.Count > 0)
                {
                    LogAlertBatcherBroadcast(buffer.Count);

                    var batchedMessage = new BatchedAlertMessage
                    {
                        Sender = _myAddr
                    };
                    batchedMessage.Messages.AddRange(buffer);

                    var request = RapidUtils.ToRapidRequest(batchedMessage);
                    _broadcaster.Broadcast(request, shutdownToken);
                }
            }
            catch (OperationCanceledException)
            {
                LogAlertBatcherExit();
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
            LogExtractJoinerUuidAndMetadata(new LoggableEndpoint(alertMessage.EdgeDst));
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
        // Check if this node is still in the ring - it may have been removed during a view change
        if (!_membershipView.IsHostPresent(_myAddr))
        {
            LogSkippingFailureDetectorsNotInRing();
            return;
        }

        var subjects = _membershipView.GetSubjectsOf(_myAddr);
        var configurationId = _membershipView.ConfigurationId;

        LogCreateFailureDetectors(subjects.Length);

        for (var i = 0; i < subjects.Length; i++)
        {
            var subject = subjects[i];
            var ringNumber = i;
            var fd = _fdFactory.CreateInstance(subject, () => EdgeFailureNotification(subject, configurationId));

            LogCreatedFailureDetector(new LoggableEndpoint(subject), ringNumber);

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
        LogEdgeFailureNotificationScheduled(new LoggableEndpoint(subject), configurationId);

        try
        {
            if (configurationId != _membershipView.ConfigurationId)
            {
                LogIgnoringOldConfigNotification(new LoggableEndpoint(subject), new CurrentConfigId(_membershipView), configurationId);
                return;
            }

            LogAnnouncingEdgeFail(new LoggableEndpoint(subject), new LoggableEndpoint(_myAddr), configurationId, new MembershipSize(_membershipView));

            var ringNumbers = _membershipView.GetRingNumbers(_myAddr, subject);
            LogEdgeFailureNotificationEnqueued(new LoggableEndpoint(subject), new LoggableRingNumbers(ringNumbers));

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
    }

    /// <summary>
    /// Registers a continuation on FastPaxos.Decided that handles the result and checks for shutdown.
    /// The continuation is tracked as a background task to ensure proper cleanup during shutdown.
    /// </summary>
    private void RegisterFastPaxosDecidedContinuation(FastPaxos fastPaxosInstance)
    {
        var continuationTask = fastPaxosInstance.Decided.ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                LogFastPaxosDecidedFaulted(t.Exception!);
                return;
            }

            DecideViewChange(t.Result);
        }, CancellationToken.None, TaskContinuationOptions.None, _sharedResources.TaskScheduler);
    }

    /// <summary>
    /// Asynchronously disposes the membership service, waiting for background tasks to complete.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return; // Already disposed
        }

        LogDispose();
        Shutdown();

        // Wait for tracked background tasks via SharedResources
        // The SharedResources.WaitForBackgroundTasksAsync handles this
        await Task.CompletedTask.ConfigureAwait(false);

        _fastPaxosInstance?.Dispose();
    }

    /// <summary>
    /// Synchronously disposes the membership service.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return; // Already disposed
        }

        LogDispose();
        Shutdown();
        _fastPaxosInstance?.Dispose();
    }
}
