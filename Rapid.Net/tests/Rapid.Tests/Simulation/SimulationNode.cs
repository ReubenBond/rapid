using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Rapid.Messaging;
using Rapid.Monitoring;
using Rapid.Pb;

namespace Rapid.Tests.Simulation;

/// <summary>
/// Represents a simulated node in a Rapid cluster.
/// Uses in-memory transport instead of gRPC and does not require a WebApplication.
/// </summary>
internal sealed class SimulationNode : IAsyncDisposable, IDisposable
{
    private readonly SimulationHarness _harness;
    private readonly NodeSimulationContext _context;
    private readonly SharedResources _sharedResources;
    private readonly PingPongFailureDetectorFactory _failureDetectorFactory;
    private readonly IConsensusCoordinatorFactory _consensusCoordinatorFactory;
    private readonly CutDetectorFactory _cutDetectorFactory;
    private readonly MembershipViewAccessor _viewAccessor;
    private readonly IOptions<RapidProtocolOptions> _protocolOptions;
    private readonly ILogger<SimulationNode> _logger;
    private readonly ILogger<MembershipService> _membershipServiceLogger;
    private readonly TaskCompletionSource<MembershipService> _membershipServiceTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private MembershipService? _membershipService;
    private bool _disposed;

    /// <summary>
    /// Gets the endpoint address of this node.
    /// </summary>
    public Endpoint Address { get; }

    /// <summary>
    /// Gets the simulation random instance for this node.
    /// </summary>
    public SimulationRandom Random { get; }

    /// <summary>
    /// Gets the current membership view of this node.
    /// </summary>
    public MembershipView CurrentView => _viewAccessor.CurrentView;

    /// <summary>
    /// Gets the membership view accessor for subscribing to view changes.
    /// </summary>
    public IMembershipViewAccessor ViewAccessor => _viewAccessor;

    /// <summary>
    /// Gets whether this node is initialized and part of a cluster.
    /// </summary>
    public bool IsInitialized => _membershipService != null;

    /// <summary>
    /// Gets the membership size of this node's view.
    /// </summary>
    public int MembershipSize => _viewAccessor.CurrentView.Size;

    /// <summary>
    /// Gets the messaging client for testing purposes.
    /// </summary>
    internal InMemoryMessagingClient MessagingClient { get; }

    private SimulationNode(
        SimulationHarness harness,
        NodeSimulationContext context,
        Endpoint address,
        RapidProtocolOptions? protocolOptions,
        ILoggerFactory? loggerFactory)
    {
        _harness = harness;
        _context = context;
        Address = address;
        Random = harness.CreateDerivedRandom();

        var factory = loggerFactory ?? harness.LoggerFactory;
        _logger = factory?.CreateLogger<SimulationNode>()
            ?? NullLogger<SimulationNode>.Instance;
        _membershipServiceLogger = factory?.CreateLogger<MembershipService>()
            ?? NullLogger<MembershipService>.Instance;

        // Create protocol options
        var options = protocolOptions ?? new RapidProtocolOptions();
        _protocolOptions = Options.Create(options);

        // Create shared resources with the node's time provider and task scheduler
        var sharedResourcesLogger = factory?.CreateLogger<SharedResources>()
            ?? NullLogger<SharedResources>.Instance;
        _sharedResources = new SharedResources(sharedResourcesLogger, context.TimeProvider, context.TaskScheduler, Random, Random.NextGuid);

        // Create in-memory messaging client using GrpcTimeout from protocol options.
        // For tests with suspended nodes requiring Classic Paxos fallback,
        // a longer timeout (e.g., 30 seconds) may be needed to allow
        // for the random jitter delay before Classic Paxos starts.
        MessagingClient = new InMemoryMessagingClient(harness, this, address, options);

        // Create view accessor
        _viewAccessor = new MembershipViewAccessor();

        // Create failure detector factory
        var failureDetectorLogger = factory?.CreateLogger<PingPongFailureDetector>()
            ?? NullLogger<PingPongFailureDetector>.Instance;
        _failureDetectorFactory = new PingPongFailureDetectorFactory(
            address,
            MessagingClient,
            _sharedResources,
            _protocolOptions,
            failureDetectorLogger);

        // Create consensus coordinator factory
        var consensusCoordinatorLogger = factory?.CreateLogger<ConsensusCoordinator>()
            ?? NullLogger<ConsensusCoordinator>.Instance;
        var fastPaxosLogger = factory?.CreateLogger<FastPaxos>()
            ?? NullLogger<FastPaxos>.Instance;
        var paxosLogger = factory?.CreateLogger<Paxos>()
            ?? NullLogger<Paxos>.Instance;
        _consensusCoordinatorFactory = new ConsensusCoordinatorFactory(
            MessagingClient,
            _protocolOptions,
            _sharedResources,
            consensusCoordinatorLogger,
            fastPaxosLogger,
            paxosLogger);

        // Create cut detector factory
        var simpleCutDetectorLogger = factory?.CreateLogger<SimpleCutDetector>()
            ?? NullLogger<SimpleCutDetector>.Instance;
        var multiNodeCutDetectorLogger = factory?.CreateLogger<MultiNodeCutDetector>()
            ?? NullLogger<MultiNodeCutDetector>.Instance;
        _cutDetectorFactory = new CutDetectorFactory(_protocolOptions, simpleCutDetectorLogger, multiNodeCutDetectorLogger);

        // Register with the simulation harness
        harness.RegisterNode(this);
    }

