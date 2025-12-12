using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

using Microsoft.Extensions.Options;
using Rapid.Exceptions;
using Rapid.Logging;
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
internal sealed class MembershipService : IMembershipServiceHandler, IAsyncDisposable, IDisposable
{
    private readonly MembershipServiceLogger _log;
    private ICutDetector _cutDetection = null!;
    private readonly ICutDetectorFactory _cutDetectorFactory;
    private readonly Endpoint _myAddr;
    private readonly IBroadcaster _broadcaster;
    private readonly Dictionary<Endpoint, Channel<TaskCompletionSource<RapidResponse>>> _joinersToRespondTo = [];
    private readonly Dictionary<Endpoint, NodeId> _joinerUuid = [];
    private readonly Dictionary<Endpoint, Metadata> _joinerMetadata = [];
    private readonly IMessagingClient _messagingClient;
    private readonly MetadataManager _metadataManager;
    private readonly IConsensusCoordinatorFactory _consensusCoordinatorFactory;
    private MembershipView _membershipView = null!;

    // Event subscriptions (IAsyncEnumerable and IObservable-based using BroadcastChannel)
    private readonly BroadcastChannel<ClusterEventNotification> _eventChannel;

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
    private ConsensusCoordinator _consensusInstance = null!;

    // Initialization state
    private bool _initialized;

    // Configuration for join - stored from RapidOptions
    private readonly Endpoint? _seedAddress;
    private readonly Metadata _nodeMetadata;

    // Buffer for consensus messages from future configurations
    // Key: configurationId, Value: list of messages waiting for that config
    private readonly Dictionary<long, List<RapidRequest>> _pendingConsensusMessages = [];

    // View change accessor for publishing updates
    private readonly MembershipViewAccessor _viewAccessor;

    // Flag to track if a rejoin is in progress
    private bool _isRejoining;

    // Flag to track if a stale view refresh is in progress (to prevent concurrent refreshes)
    private bool _isRefreshingView;

    // Background task tracking for graceful shutdown
    private readonly List<Task> _backgroundTasks = [];
    private readonly Lock _backgroundTasksLock = new();

    // Internal cancellation for background tasks - linked to SharedResources.ShuttingDownToken
    // Cancelled by StopAsync or when SharedResources signals shutdown
    private readonly CancellationTokenSource _stoppingCts;

    /// <summary>
    /// Result of a single join attempt. Used to avoid exception-based control flow for retryable conditions.
    /// </summary>
    private enum JoinAttemptStatus
    {
        /// <summary>Join succeeded - response contains valid membership data.</summary>
        Success,
        /// <summary>Join failed but should be retried (transient error like config change, network issue).</summary>
        RetryNeeded,
        /// <summary>Join failed permanently - should not retry.</summary>
        Failed
    }

    /// <summary>
    /// Result of a single join attempt.
    /// </summary>
    private readonly struct JoinAttemptResult(JoinAttemptStatus status, JoinResponse? response, string? failureReason)
    {
        public JoinAttemptStatus Status { get; } = status;
        public JoinResponse? Response { get; } = response;
        public string? FailureReason { get; } = failureReason;

        public static JoinAttemptResult Success(JoinResponse response) => new(JoinAttemptStatus.Success, response, null);
        public static JoinAttemptResult RetryNeeded(string reason) => new(JoinAttemptStatus.RetryNeeded, null, reason);
        public static JoinAttemptResult Failed(string reason) => new(JoinAttemptStatus.Failed, null, reason);
    }

    /// <summary>
    /// Creates a new MembershipService instance.
    /// Call <see cref="InitializeAsync"/> after construction to start or join a cluster.
    /// </summary>
    public MembershipService(
        IOptions<RapidOptions> rapidOptions,
        IOptions<RapidProtocolOptions> protocolOptions,
        IMessagingClient messagingClient,
        IBroadcasterFactory broadcasterFactory,
        IEdgeFailureDetectorFactory edgeFailureDetector,
        IConsensusCoordinatorFactory consensusCoordinatorFactory,
        ICutDetectorFactory cutDetectorFactory,
        MembershipViewAccessor viewAccessor,
        SharedResources sharedResources,
        ILogger<MembershipService> logger)
    {
        var opts = rapidOptions.Value;
        _myAddr = opts.ListenAddress;
        _seedAddress = opts.SeedAddress;
        _nodeMetadata = opts.Metadata;
        _options = protocolOptions.Value;
        _membershipView = MembershipView.Empty;
        _cutDetectorFactory = cutDetectorFactory;
        _sharedResources = sharedResources;
        _metadataManager = new MetadataManager();
        _messagingClient = messagingClient;
        _broadcaster = broadcasterFactory.Create();
        _fdFactory = edgeFailureDetector;
        _consensusCoordinatorFactory = consensusCoordinatorFactory;
        _viewAccessor = viewAccessor;
        _log = new MembershipServiceLogger(logger);
        _sendQueue = Channel.CreateUnbounded<AlertMessage>();
        _eventChannel = new BroadcastChannel<ClusterEventNotification>();

        // Create linked CTS so background tasks stop on either StopAsync or SharedResources shutdown
        _stoppingCts = CancellationTokenSource.CreateLinkedTokenSource(sharedResources.ShuttingDownToken);

        // Configure the failure detector factory to detect stale views (learner role - missed consensus decisions)
        if (edgeFailureDetector is PingPongFailureDetectorFactory pingPongFactory)
        {
            pingPongFactory.OnStaleViewDetected = OnStaleViewDetected;
            pingPongFactory.GetLocalConfigurationId = () => _membershipView.ConfigurationId;
        }
    }

