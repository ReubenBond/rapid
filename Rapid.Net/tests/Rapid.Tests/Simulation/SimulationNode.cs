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
    private readonly RapidProtocolOptions _protocolOptions;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ILogger<SimulationNode> _logger;
    
    // These are mutable because they need to be recreated during rejoin
    private SharedResources _sharedResources;
    private PingPongFailureDetectorFactory _failureDetectorFactory;
    private IConsensusCoordinatorFactory _consensusCoordinatorFactory;
    private CutDetectorFactory _cutDetectorFactory;
    private MembershipViewAccessor _viewAccessor;
    private ILogger<MembershipService> _membershipServiceLogger;
    private TaskCompletionSource<MembershipService> _membershipServiceTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
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

        _loggerFactory = loggerFactory ?? harness.LoggerFactory;
        _logger = _loggerFactory?.CreateLogger<SimulationNode>()
            ?? NullLogger<SimulationNode>.Instance;
        _membershipServiceLogger = _loggerFactory?.CreateLogger<MembershipService>()
            ?? NullLogger<MembershipService>.Instance;

        // Create protocol options
        _protocolOptions = protocolOptions ?? new RapidProtocolOptions();

        // Create shared resources with the node's time provider and task scheduler
        var sharedResourcesLogger = _loggerFactory?.CreateLogger<SharedResources>()
            ?? NullLogger<SharedResources>.Instance;
        _sharedResources = new SharedResources(sharedResourcesLogger, context.TimeProvider, context.TaskScheduler, Random, Random.NextGuid);

        // Create in-memory messaging client using GrpcTimeout from protocol options.
        // For tests with suspended nodes requiring Classic Paxos fallback,
        // a longer timeout (e.g., 30 seconds) may be needed to allow
        // for the random jitter delay before Classic Paxos starts.
        MessagingClient = new InMemoryMessagingClient(harness, this, address, _protocolOptions);

        // Create view accessor
        _viewAccessor = new MembershipViewAccessor();

        // Create failure detector factory
        var failureDetectorLogger = _loggerFactory?.CreateLogger<PingPongFailureDetector>()
            ?? NullLogger<PingPongFailureDetector>.Instance;
        _failureDetectorFactory = new PingPongFailureDetectorFactory(
            address,
            MessagingClient,
            _sharedResources,
            Options.Create(_protocolOptions),
            failureDetectorLogger);

        // Create consensus coordinator factory
        var consensusCoordinatorLogger = _loggerFactory?.CreateLogger<ConsensusCoordinator>()
            ?? NullLogger<ConsensusCoordinator>.Instance;
        var fastPaxosLogger = _loggerFactory?.CreateLogger<FastPaxos>()
            ?? NullLogger<FastPaxos>.Instance;
        var paxosLogger = _loggerFactory?.CreateLogger<Paxos>()
            ?? NullLogger<Paxos>.Instance;
        _consensusCoordinatorFactory = new ConsensusCoordinatorFactory(
            MessagingClient,
            Options.Create(_protocolOptions),
            _sharedResources,
            consensusCoordinatorLogger,
            fastPaxosLogger,
            paxosLogger);

        // Create cut detector factory
        var simpleCutDetectorLogger = _loggerFactory?.CreateLogger<SimpleCutDetector>()
            ?? NullLogger<SimpleCutDetector>.Instance;
        var multiNodeCutDetectorLogger = _loggerFactory?.CreateLogger<MultiNodeCutDetector>()
            ?? NullLogger<MultiNodeCutDetector>.Instance;
        _cutDetectorFactory = new CutDetectorFactory(Options.Create(_protocolOptions), simpleCutDetectorLogger, multiNodeCutDetectorLogger);

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
    /// Creates a MembershipService with the given seed address and initializes it.
    /// </summary>
    private async Task<MembershipService> CreateAndInitializeMembershipServiceAsync(
        Endpoint? seedAddress,
        Metadata? metadata,
        CancellationToken cancellationToken)
    {
        var rapidOptions = new RapidOptions
        {
            ListenAddress = Address,
            SeedAddress = seedAddress,
            Metadata = metadata ?? new Metadata()
        };

        var broadcasterFactory = new UnicastToAllBroadcasterFactory(MessagingClient);

        var membershipService = new MembershipService(
            Options.Create(rapidOptions),
            Options.Create(_protocolOptions),
            MessagingClient,
            broadcasterFactory,
            _failureDetectorFactory,
            _consensusCoordinatorFactory,
            _cutDetectorFactory,
            _viewAccessor,
            _sharedResources,
            _membershipServiceLogger);

        await membershipService.InitializeAsync(cancellationToken).ConfigureAwait(true);

        return membershipService;
    }

    /// <summary>
    /// Starts this node as a new single-node cluster (seed node).
    /// </summary>
    public async Task StartClusterAsync(Metadata? metadata = null, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting cluster for node {Address}", RapidUtils.Loggable(Address));

        if (_membershipService != null)
        {
            _logger.LogError("Cannot start cluster - node {Address} is already initialized", RapidUtils.Loggable(Address));
            throw new InvalidOperationException("Node is already initialized");
        }

        // seedAddress = null means start a new cluster
        _membershipService = await CreateAndInitializeMembershipServiceAsync(
            seedAddress: null,
            metadata,
            cancellationToken).ConfigureAwait(true);

        // Signal that the node is now initialized and ready to handle requests
        _membershipServiceTcs.TrySetResult(_membershipService);

        _logger.LogInformation("Cluster started for node {Address} with {MembershipSize} members",
            RapidUtils.Loggable(Address), CurrentView.Size);
    }

    /// <summary>
    /// Starts this node as a new single-node cluster (seed node).
    /// Synchronous wrapper for backwards compatibility with existing tests.
    /// </summary>
    public void StartCluster(Metadata? metadata = null)
    {
        // Run synchronously on the simulation's task scheduler
        StartClusterAsync(metadata, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Joins this node to an existing cluster through the specified seed node.
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

        _membershipService = await CreateAndInitializeMembershipServiceAsync(
            seedAddress: seedNode.Address,
            metadata,
            cancellationToken).ConfigureAwait(true);

        // Signal that the node is now initialized and ready to handle requests
        _membershipServiceTcs.TrySetResult(_membershipService);

        _logger.LogInformation("Node {Address} successfully joined cluster with {MembershipSize} members, ConfigId={ConfigId}",
            RapidUtils.Loggable(Address), CurrentView.Size, CurrentView.ConfigurationId);
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
    /// Gets the observable for subscribing to cluster events.
    /// Multiple subscribers receive the same events through multicast.
    /// </summary>
    public IObservable<ClusterEventNotification> Events =>
        _membershipService?.Events ?? throw new InvalidOperationException("Membership service has not been initialized.");

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
        // Mark as disposed first to prevent any rejoin attempts (MembershipService checks _disposed)
        _disposed = true;
        
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
        // This will cause consensus instances to complete and prevent rejoins
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
        // This will cause consensus instances to complete and prevent rejoins
        _sharedResources.StartShutdown();

        if (_membershipService != null)
        {
            _membershipService.Shutdown();
        }
        // Note: We cannot await DisposeAsync here, so we skip the async dispose
        // The shutdown above should have cancelled everything
        _sharedResources.Dispose();
        MessagingClient.Dispose();
        _harness.UnregisterNode(this);

        _logger.LogDebug("Node {Address} disposed", RapidUtils.Loggable(Address));
    }

    /// <summary>
    /// Simple broadcaster factory that creates UnicastToAllBroadcaster instances.
    /// </summary>
    private sealed class UnicastToAllBroadcasterFactory(IMessagingClient messagingClient) : IBroadcasterFactory
    {
        public IBroadcaster Create() => new UnicastToAllBroadcaster(messagingClient);
    }
}