    /// <summary>
    /// Creates a new simulation node with a pre-created context.
    /// </summary>
    public static SimulationNode Create(
        SimulationHarness harness,
        NodeSimulationContext context,
        string hostname,
        int port,
        RapidProtocolOptions? protocolOptions = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentNullException.ThrowIfNull(context);
        var address = RapidUtils.HostFromParts(hostname, port);
        return new SimulationNode(harness, context, address, protocolOptions, loggerFactory);
    }

    /// <summary>
    /// Creates a new simulation node with a numeric identifier.
    /// </summary>
    public static SimulationNode Create(
        SimulationHarness harness,
        NodeSimulationContext context,
        int nodeId,
        RapidProtocolOptions? protocolOptions = null,
        ILoggerFactory? loggerFactory = null) => Create(harness, context, "node", nodeId, protocolOptions, loggerFactory);

    /// <summary>
    /// Starts this node as a new single-node cluster (seed node).
    /// </summary>
    public void StartCluster(Metadata? metadata = null)
    {
        _logger.LogInformation("Starting cluster for node {Address}", RapidUtils.Loggable(Address));

        if (_membershipService != null)
        {
            _logger.LogError("Cannot start cluster - node {Address} is already initialized", RapidUtils.Loggable(Address));
            throw new InvalidOperationException("Node is already initialized");
        }

        var nodeId = RapidUtils.NodeIdFromUuid(Random.NextGuid());
        var actualMetadata = metadata ?? new Metadata();

        _logger.LogDebug("Node {Address} generated node ID {NodeId}", RapidUtils.Loggable(Address), nodeId);

        var opts = _protocolOptions.Value;
        var membershipView = new MembershipViewBuilder(opts.ObserversPerSubject, [nodeId], [Address]).Build();

        // Use factory to create appropriate cut detector for single-node cluster
        var cutDetector = _cutDetectorFactory.Create(membershipView);
        var metadataMap = new Dictionary<Endpoint, Metadata> { { Address, actualMetadata } };
        var broadcaster = new UnicastToAllBroadcaster(MessagingClient);

        _membershipService = new MembershipService(
            Address,
            cutDetector,
            membershipView,
            _sharedResources,
            _protocolOptions,
            MessagingClient,
            broadcaster,
            _failureDetectorFactory,
            _consensusCoordinatorFactory,
            _cutDetectorFactory,
            _viewAccessor,
            metadataMap,
            _membershipServiceLogger);

        // Signal that the node is now initialized and ready to handle requests
        _membershipServiceTcs.TrySetResult(_membershipService);

        _logger.LogInformation("Cluster started for node {Address} with {MembershipSize} members",
            RapidUtils.Loggable(Address), membershipView.Size);
    }