    /// <summary>
    /// Initializes the membership service by either starting a new cluster or joining an existing one.
    /// This must be called after construction before the service can handle messages.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            throw new InvalidOperationException("MembershipService is already initialized");
        }

        if (_seedAddress == null || _myAddr.Equals(_seedAddress))
        {
            // Start a new cluster
            StartNewCluster();
        }
        else
        {
            // Join an existing cluster
            await JoinClusterAsync(cancellationToken).ConfigureAwait(true);
        }

        // Start background jobs after initialization
        var alertBatcherTask = Task.Factory.StartNew(AlertBatcherAsync, _stoppingCts.Token, TaskCreationOptions.None, _sharedResources.TaskScheduler).Unwrap();
        TrackBackgroundTask(alertBatcherTask);

        _initialized = true;
    }

    /// <summary>
    /// Starts a new cluster with this node as the only member.
    /// </summary>
    private void StartNewCluster()
    {
        _log.StartingNewCluster(new MembershipServiceLogger.LoggableEndpoint(_myAddr));

        var nodeId = RapidUtils.NodeIdFromUuid(_sharedResources.NewGuid());
        _membershipView = new MembershipViewBuilder(_options.ObserversPerSubject, [nodeId], [_myAddr]).Build();
        _cutDetection = _cutDetectorFactory.Create(_membershipView);

        _metadataManager.Add(_myAddr, _nodeMetadata);

        FinalizeInitialization();
    }

    /// <summary>
    /// Joins an existing cluster through the configured seed node.
    /// </summary>
    private async Task JoinClusterAsync(CancellationToken cancellationToken)
    {
        _log.JoiningCluster(new MembershipServiceLogger.LoggableEndpoint(_seedAddress!), new MembershipServiceLogger.LoggableEndpoint(_myAddr));

        var maxRetries = _options.MaxJoinRetries;
        var retryDelay = _options.JoinRetryBaseDelay;
        JoinResponse? successfulResponse = null;
        string? lastFailureReason = null;

        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            var result = await TryJoinClusterAsync(cancellationToken).ConfigureAwait(true);

            switch (result.Status)
            {
                case JoinAttemptStatus.Success:
                    successfulResponse = result.Response;
                    break;

                case JoinAttemptStatus.RetryNeeded when attempt < maxRetries:
                    _log.JoinRetry(attempt + 1, result.FailureReason!, retryDelay.TotalMilliseconds);
                    await Task.Delay(retryDelay, _sharedResources.TimeProvider, cancellationToken).ConfigureAwait(true);
                    retryDelay = TimeSpan.FromTicks((long)(retryDelay.Ticks * _options.JoinRetryBackoffMultiplier));

                    // Cap at maximum delay
                    if (retryDelay > _options.JoinRetryMaxDelay)
                    {
                        retryDelay = _options.JoinRetryMaxDelay;
                    }
                    continue;

                case JoinAttemptStatus.RetryNeeded:
                    // Last attempt failed with retryable error - treat as failure
                    lastFailureReason = result.FailureReason;
                    break;

                case JoinAttemptStatus.Failed:
                    // Permanent failure - don't retry
                    throw new JoinException(result.FailureReason ?? "Join failed");
            }

            // Either succeeded or exhausted retries
            break;
        }

        if (successfulResponse == null)
        {
            _log.JoinFailed(maxRetries + 1);
            throw new JoinException(lastFailureReason ?? $"Failed to join cluster after {maxRetries + 1} attempts");
        }

        // Initialize membership from response
        var metadataMap = new Dictionary<Endpoint, Metadata>();
        for (var i = 0; i < successfulResponse.MetadataKeys.Count && i < successfulResponse.MetadataValues.Count; i++)
        {
            var endpoint = successfulResponse.MetadataKeys[i];
            var metadata = successfulResponse.MetadataValues[i];
            metadataMap[endpoint] = metadata;
        }

        _membershipView = new MembershipViewBuilder(
            _options.ObserversPerSubject,
            [.. successfulResponse.Identifiers],
            [.. successfulResponse.Endpoints]).BuildWithConfigurationId(new ConfigurationId(successfulResponse.ConfigurationId));
        _cutDetection = _cutDetectorFactory.Create(_membershipView);
        _metadataManager.AddMetadata(metadataMap);

        FinalizeInitialization();
    }

    /// <summary>
    /// Attempts a single join operation. Generates a new NodeId and handles UUID collisions internally.
    /// Returns a result indicating success, retry needed, or permanent failure.
    /// </summary>
    private async Task<JoinAttemptResult> TryJoinClusterAsync(CancellationToken cancellationToken)
    {
        var currentIdentifier = RapidUtils.NodeIdFromUuid(_sharedResources.NewGuid());

        // Phase 1: Contact seed for observers (with retry on UUID collision)
        JoinResponse joinResponse;
        while (true)
        {
            var preJoinMessage = new PreJoinMessage
            {
                Sender = _myAddr,
                NodeId = currentIdentifier
            };

            RapidResponse preJoinResponse;
            try
            {
                preJoinResponse = await _messagingClient.SendMessageAsync(
                    _seedAddress!,
                    preJoinMessage.ToRapidRequest(),
                    cancellationToken).ConfigureAwait(true);
            }
            catch (TimeoutException)
            {
                return JoinAttemptResult.RetryNeeded("Timeout contacting seed node");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Network errors are typically transient
                return JoinAttemptResult.RetryNeeded($"Network error contacting seed: {ex.Message}");
            }

            joinResponse = preJoinResponse.JoinResponse;

            if (joinResponse.StatusCode == JoinStatusCode.UuidAlreadyInRing)
            {
                // UUID collision - generate a new identifier and retry (matches Java behavior)
                currentIdentifier = RapidUtils.NodeIdFromUuid(_sharedResources.NewGuid());
                continue;
            }

            break;
        }

        if (joinResponse.StatusCode != JoinStatusCode.SafeToJoin &&
            joinResponse.StatusCode != JoinStatusCode.HostnameAlreadyInRing)
        {
            // Most status codes indicate permanent failure
            return JoinAttemptResult.Failed($"Join failed with status: {joinResponse.StatusCode}");
        }

        var observers = joinResponse.Endpoints.ToList();
        if (observers.Count == 0)
        {
            return JoinAttemptResult.Failed("No observers returned from seed");
        }

        // Phase 2: Contact observers
        var ringNumbersPerObserver = new Dictionary<Endpoint, List<int>>();
        for (var ringNumber = 0; ringNumber < observers.Count; ringNumber++)
        {
            var observer = observers[ringNumber];
            if (!ringNumbersPerObserver.TryGetValue(observer, out var value))
            {
                ringNumbersPerObserver[observer] = value = [];
            }
            ringNumbersPerObserver[observer].Add(ringNumber);
        }

        var tasks = ringNumbersPerObserver.Select(async entry =>
        {
            var joinMessageForObserver = new JoinMessage
            {
                Sender = _myAddr,
                NodeId = currentIdentifier,
                Metadata = _nodeMetadata,
                ConfigurationId = joinResponse.ConfigurationId
            };
            joinMessageForObserver.RingNumber.AddRange(entry.Value);

            return await _messagingClient.SendMessageAsync(
                entry.Key,
                joinMessageForObserver.ToRapidRequest(),
                cancellationToken).WithDefaultOnException().ConfigureAwait(true);
        });

        var responses = await Task.WhenAll(tasks).ConfigureAwait(true);
        var successfulResponse = responses.FirstOrDefault(r => r?.JoinResponse?.StatusCode == JoinStatusCode.SafeToJoin)?.JoinResponse;

        if (successfulResponse == null)
        {
            // Check if we got a ConfigChanged response - configuration changed during join, retry
            if (responses.Any(r => r?.JoinResponse?.StatusCode == JoinStatusCode.ConfigChanged))
            {
                return JoinAttemptResult.RetryNeeded("Configuration changed during join");
            }

            // No successful response from any observer - transient, should retry
            return JoinAttemptResult.RetryNeeded("Failed to get successful response from any observer");
        }

        return JoinAttemptResult.Success(successfulResponse);
    }

    /// <summary>
    /// Finalizes initialization after the membership view is established.
    /// Sets up broadcaster, consensus, failure detectors, and publishes initial view.
    /// </summary>
    private void FinalizeInitialization()
    {
        lock (_membershipUpdateLock)
        {
            // Get metadata map from the manager (was populated by StartNewCluster or JoinClusterAsync)
            var metadataMap = new Dictionary<Endpoint, Metadata>(_metadataManager.GetAllMetadata());

            // SetMembershipView handles all the setup - for initial join, nodeStatusChanges is null
            // which causes GetInitialViewChange() to be used (all nodes marked as Up)
            SetMembershipView(_membershipView, metadataMap, nodeStatusChanges: null, addedNodes: null);
        }

        _log.MembershipServiceInitialized(new MembershipServiceLogger.LoggableEndpoint(_myAddr), new MembershipServiceLogger.CurrentConfigId(_membershipView), new MembershipServiceLogger.MembershipSize(_membershipView));
    }

    /// <summary>
    /// Centralized method for updating the membership view. All view changes flow through here.
    /// This method handles:
    /// - Updating the membership view
    /// - Recreating the cut detector
    /// - Updating the broadcaster
    /// - Disposing old and creating new failure detectors  
    /// - Creating new consensus instance
    /// - Publishing the view to the accessor
    /// - Publishing VIEW_CHANGE event
    /// </summary>
    /// <param name="newView">The new membership view to apply.</param>
    /// <param name="metadataMap">Metadata for nodes in the view. If null, existing metadata is preserved.</param>
    /// <param name="nodeStatusChanges">The status changes to publish. If null, all nodes are treated as Up (initial join).</param>
    /// <param name="addedNodes">Nodes that were added (for notifying waiting joiners). Can be null.</param>
    /// <returns>The previous consensus instance that should be disposed by the caller.</returns>
    private ConsensusCoordinator? SetMembershipView(
        MembershipView newView,
        Dictionary<Endpoint, Metadata>? metadataMap,
        List<NodeStatusChange>? nodeStatusChanges,
        List<Endpoint>? addedNodes)
    {
        // Must be called under _membershipUpdateLock
        var previousConsensusInstance = _initialized ? _consensusInstance : null;

        // Update the view
        _membershipView = newView;

        // Update metadata if provided
        if (metadataMap != null)
        {
            _metadataManager.Clear();
            _metadataManager.AddMetadata(metadataMap);
        }

        // Recreate cut detector for the new cluster size
        _cutDetection = _cutDetectorFactory.Create(_membershipView);

        // Update broadcaster membership
        _broadcaster.SetMembership([.. _membershipView.GetRing(0)]);

        // Dispose old failure detectors and create new ones
        foreach (var fd in _failureDetectors)
        {
            fd.Dispose();
        }
        _failureDetectors.Clear();

        // Create new consensus instance
        _consensusInstance = _consensusCoordinatorFactory.Create(_myAddr, _membershipView.ConfigurationId, _membershipView.Size, _broadcaster);
        RegisterConsensusDecidedContinuation(_consensusInstance);
        _announcedProposal = false;

        // Replay any buffered consensus messages for this configuration
        ReplayBufferedConsensusMessages(_membershipView.ConfigurationId, _stoppingCts.Token);

        // Create new failure detectors
        CreateFailureDetectorsForCurrentConfiguration();

        // Notify waiting joiners if any nodes were added
        if (addedNodes != null)
        {
            NotifyWaitingJoiners(addedNodes);
        }

        // Publish the new view to the accessor
        _viewAccessor.PublishView(_membershipView);

        // Publish VIEW_CHANGE event
        var statusChanges = nodeStatusChanges ?? GetInitialViewChange();
        var currentMembership = _membershipView.GetRing(0);
        var clusterStatusChange = new ClusterStatusChange(_membershipView.ConfigurationId, [.. currentMembership], statusChanges);

            _log.PublishingViewChange(new MembershipServiceLogger.CurrentConfigId(_membershipView), new MembershipServiceLogger.MembershipSize(_membershipView));
        PublishEvent(ClusterEvents.ViewChange, clusterStatusChange);

        // Clear pending joiner data that's no longer needed
        _pendingConsensusMessages.Keys
            .Where(k => k < _membershipView.ConfigurationId)
            .ToList()
            .ForEach(k => _pendingConsensusMessages.Remove(k));

        return previousConsensusInstance;
    }

    /// <summary>
    /// Notifies joiners waiting for their join to complete.
    /// </summary>
    private void NotifyWaitingJoiners(List<Endpoint> addedNodes)
    {
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
                    ConfigurationId = _membershipView.ConfigurationId
                };
                response.Endpoints.AddRange(config.Endpoints);
                response.Identifiers.AddRange(config.NodeIds);
                var allMetadata = _metadataManager.GetAllMetadata();
                response.MetadataKeys.AddRange(allMetadata.Keys);
                response.MetadataValues.AddRange(allMetadata.Values);

                var rapidResponse = response.ToRapidResponse();

                // Send response to all waiting tasks
                while (channel.Reader.TryRead(out var tcs))
                {
                    waitingCount++;
                    tcs.SetResult(rapidResponse);
                }

                _log.NotifyingJoiners(waitingCount, new MembershipServiceLogger.LoggableEndpoint(node));
                _joinersToRespondTo.Remove(node);
            }
        }
    }

    /// <summary>
    /// Entry point for all messages.
    /// </summary>
    public async Task<RapidResponse> HandleMessageAsync(RapidRequest msg, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(msg);

        if (IsTraceMessage(msg.ContentCase))
        {
            _log.HandleMessageReceivedTrace(msg.ContentCase);
        }
        else
        {
            _log.HandleMessageReceivedDebug(msg.ContentCase);
        }

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
            RapidRequest.ContentOneofCase.MembershipViewRequest => HandleMembershipViewRequest(msg.MembershipViewRequest, cancellationToken),
            _ => throw new ArgumentException($"Unidentified RapidRequest type {msg.ContentCase}")
        };

        static bool IsTraceMessage(RapidRequest.ContentOneofCase contentCase)
            => contentCase == RapidRequest.ContentOneofCase.ProbeMessage;
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

            _log.JoinAtSeed(new MembershipServiceLogger.LoggableEndpoint(_myAddr), new MembershipServiceLogger.LoggableEndpoint(msg.Sender),
                new MembershipServiceLogger.CurrentConfigId(_membershipView), new MembershipServiceLogger.MembershipSize(_membershipView));

            var observersCount = 0;
            if (statusCode == JoinStatusCode.SafeToJoin || statusCode == JoinStatusCode.HostnameAlreadyInRing)
            {
                var observers = _membershipView.GetExpectedObserversOf(joiningEndpoint);
                builder.Endpoints.AddRange(observers);
                observersCount = observers.Length;
            }

            _log.HandlePreJoinResult(new MembershipServiceLogger.LoggableEndpoint(joiningEndpoint), statusCode, observersCount);

            return builder.ToRapidResponse();
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

        _log.HandleJoinMessage(new MembershipServiceLogger.LoggableEndpoint(joinMessage.Sender), joinMessage.ConfigurationId);

        lock (_membershipUpdateLock)
        {
            var currentConfiguration = _membershipView.ConfigurationId;

            if (currentConfiguration == joinMessage.ConfigurationId)
            {
                _log.EnqueueingSafeToJoin(new MembershipServiceLogger.LoggableEndpoint(joinMessage.Sender), new MembershipServiceLogger.CurrentConfigId(_membershipView),
                    new MembershipServiceLogger.MembershipSize(_membershipView));

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
                _log.WrongConfiguration(new MembershipServiceLogger.LoggableEndpoint(joinMessage.Sender), joinMessage.ConfigurationId,
                    new MembershipServiceLogger.CurrentConfigId(_membershipView), new MembershipServiceLogger.MembershipSize(_membershipView));

                var responseBuilder = new JoinResponse
                {
                    Sender = _myAddr,
                    ConfigurationId = _membershipView.ConfigurationId
                };

                if (_membershipView.IsHostPresent(joinMessage.Sender) &&
                    _membershipView.IsIdentifierPresent(joinMessage.NodeId))
                {
                    // Race condition where a observer already crossed H messages for the joiner and changed
                    // the configuration, but the JoinPhase2 messages show up at the observer
                    // after it has already added the joiner. In this case, we simply
                    // tell the sender that they're safe to join.
                    _log.JoinerAlreadyInRing();
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

                tcs.SetResult(responseBuilder.ToRapidResponse());
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
        _log.HandleBatchedAlertMessage(messageBatch.Messages.Count, new MembershipServiceLogger.LoggableEndpoint(messageBatch.Sender));

        lock (_membershipUpdateLock)
        {
            if (!FilterAlertMessages(messageBatch, _membershipView.ConfigurationId))
            {
                _log.BatchedAlertFiltered(new MembershipServiceLogger.CurrentConfigId(_membershipView));
                return new ConsensusResponse().ToRapidResponse();
            }

            // Use SortedSet for deduplication and consistent ordering across all nodes.
            // This ensures all nodes propose the same set in the same order.
            var proposals = new SortedSet<Endpoint>(EndpointComparer.Instance);

            // Process alerts by ring number to enable proper batching.
            // This ensures multiple nodes can accumulate in the preProposal set
            // before any of them reaches the H threshold, allowing them to be
            // batched into a single view change proposal.
            //
            // Without this interleaving, if a single observer sends alerts for
            // multiple nodes with all ring numbers, each node would go from
            // 0 -> L -> H reports atomically, triggering individual proposals.
            var maxRingNumber = _membershipView.RingCount;
            for (var ringNumber = 0; ringNumber < maxRingNumber; ringNumber++)
            {
                foreach (var msg in messageBatch.Messages)
                {
                    if (!msg.RingNumber.Contains(ringNumber))
                    {
                        continue;
                    }

                    _log.ProcessingAlert(new MembershipServiceLogger.LoggableEndpoint(msg.EdgeSrc), new MembershipServiceLogger.LoggableEndpoint(msg.EdgeDst), msg.EdgeStatus);
                    // For valid UP alerts, extract the joiner details (UUID and metadata) which is going to be needed
                    // when the node is added to the rings
                    var extractedMessage = ExtractJoinerUuidAndMetadata(msg);
                    var cutProposals = _cutDetection.AggregateForProposalSingleRing(extractedMessage, ringNumber);
                    _log.CutDetectionProposals(cutProposals.Count);
                    foreach (var proposal in cutProposals)
                    {
                        proposals.Add(proposal);
                    }
                }
            }

            // Lastly, we apply implicit detections
            var implicitProposals = _cutDetection.InvalidateFailingEdges();
            _log.ImplicitEdgeInvalidation(implicitProposals.Count);
            foreach (var proposal in implicitProposals)
            {
                proposals.Add(proposal);
            }

            // If we have a proposal for this stage, start an instance of consensus on it.
            lock (_membershipUpdateLock)
            {
                if (proposals.Count > 0 && !_announcedProposal)
                {
                    _announcedProposal = true;
                    var currentConfigurationId = _membershipView.ConfigurationId;
                    var proposalList = proposals.ToList();
                    _log.InitiatingConsensus(new MembershipServiceLogger.LoggableEndpoints(proposalList));

                    // Inform subscribers that a proposal has been announced.
                    var nodeStatusChanges = CreateNodeStatusChangeList(proposalList);
                    var currentMembership = _membershipView.GetRing(0);
                    var clusterStatusChange = new ClusterStatusChange(currentConfigurationId, [.. currentMembership], nodeStatusChanges);

                    PublishEvent(ClusterEvents.ViewChangeProposal, clusterStatusChange);

                    _consensusInstance.Propose(proposalList, cancellationToken);
                }
            }

            return new ConsensusResponse().ToRapidResponse();
        }
    }

    /// <summary>
    /// Receives proposal for the one-step consensus (essentially phase 2 of Fast Paxos).
    ///
    /// XXX: Implement recovery for the extremely rare possibility of conflicting proposals.
    /// </summary>
    private RapidResponse HandleConsensusMessages(RapidRequest request, CancellationToken cancellationToken)
    {
        _log.HandleConsensusMessages();

        // Extract configuration ID from the message
        var messageConfigId = GetConfigurationIdFromConsensusMessage(request);

        lock (_membershipUpdateLock)
        {
            var currentConfigId = _membershipView.ConfigurationId;

            if (messageConfigId > currentConfigId)
            {
                // Message is for a future configuration - buffer it for later processing
                _log.BufferingFutureConsensusMessage(request.ContentCase, messageConfigId, currentConfigId);

                if (!_pendingConsensusMessages.TryGetValue(messageConfigId, out var pendingList))
                {
                    pendingList = [];
                    _pendingConsensusMessages[messageConfigId] = pendingList;
                }
                pendingList.Add(request);

                return new ConsensusResponse().ToRapidResponse();
            }

            // Message is for current or past configuration - process normally
            // (past config messages will be rejected by Paxos due to config mismatch)
            _consensusInstance.HandleMessages(request, cancellationToken);
        }

        return new ConsensusResponse().ToRapidResponse();
    }

    /// <summary>
    /// Extracts the configuration ID from a consensus message.
    /// </summary>
    private static long GetConfigurationIdFromConsensusMessage(RapidRequest request)
    {
        return request.ContentCase switch
        {
            RapidRequest.ContentOneofCase.FastRoundPhase2BMessage => request.FastRoundPhase2BMessage.ConfigurationId,
            RapidRequest.ContentOneofCase.Phase1AMessage => request.Phase1AMessage.ConfigurationId,
            RapidRequest.ContentOneofCase.Phase1BMessage => request.Phase1BMessage.ConfigurationId,
            RapidRequest.ContentOneofCase.Phase2AMessage => request.Phase2AMessage.ConfigurationId,
            RapidRequest.ContentOneofCase.Phase2BMessage => request.Phase2BMessage.ConfigurationId,
            _ => throw new ArgumentException($"Unexpected consensus message type: {request.ContentCase}")
        };
    }

    /// <summary>
    /// Propagates the intent of a node to leave the group
    /// </summary>
    private RapidResponse HandleLeaveMessage(RapidRequest request, CancellationToken cancellationToken)
    {
        var leaveMessage = request.LeaveMessage;
        _log.ReceivedLeaveMessage(new MembershipServiceLogger.LoggableEndpoint(leaveMessage.Sender), new MembershipServiceLogger.LoggableEndpoint(_myAddr));
        EdgeFailureNotification(leaveMessage.Sender, _membershipView.ConfigurationId);
        return new ConsensusResponse().ToRapidResponse();
    }

    /// <summary>
    /// Invoked by observers of a node for failure detection.
    /// </summary>
    private RapidResponse HandleProbeMessage(ProbeMessage probeMessage, CancellationToken cancellationToken)
    {
        _log.HandleProbeMessage();
        var senderInMembership = probeMessage.Sender != null && _membershipView.IsHostPresent(probeMessage.Sender);
        return new ProbeResponse
        {
            ConfigurationId = _membershipView.ConfigurationId,
            SenderInMembership = senderInMembership
        }.ToRapidResponse();
    }

    /// <summary>
    /// Handles a request from a node that has detected it has a stale view.
    /// This is the "learner" role in Paxos - allowing nodes that missed consensus
    /// decisions to catch up by requesting the current view from another node.
    /// </summary>
    private RapidResponse HandleMembershipViewRequest(MembershipViewRequest request, CancellationToken cancellationToken)
    {
        _log.HandleMembershipViewRequest(
            new MembershipServiceLogger.LoggableEndpoint(request.Sender),
            request.CurrentConfigurationId,
            new MembershipServiceLogger.CurrentConfigId(_membershipView));

        // Build response with current membership view
        var response = new MembershipViewResponse
        {
            Sender = _myAddr,
            ConfigurationId = _membershipView.ConfigurationId
        };

        // Add all endpoints and their node IDs
        var ring0 = _membershipView.GetRing(0);
        response.Endpoints.AddRange(ring0);
        response.Identifiers.AddRange(_membershipView.NodeIds);

        // Add metadata for all nodes
        foreach (var endpoint in ring0)
        {
            var metadata = _metadataManager.Get(endpoint) ?? new Metadata();
            response.MetadataKeys.Add(endpoint);
            response.MetadataValues.Add(metadata);
        }

        return response.ToRapidResponse();
    }

    /// <summary>
    /// This is invoked by FastPaxos modules when they arrive at a decision.
    ///
    /// Any node that is not in the membership list will be added to the cluster,
    /// and any node that is currently in the membership list will be removed from it.
    /// </summary>
    private async Task DecideViewChange(List<Endpoint> proposal)
    {
        _log.DecideViewChange(proposal.Count);

        ConsensusCoordinator? previousConsensusInstance;
        lock (_membershipUpdateLock)
        {
            _announcedProposal = false;

            // Track nodes that were added so we can notify their joiners after ALL nodes are processed
            var addedNodes = new List<Endpoint>();

            // Build status changes during the loop, capturing state BEFORE modifications
            // This ensures consistent semantics: Up = joining, Down = leaving/failing
            var nodeStatusChanges = new List<NodeStatusChange>(proposal.Count);

            // Create a builder from the current view to make modifications
            var builder = _membershipView.ToBuilder();

            foreach (var node in proposal)
            {
                // If the node is already in the ring, remove it. Else, add it.
                // XXX: Maybe there's a cleaner way to do this in the future because
                // this ties us to just two states a node can be in.
                var isPresent = _membershipView.IsHostPresent(node);
                if (isPresent)
                {
                    _log.RemovingNode(new MembershipServiceLogger.LoggableEndpoint(node));
                    builder.RingDelete(node);
                    nodeStatusChanges.Add(new NodeStatusChange(node, EdgeStatus.Down, _metadataManager.Get(node) ?? new Metadata()));
                }
                else
                {
                    if (!_joinerUuid.TryGetValue(node, out var nodeId))
                    {
                        _log.DecidedNodeWithoutUuid(new MembershipServiceLogger.LoggableEndpoint(node));
                        continue;
                    }

                    var metadata = _joinerMetadata.GetValueOrDefault(node, new Metadata());

                    _log.AddingNode(new MembershipServiceLogger.LoggableEndpoint(node));
                    builder.RingAdd(node, nodeId);
                    _metadataManager.Add(node, metadata);
                    nodeStatusChanges.Add(new NodeStatusChange(node, EdgeStatus.Up, metadata));

                    _joinerUuid.Remove(node);
                    _joinerMetadata.Remove(node);

                    // Track this node for later notification
                    addedNodes.Add(node);
                }
            }

            // Build the new immutable view with incremented version
            var newView = builder.Build(_membershipView.ConfigurationId);

            _log.DecideViewChangeCleanup();

            // Use SetMembershipView to apply all changes - pass null for metadataMap to preserve existing
            previousConsensusInstance = SetMembershipView(newView, metadataMap: null, nodeStatusChanges, addedNodes);
        }

        if (previousConsensusInstance != null)
        {
            await previousConsensusInstance.DisposeAsync();
        }
    }

    /// <summary>
    /// Gets the async enumerable for subscribing to cluster events.
    /// Each subscriber receives all events published after they start iterating.
    /// </summary>
    public IAsyncEnumerable<ClusterEventNotification> EventStream => _eventChannel.Reader;

    /// <summary>
    /// Gets the observable for subscribing to cluster events.
    /// Multiple subscribers receive the same events through multicast.
    /// </summary>
    public IObservable<ClusterEventNotification> Events => _eventChannel.Reader;

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
    /// Queues a AlertMessage to be broadcasted after potentially being batched.
    /// </summary>
    /// <param name="msg">the AlertMessage to be broadcasted</param>
    private void EnqueueAlertMessage(AlertMessage msg)
    {
        _log.EnqueueAlertMessage(new MembershipServiceLogger.LoggableEndpoint(msg.EdgeSrc), new MembershipServiceLogger.LoggableEndpoint(msg.EdgeDst), msg.EdgeStatus);
        _sendQueue.Writer.TryWrite(msg);
    }

    /// <summary>
    /// Batches outgoing AlertMessages into a single BatchAlertMessage.
    /// </summary>
    private async Task AlertBatcherAsync()
    {
        var buffer = new List<AlertMessage>();
        var stoppingToken = _stoppingCts.Token;

        while (!stoppingToken.IsCancellationRequested)
        {
            buffer.Clear();
            try
            {
                await Task.Delay(_options.BatchingWindow, _sharedResources.TimeProvider, stoppingToken).ConfigureAwait(true);
                await _sendQueue.Reader.WaitToReadAsync(stoppingToken);
                while (_sendQueue.Reader.TryRead(out var msg))
                {
                    buffer.Add(msg);
                }

                if (buffer.Count > 0)
                {
                    _log.AlertBatcherBroadcast(buffer.Count);

                    var batchedMessage = new BatchedAlertMessage
                    {
                        Sender = _myAddr
                    };
                    batchedMessage.Messages.AddRange(buffer);

                    var request = batchedMessage.ToRapidRequest();
                    _broadcaster.Broadcast(request, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                _log.AlertBatcherExit();
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
            _log.ExtractJoinerUuidAndMetadata(new MembershipServiceLogger.LoggableEndpoint(alertMessage.EdgeDst));
        }
        return alertMessage;
    }

    /// <summary>
    /// Formats a proposal or view change for application subscriptions.
    /// Determines status based on current view state:
    /// - Nodes NOT in view → EdgeStatus.Up (joining)
    /// - Nodes IN view → EdgeStatus.Down (leaving/failing)
    ///
    /// For ViewChangeProposal: called BEFORE view update, so joining nodes aren't in view yet.
    /// For ViewChange: status is captured inline BEFORE each node is added/removed.
    /// Both cases produce consistent semantics: Up = joining, Down = leaving.
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
    /// Publishes a cluster event to all subscribers via the channel.
    /// </summary>
    /// <param name="evt">The cluster event type.</param>
    /// <param name="statusChange">The cluster status change details.</param>
    private void PublishEvent(ClusterEvents evt, ClusterStatusChange statusChange)
    {
        var notification = new ClusterEventNotification(evt, statusChange);
        _eventChannel.Writer.TryPublish(notification);
    }

    /// <summary>
    /// Attempts to rejoin the cluster after being kicked.
    /// Cycles through known members to find a live seed and performs the join protocol.
    /// </summary>
    private async Task RejoinClusterAsync(CancellationToken cancellationToken)
    {
        if (_isRejoining || _disposed != 0)
        {
            _log.RejoinSkipped(new MembershipServiceLogger.LoggableEndpoint(_myAddr));
            return;
        }
        _isRejoining = true;

        try
        {
            _log.StartingRejoin(new MembershipServiceLogger.LoggableEndpoint(_myAddr));

            // Get known members from our current (stale) view to try as seeds
            var knownMembers = _membershipView.GetRing(0).Where(e => !e.Equals(_myAddr)).ToList();

            // Generate a new node ID for the rejoin
            var nodeId = RapidUtils.NodeIdFromUuid(_sharedResources.NewGuid());
            var metadata = _metadataManager.Get(_myAddr) ?? new Metadata();

            JoinResponse? successfulResponse = null;

            foreach (var seed in knownMembers)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                try
                {
                    successfulResponse = await TryRejoinThroughSeedAsync(seed, nodeId, metadata, cancellationToken).ConfigureAwait(true);
                    if (successfulResponse != null)
                    {
                        break;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.RejoinFailedThroughSeed(new MembershipServiceLogger.LoggableEndpoint(_myAddr), new MembershipServiceLogger.LoggableEndpoint(seed), ex.Message);
                    // Try next seed
                }
            }

            if (successfulResponse == null)
            {
                _log.RejoinFailedNoSeeds(new MembershipServiceLogger.LoggableEndpoint(_myAddr));
                return;
            }

            // Reset internal state with new membership
            ResetStateAfterRejoin(successfulResponse, nodeId);

            _log.RejoinSuccessful(
                new MembershipServiceLogger.LoggableEndpoint(_myAddr),
                successfulResponse.Endpoints.Count,
                successfulResponse.ConfigurationId);
        }
        finally
        {
            _isRejoining = false;
        }
    }

    /// <summary>
    /// Attempts to rejoin the cluster through a specific seed node.
    /// </summary>
    private async Task<JoinResponse?> TryRejoinThroughSeedAsync(
        Endpoint seed,
        NodeId nodeId,
        Metadata metadata,
        CancellationToken cancellationToken)
    {
        _log.AttemptingRejoinThroughSeed(new MembershipServiceLogger.LoggableEndpoint(_myAddr), new MembershipServiceLogger.LoggableEndpoint(seed));

        // Phase 1: PreJoin to get observers
        var preJoinMessage = new PreJoinMessage
        {
            Sender = _myAddr,
            NodeId = nodeId
        };

        var preJoinResponse = await _messagingClient.SendMessageAsync(
            seed,
            preJoinMessage.ToRapidRequest(),
            cancellationToken).ConfigureAwait(true);

        var joinResponse = preJoinResponse.JoinResponse;
        _log.RejoinPreJoinResponse(
            new MembershipServiceLogger.LoggableEndpoint(_myAddr),
            joinResponse.StatusCode,
            joinResponse.Endpoints.Count);

        if (joinResponse.StatusCode != JoinStatusCode.SafeToJoin &&
            joinResponse.StatusCode != JoinStatusCode.HostnameAlreadyInRing)
        {
            return null;
        }

        var observers = joinResponse.Endpoints.ToList();
        if (observers.Count == 0)
        {
            return null;
        }

        // Phase 2: Contact observers
        var ringNumbersPerObserver = new Dictionary<Endpoint, List<int>>();
        for (var ringNumber = 0; ringNumber < observers.Count; ringNumber++)
        {
            var observer = observers[ringNumber];
            if (!ringNumbersPerObserver.TryGetValue(observer, out var value))
            {
                ringNumbersPerObserver[observer] = value = [];
            }
            value.Add(ringNumber);
        }

        var tasks = ringNumbersPerObserver.Select(async entry =>
        {
            var joinMessageForObserver = new JoinMessage
            {
                Sender = _myAddr,
                NodeId = nodeId,
                Metadata = metadata,
                ConfigurationId = joinResponse.ConfigurationId
            };
            joinMessageForObserver.RingNumber.AddRange(entry.Value);

            return await _messagingClient.SendMessageBestEffortAsync(
                entry.Key,
                joinMessageForObserver.ToRapidRequest(),
                cancellationToken).ConfigureAwait(true);
        });

        var responses = await Task.WhenAll(tasks).ConfigureAwait(true);
        return responses.FirstOrDefault(r => r?.JoinResponse?.StatusCode == JoinStatusCode.SafeToJoin)?.JoinResponse;
    }

    /// <summary>
    /// Resets the internal state after a successful rejoin.
    /// </summary>
    private void ResetStateAfterRejoin(JoinResponse response, NodeId nodeId)
    {
        ConsensusCoordinator? oldConsensus;
        lock (_membershipUpdateLock)
        {
            // Clear pending data before setting new view
            _joinersToRespondTo.Clear();
            _joinerUuid.Clear();
            _joinerMetadata.Clear();
            _pendingConsensusMessages.Clear();

            // Build new membership view from response
            var metadataMap = new Dictionary<Endpoint, Metadata>();
            for (var i = 0; i < response.MetadataKeys.Count && i < response.MetadataValues.Count; i++)
            {
                metadataMap[response.MetadataKeys[i]] = response.MetadataValues[i];
            }

            var newView = new MembershipViewBuilder(
                _options.ObserversPerSubject,
                [.. response.Identifiers],
                [.. response.Endpoints]).BuildWithConfigurationId(new ConfigurationId(response.ConfigurationId));

            // Use SetMembershipView to apply all changes - for rejoin, all nodes are treated as Up
            oldConsensus = SetMembershipView(newView, metadataMap, nodeStatusChanges: null, addedNodes: null);
        }

        // Dispose old consensus (fire and forget)
        if (oldConsensus != null)
        {
            _ = oldConsensus.DisposeAsync();
        }
    }

    /// <summary>
    /// Called by the failure detector when a probe response indicates this node
    /// has a stale view (remote has higher config ID, but we're still in membership).
    /// This is the Paxos "learner" role - requesting missed consensus decisions.
    /// </summary>
    /// <param name="remoteEndpoint">The endpoint that reported the higher config ID.</param>
    /// <param name="remoteConfigId">The configuration ID from the probe response.</param>
    /// <param name="localConfigId">The local configuration ID when the stale view was detected.</param>
    private void OnStaleViewDetected(Endpoint remoteEndpoint, long remoteConfigId, long localConfigId)
    {
        _log.StaleViewDetected(new MembershipServiceLogger.LoggableEndpoint(remoteEndpoint), remoteConfigId, localConfigId);

        // Schedule refresh on the background task scheduler
        var refreshTask = Task.Factory.StartNew(
            () => RefreshMembershipViewAsync(remoteEndpoint, remoteConfigId, _stoppingCts.Token),
            _stoppingCts.Token,
            TaskCreationOptions.None,
            _sharedResources.TaskScheduler).Unwrap();
        TrackBackgroundTask(refreshTask);
    }

    /// <summary>
    /// Requests an updated membership view from a remote node.
    /// This implements the Paxos "learner" role - catching up on missed consensus decisions.
    /// </summary>
    private async Task RefreshMembershipViewAsync(Endpoint remoteEndpoint, long expectedConfigId, CancellationToken cancellationToken)
    {
        // Prevent concurrent refresh attempts
        if (_isRefreshingView || _disposed != 0)
        {
            _log.SkippingStaleViewRefresh(expectedConfigId, _membershipView.ConfigurationId);
            return;
        }

        // Double-check we still need to refresh (config may have been updated by another mechanism)
        if (expectedConfigId <= _membershipView.ConfigurationId)
        {
            _log.SkippingStaleViewRefresh(expectedConfigId, _membershipView.ConfigurationId);
            return;
        }

        _isRefreshingView = true;
        try
        {
            _log.RequestingMembershipView(new MembershipServiceLogger.LoggableEndpoint(remoteEndpoint));

            var request = new MembershipViewRequest
            {
                Sender = _myAddr,
                CurrentConfigurationId = _membershipView.ConfigurationId
            };

            var response = await _messagingClient.SendMessageAsync(
                remoteEndpoint,
                request.ToRapidRequest(),
                cancellationToken).ConfigureAwait(true);

            var viewResponse = response.MembershipViewResponse;
            if (viewResponse == null)
            {
                _log.MembershipViewRefreshFailed(new MembershipServiceLogger.LoggableEndpoint(remoteEndpoint), "No MembershipViewResponse in reply");
                return;
            }

            // Only apply if the response is newer than our current view
            if (viewResponse.ConfigurationId <= _membershipView.ConfigurationId)
            {
                _log.SkippingStaleViewRefresh(viewResponse.ConfigurationId, _membershipView.ConfigurationId);
                return;
            }

            // Apply the learned view
            ApplyLearnedMembershipView(viewResponse);

            _log.MembershipViewRefreshed(
                new MembershipServiceLogger.LoggableEndpoint(remoteEndpoint),
                viewResponse.ConfigurationId,
                viewResponse.Endpoints.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.MembershipViewRefreshFailed(new MembershipServiceLogger.LoggableEndpoint(remoteEndpoint), ex.Message);
        }
        finally
        {
            _isRefreshingView = false;
        }
    }

    /// <summary>
    /// Applies a learned membership view from a remote node.
    /// This is used by the Paxos learner mechanism to catch up on missed consensus decisions.
    /// If the local node is not in the new view, it triggers a kicked event and rejoin.
    /// </summary>
    private void ApplyLearnedMembershipView(MembershipViewResponse viewResponse)
    {
        ConsensusCoordinator? oldConsensus;
        bool wasKicked;
        lock (_membershipUpdateLock)
        {
            // Guard against config ID regression - never allow a stale view to replace a fresher one
            if (viewResponse.ConfigurationId <= _membershipView.ConfigurationId)
            {
                return;
            }

            // Build metadata map from response
            var metadataMap = new Dictionary<Endpoint, Metadata>();
            for (var i = 0; i < viewResponse.MetadataKeys.Count && i < viewResponse.MetadataValues.Count; i++)
            {
                metadataMap[viewResponse.MetadataKeys[i]] = viewResponse.MetadataValues[i];
            }

            // Build the new view from the response
            var newView = new MembershipViewBuilder(
                _options.ObserversPerSubject,
                [.. viewResponse.Identifiers],
                [.. viewResponse.Endpoints]).BuildWithConfigurationId(new ConfigurationId(viewResponse.ConfigurationId));

            // Check if we were kicked (not in the new membership)
            var newMembers = new HashSet<Endpoint>(newView.GetRing(0));
            wasKicked = !newMembers.Contains(_myAddr);

            if (wasKicked)
            {
                // We were kicked - publish kicked event but don't apply the view
                // (we'll rejoin with a new identity)
                _log.NodeKicked(
                    new MembershipServiceLogger.LoggableEndpoint(_myAddr),
                    viewResponse.ConfigurationId,
                    _membershipView.ConfigurationId.Version);

                var currentMembership = _membershipView.GetRing(0);
                var nodeStatusChange = new NodeStatusChange(_myAddr, EdgeStatus.Down, _metadataManager.Get(_myAddr) ?? new Metadata());
                var clusterStatusChange = new ClusterStatusChange(_membershipView.ConfigurationId, [.. currentMembership], [nodeStatusChange]);

                PublishEvent(ClusterEvents.Kicked, clusterStatusChange);
                oldConsensus = null;
            }
            else
            {
                // We're still in membership - compute status changes and apply the view
                var oldMembers = new HashSet<Endpoint>(_membershipView.GetRing(0));
                var nodeStatusChanges = new List<NodeStatusChange>();

                // Nodes that left (in old but not in new)
                foreach (var node in oldMembers)
                {
                    if (!newMembers.Contains(node))
                    {
                        nodeStatusChanges.Add(new NodeStatusChange(node, EdgeStatus.Down, _metadataManager.Get(node) ?? new Metadata()));
                    }
                }

                // Nodes that joined (in new but not in old)
                foreach (var node in newMembers)
                {
                    if (!oldMembers.Contains(node))
                    {
                        var metadata = metadataMap.GetValueOrDefault(node, new Metadata());
                        nodeStatusChanges.Add(new NodeStatusChange(node, EdgeStatus.Up, metadata));
                    }
                }

                // Apply the new view
                oldConsensus = SetMembershipView(newView, metadataMap, nodeStatusChanges, addedNodes: null);
            }
        }

        // Dispose old consensus (fire and forget)
        if (oldConsensus != null)
        {
            _ = oldConsensus.DisposeAsync();
        }

        // If we were kicked, schedule rejoin outside the lock
        if (wasKicked)
        {
            var rejoinTask = Task.Factory.StartNew(
                () => RejoinClusterAsync(_stoppingCts.Token),
                _stoppingCts.Token,
                TaskCreationOptions.None,
                _sharedResources.TaskScheduler).Unwrap();
            TrackBackgroundTask(rejoinTask);
        }
    }

    /// <summary>
    /// Creates and schedules failure detector instances based on the fdFactory instance.
    /// </summary>
    private void CreateFailureDetectorsForCurrentConfiguration()
    {
        // Check if this node is still in the ring - it may have been removed during a view change
        if (!_membershipView.IsHostPresent(_myAddr))
        {
            _log.SkippingFailureDetectorsNotInRing();
            return;
        }

        var subjects = _membershipView.GetSubjectsOf(_myAddr);
        var configurationId = _membershipView.ConfigurationId;

        _log.CreateFailureDetectors(subjects.Length);

        for (var i = 0; i < subjects.Length; i++)
        {
            var subject = subjects[i];
            var ringNumber = i;
            var fd = _fdFactory.CreateInstance(subject, () => EdgeFailureNotification(subject, configurationId));

            _log.CreatedFailureDetector(new MembershipServiceLogger.LoggableEndpoint(subject), ringNumber);

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
        _log.EdgeFailureNotificationScheduled(new MembershipServiceLogger.LoggableEndpoint(subject), configurationId);

        try
        {
            if (configurationId != _membershipView.ConfigurationId)
            {
                _log.IgnoringOldConfigNotification(new MembershipServiceLogger.LoggableEndpoint(subject), new MembershipServiceLogger.CurrentConfigId(_membershipView), configurationId);
                return;
            }

            _log.AnnouncingEdgeFail(new MembershipServiceLogger.LoggableEndpoint(subject), new MembershipServiceLogger.LoggableEndpoint(_myAddr), configurationId, new MembershipServiceLogger.MembershipSize(_membershipView));

            var ringNumbers = _membershipView.GetRingNumbers(_myAddr, subject);
            _log.EdgeFailureNotificationEnqueued(new MembershipServiceLogger.LoggableEndpoint(subject), new MembershipServiceLogger.LoggableRingNumbers(ringNumbers));

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
            _log.ErrorInEdgeFailureNotification(ex, new MembershipServiceLogger.LoggableEndpoint(subject));
            throw;
        }
    }

    /// <summary>
    /// Registers a continuation on ConsensusCoordinator.Decided that handles the result and checks for shutdown.
    /// The continuation is tracked as a background task to ensure proper cleanup during shutdown.
    /// </summary>
    private void RegisterConsensusDecidedContinuation(ConsensusCoordinator consensusInstance)
    {
        var continuationTask = consensusInstance.Decided.ContinueWith(async decision =>
        {
            if (decision.IsFaulted)
            {
                _log.ConsensusDecidedFaulted(decision.Exception!);
                // Consensus failed (e.g., exhausted all rounds during a partition).
                // Reset state to allow new proposals when alerts arrive.
                await ResetConsensusStateAfterFailure(consensusInstance);
                return;
            }

            await DecideViewChange(await decision);
        }, CancellationToken.None, TaskContinuationOptions.None, _sharedResources.TaskScheduler);
        continuationTask.Unwrap().Ignore();
    }

    /// <summary>
    /// Resets consensus state after a failure to allow new proposals.
    /// Called when consensus exhausts all rounds without reaching a decision.
    /// </summary>
    private async Task ResetConsensusStateAfterFailure(ConsensusCoordinator failedInstance)
    {
        lock (_membershipUpdateLock)
        {
            // Only reset if this is still the current consensus instance
            if (!ReferenceEquals(_consensusInstance, failedInstance))
            {
                return;
            }

            // Reset the announced proposal flag so new alerts can trigger consensus
            _announcedProposal = false;

            // Create a fresh consensus coordinator for the same configuration
            _consensusInstance = _consensusCoordinatorFactory.Create(
                _myAddr,
                _membershipView.ConfigurationId,
                _membershipView.Size,
                _broadcaster);
            RegisterConsensusDecidedContinuation(_consensusInstance);
        }

        await failedInstance.DisposeAsync();
    }

    /// <summary>
    /// Replays buffered consensus messages for the given configuration.
    /// Called after a view change to process any messages that arrived before we transitioned.
    /// Also cleans up messages for old configurations.
    /// </summary>
    private void ReplayBufferedConsensusMessages(long currentConfigId, CancellationToken cancellationToken)
    {
        // Clean up messages for old configurations (they're no longer relevant)
        var keysToRemove = _pendingConsensusMessages.Keys.Where(k => k < currentConfigId).ToList();
        foreach (var key in keysToRemove)
        {
            _pendingConsensusMessages.Remove(key);
        }

        // Replay messages for the current configuration
        if (_pendingConsensusMessages.TryGetValue(currentConfigId, out var pendingMessages))
        {
            _pendingConsensusMessages.Remove(currentConfigId);

            if (pendingMessages.Count > 0)
            {
                _log.ReplayingBufferedMessages(pendingMessages.Count, currentConfigId);

                foreach (var message in pendingMessages)
                {
                    _consensusInstance.HandleMessages(message, cancellationToken);
                }
            }
        }
    }

    /// <summary>
    /// Tracks a background task to ensure it can be awaited during shutdown.
    /// </summary>
    private void TrackBackgroundTask(Task task)
    {
        lock (_backgroundTasksLock)
        {
            _backgroundTasks.Add(task);
        }
    }

    /// <summary>
    /// Stops the membership service gracefully by notifying observers and waiting for background tasks.
    /// Does not dispose resources - call <see cref="DisposeAsync"/> after this method.
    /// </summary>
    /// <remarks>
    /// For graceful shutdown: call <c>StopAsync()</c> then <c>DisposeAsync()</c>.
    /// For hard crash (no notification): call <c>DisposeAsync()</c> directly.
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token to observe.</param>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _log.Stopping();

        // Send leave messages to observers
        try
        {
            var leaveMessage = new LeaveMessage { Sender = _myAddr };
            var leave = leaveMessage.ToRapidRequest();

            var observers = _membershipView.GetObserversOf(_myAddr);
            _log.LeavingWithObservers(new MembershipServiceLogger.LoggableEndpoint(_myAddr), observers.Length, new MembershipServiceLogger.LoggableEndpoints(observers));

            var leaveTasks = observers.Select(endpoint =>
                _messagingClient.SendMessageBestEffortAsync(endpoint, leave, cancellationToken));

            await Task.WhenAll(leaveTasks).WaitAsync(_options.LeaveMessageTimeout, _sharedResources.TimeProvider, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Cancellation requested - continue with shutdown
        }
        catch (TimeoutException)
        {
            _log.TimeoutWhileLeaving();
        }
        catch (NodeNotInRingException)
        {
            // We may already have been removed, continue with shutdown
            _log.NodeAlreadyRemoved();
        }

        // Cancel background tasks
        await _stoppingCts.CancelAsync().ConfigureAwait(true);

        // Wait for background tasks to complete
        Task[] backgroundTasks;
        lock (_backgroundTasksLock)
        {
            backgroundTasks = [.. _backgroundTasks];
        }

        if (backgroundTasks.Length > 0)
        {
            _log.WaitingForBackgroundTasks(backgroundTasks.Length);

            try
            {
                await Task.WhenAll(backgroundTasks).WaitAsync(cancellationToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                // Expected during forced shutdown
            }
        }
    }

    /// <summary>
    /// Asynchronously disposes the membership service.
    /// Disposes event channels, failure detectors, and consensus instance.
    /// </summary>
    /// <remarks>
    /// For graceful shutdown: call <c>StopAsync()</c> before this method.
    /// For hard crash (no notification): call this method directly.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return; // Already disposed
        }

        _log.Dispose();

        // Cancel background tasks (in case StopAsync wasn't called)
        await _stoppingCts.CancelAsync().ConfigureAwait(true);
        _stoppingCts.Dispose();

        // Dispose the event channel to signal completion to all subscribers
        _eventChannel.Dispose();

        // Dispose failure detectors
        foreach (var fd in _failureDetectors)
        {
            fd.Dispose();
        }
        _failureDetectors.Clear();

        // Dispose consensus instance
        await _consensusInstance.DisposeAsync();
    }

    /// <summary>
    /// Synchronously disposes the membership service.
    /// Note: Callers should prefer DisposeAsync when possible.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return; // Already disposed
        }

        _log.Dispose();

        // Cancel background tasks (in case StopAsync wasn't called)
#pragma warning disable CA1849 // Call async methods when in an async method - sync Dispose
        _stoppingCts.Cancel();
#pragma warning restore CA1849
        _stoppingCts.Dispose();

        // Dispose the event channel to signal completion to all subscribers
        _eventChannel.Dispose();

        // Dispose failure detectors
        foreach (var fd in _failureDetectors)
        {
            fd.Dispose();
        }
        _failureDetectors.Clear();

        // Dispose consensus instance
        _consensusInstance.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