    /// <summary>
    /// Joins this node to an existing cluster through the specified seed node.
    /// Implements retry logic with exponential backoff for resilience against message loss.
    /// </summary>
    public async Task JoinClusterAsync(SimulationNode seedNode, Metadata? metadata = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(seedNode);
        cancellationToken.ThrowIfCancellationRequested();

        _logger.LogInformation("Node {Address} attempting to join cluster via seed {SeedAddress}",
            RapidUtils.Loggable(Address), RapidUtils.Loggable(seedNode.Address));

        if (_membershipService != null)
        {
            _logger.LogError("Cannot join cluster - node {Address} is already initialized", RapidUtils.Loggable(Address));
            throw new InvalidOperationException("Node is already initialized");
        }

        var nodeId = RapidUtils.NodeIdFromUuid(Random.NextGuid());
        var actualMetadata = metadata ?? new Metadata();

        _logger.LogDebug("Node {Address} generated node ID {NodeId} for join", RapidUtils.Loggable(Address), nodeId);

        var maxRetries = _protocolOptions.Value.GrpcDefaultRetries;
        var retryDelay = TimeSpan.FromMilliseconds(100);
        JoinResponse? successfulResponse = null;

        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                successfulResponse = await TryJoinClusterAsync(seedNode, nodeId, actualMetadata, cancellationToken).ConfigureAwait(true);
                if (successfulResponse != null)
                {
                    break;
                }
            }
            catch (Exception ex) when (attempt < maxRetries && (ex is TimeoutException || (ex is InvalidOperationException && IsRetryableJoinError(ex))))
            {
                _logger.LogWarning("Node {Address} join attempt {Attempt} failed: {Message}. Retrying in {Delay}ms",
                    RapidUtils.Loggable(Address), attempt + 1, ex.Message, retryDelay.TotalMilliseconds);
                await Task.Delay(retryDelay, _context.TimeProvider, cancellationToken).ConfigureAwait(true);
                retryDelay *= 2; // Exponential backoff
            }
        }

        if (successfulResponse == null)
        {
            _logger.LogError("Node {Address} failed to join cluster after {MaxRetries} retries",
                RapidUtils.Loggable(Address), maxRetries + 1);
            throw new InvalidOperationException($"Failed to join cluster after {maxRetries + 1} attempts");
        }

        // Initialize membership from response
        var metadataMap = new Dictionary<Endpoint, Metadata>();
        for (var i = 0; i < successfulResponse.MetadataKeys.Count && i < successfulResponse.MetadataValues.Count; i++)
        {
            var endpoint = successfulResponse.MetadataKeys[i];
            var m = successfulResponse.MetadataValues[i];
            metadataMap[endpoint] = m;
        }

        var opts = _protocolOptions.Value;
        var clusterSize = successfulResponse.Endpoints.Count;
        var membershipView = new MembershipViewBuilder(
            opts.ObserversPerSubject,
            [.. successfulResponse.Identifiers],
            [.. successfulResponse.Endpoints]).BuildWithConfigurationId(new ConfigurationId(successfulResponse.ConfigurationId));

        // Use factory to create appropriate cut detector based on actual cluster size
        var cutDetector = _cutDetectorFactory.Create(membershipView);
        var broadcaster = new UnicastToAllBroadcaster(MessagingClient);

        _membershipService = new MembershipService(
            Address,
            cutDetector,
            membershipView,
            _sharedResources,
            _protocolOptions,
            MessagingClient,
            broadcaster,
            _failureDetectorFactory,
            _consensusCoordinatorFactory,
            _cutDetectorFactory,
            _viewAccessor,
            metadataMap,
            _membershipServiceLogger);

        // Signal that the node is now initialized and ready to handle requests
        _membershipServiceTcs.TrySetResult(_membershipService);

        _logger.LogInformation("Node {Address} successfully joined cluster with {MembershipSize} members, ConfigId={ConfigId}",
            RapidUtils.Loggable(Address), membershipView.Size, membershipView.ConfigurationId);
    }

    /// <summary>
    /// Attempts a single join operation. Returns the successful JoinResponse or null if retry is needed.
    /// </summary>
    private async Task<JoinResponse?> TryJoinClusterAsync(SimulationNode seedNode, NodeId nodeId, Metadata metadata, CancellationToken cancellationToken)
    {
        // Phase 1: Contact seed for observers
        _logger.LogDebug("Node {Address} sending PreJoinMessage to seed {SeedAddress}",
            RapidUtils.Loggable(Address), RapidUtils.Loggable(seedNode.Address));

        var preJoinMessage = new PreJoinMessage
        {
            Sender = Address,
            NodeId = nodeId
        };

        var preJoinResponse = await MessagingClient.SendMessageAsync(
            seedNode.Address,
            RapidUtils.ToRapidRequest(preJoinMessage),
            cancellationToken).ConfigureAwait(true);

        var joinResponse = preJoinResponse.JoinResponse;

        _logger.LogDebug("Node {Address} received join response with status {StatusCode} and {ObserverCount} observers",
            RapidUtils.Loggable(Address), joinResponse.StatusCode, joinResponse.Endpoints.Count);

        if (joinResponse.StatusCode != JoinStatusCode.SafeToJoin &&
            joinResponse.StatusCode != JoinStatusCode.HostnameAlreadyInRing)
        {
            _logger.LogError("Node {Address} join failed with status: {StatusCode}",
                RapidUtils.Loggable(Address), joinResponse.StatusCode);
            throw new InvalidOperationException($"Join failed with status: {joinResponse.StatusCode}");
        }

        var observers = joinResponse.Endpoints.ToList();
        if (observers.Count == 0)
        {
            _logger.LogError("Node {Address} received no observers from seed", RapidUtils.Loggable(Address));
            throw new InvalidOperationException("No observers returned from seed");
        }

        // Phase 2: Contact observers
        _logger.LogDebug("Node {Address} contacting {ObserverCount} observers",
            RapidUtils.Loggable(Address), observers.Count);

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
                Sender = Address,
                NodeId = nodeId,
                Metadata = metadata,
                ConfigurationId = joinResponse.ConfigurationId
            };
            joinMessageForObserver.RingNumber.AddRange(entry.Value);

            _logger.LogTrace("Node {Address} sending JoinMessage to observer {Observer} for rings {Rings}",
                RapidUtils.Loggable(Address), RapidUtils.Loggable(entry.Key), string.Join(",", entry.Value));

            // Use best-effort for observer messages - some may be dropped but we only need one success
            return await MessagingClient.SendMessageBestEffortAsync(
                entry.Key,
                RapidUtils.ToRapidRequest(joinMessageForObserver),
                cancellationToken).ConfigureAwait(true);
        });

        var responses = await Task.WhenAll(tasks).ConfigureAwait(true);
        var successfulResponse = responses.FirstOrDefault(r => r?.JoinResponse?.StatusCode == JoinStatusCode.SafeToJoin)?.JoinResponse;

        if (successfulResponse == null)
        {
            // Check if we got a ConfigChanged response - this means we should retry
            var configChangedResponse = responses.FirstOrDefault(r => r?.JoinResponse?.StatusCode == JoinStatusCode.ConfigChanged);
            if (configChangedResponse != null)
            {
                _logger.LogDebug("Node {Address} received ConfigChanged response, will retry", RapidUtils.Loggable(Address));
                throw new InvalidOperationException("Configuration changed during join, retry needed");
            }

            _logger.LogWarning("Node {Address} failed to get successful response from any observer", RapidUtils.Loggable(Address));
            throw new InvalidOperationException("Failed to get successful response from any observer");
        }

        _logger.LogDebug("Node {Address} received successful join response with {MemberCount} members",
            RapidUtils.Loggable(Address), successfulResponse.Endpoints.Count);

        return successfulResponse;
    }

    /// <summary>
    /// Determines if a join error is retryable (transient network issues vs permanent errors).
    /// </summary>
    private static bool IsRetryableJoinError(Exception ex)
    {
        // Network partition errors, message drops (timeout), and config changes are retryable
        return ex is TimeoutException ||  // Dropped messages cause timeouts
               ex.Message.Contains("Network partition", StringComparison.OrdinalIgnoreCase) ||
               ex.Message.Contains("cannot reach", StringComparison.OrdinalIgnoreCase) ||
               ex.Message.Contains("Configuration changed", StringComparison.OrdinalIgnoreCase) ||
               ex.Message.Contains("Failed to get successful response", StringComparison.OrdinalIgnoreCase) ||
               ex.Message.Contains("dropped", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Handles an incoming request from another node.
    /// Waits for the node to be initialized before processing the request.
    /// </summary>
    internal async Task<RapidResponse> HandleRequestAsync(RapidRequest request, CancellationToken cancellationToken)
    {
        // Wait for the node to be initialized. This handles the case where
        // messages arrive before the node has completed joining the cluster.
        var membershipService = await _membershipServiceTcs.Task.WaitAsync(cancellationToken).ConfigureAwait(true);

        _logger.LogTrace("Node {Address} handling request of type {RequestType}",
            RapidUtils.Loggable(Address), request.ContentCase);

        return await membershipService.HandleMessageAsync(request, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// Gets the async enumerable for subscribing to cluster events.
    /// Each subscriber receives all events published after they start iterating.
    /// </summary>
    public IAsyncEnumerable<ClusterEventNotification> EventStream =>
        _membershipService?.EventStream ?? throw new InvalidOperationException("Membership service has not been initialized.");

    /// <summary>
    /// Gracefully leaves the cluster.
    /// </summary>
    public async Task LeaveAsync()
    {
        if (_membershipService is null)
        {
            throw new InvalidOperationException("Membership service has not been initialized.");
        }

        _logger.LogInformation("Node {Address} leaving cluster gracefully", RapidUtils.Loggable(Address));
        await _membershipService.LeaveAsync().ConfigureAwait(true);
        _logger.LogInformation("Node {Address} completed graceful leave", RapidUtils.Loggable(Address));
    }

    /// <summary>
    /// Shuts down the node.
    /// </summary>
    public void Shutdown()
    {
        if (_membershipService != null)
        {
            _logger.LogInformation("Node {Address} shutting down", RapidUtils.Loggable(Address));
            _membershipService.Shutdown();
        }
        else
        {
            _logger.LogDebug("Node {Address} Shutdown called but node is not initialized", RapidUtils.Loggable(Address));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _logger.LogDebug("Node {Address} disposing async", RapidUtils.Loggable(Address));

        // First shutdown shared resources to cancel the ShuttingDownToken
        // This will cause consensus instances to complete
        _sharedResources.StartShutdown();

        // Now dispose the membership service asynchronously
        if (_membershipService != null)
        {
            _membershipService.Shutdown();
            await _membershipService.DisposeAsync();
        }

        _sharedResources.Dispose();
        MessagingClient.Dispose();
        _harness.UnregisterNode(this);

        _logger.LogDebug("Node {Address} disposed async", RapidUtils.Loggable(Address));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _logger.LogDebug("Node {Address} disposing", RapidUtils.Loggable(Address));

        // First shutdown shared resources to cancel the ShuttingDownToken
        // This will cause consensus instances to complete
        _sharedResources.StartShutdown();

        _membershipService?.Shutdown();
        // Note: We cannot await DisposeAsync here, so we skip the async dispose
        // The shutdown above should have cancelled everything
        _sharedResources.Dispose();
        MessagingClient.Dispose();
        _harness.UnregisterNode(this);

        _logger.LogDebug("Node {Address} disposed", RapidUtils.Loggable(Address));
    }
}
