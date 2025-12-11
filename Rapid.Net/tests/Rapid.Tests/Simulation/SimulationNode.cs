using System.Diagnostics.CodeAnalysis;
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
/// Lifetime is managed by the SimulationHarness via Destroy() - do not implement IDisposable.
/// </summary>
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable",
    Justification = "Lifetime is managed by SimulationHarness.Destroy() to avoid CA2000 warnings in tests")]
internal sealed class SimulationNode
{
    private readonly SimulationHarness _harness;
    private readonly NodeSimulationContext _context;
    private readonly RapidProtocolOptions _protocolOptions;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ILogger<SimulationNode> _logger;
    private readonly MembershipService _membershipService;

    // These are mutable because they need to be recreated during rejoin
    private SharedResources _sharedResources;
    private PingPongFailureDetectorFactory _failureDetectorFactory;
    private IConsensusCoordinatorFactory _consensusCoordinatorFactory;
    private CutDetectorFactory _cutDetectorFactory;
    private MembershipViewAccessor _viewAccessor;
    private ILogger<MembershipService> _membershipServiceLogger;
    private bool _disposed;
    private bool _initialized;

    /// <summary>
    /// Gets the endpoint address of this node.
    /// </summary>
    public Endpoint Address { get; }

    /// <summary>
    /// Gets the simulation context for this node.
    /// </summary>
    public NodeSimulationContext Context => _context;

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
    public bool IsInitialized => _initialized;

    /// <summary>
    /// Gets the membership size of this node's view.
    /// </summary>
    public int MembershipSize => _viewAccessor.CurrentView.Size;

    /// <summary>
    /// Gets the messaging client for testing purposes.
    /// </summary>
    internal InMemoryMessagingClient MessagingClient { get; }

    #region Per-Node Execution Control

    /// <summary>
    /// Gets whether this node is currently suspended.
    /// </summary>
    public bool IsSuspended => _context.State == NodeSimulationState.Suspended;

    /// <summary>
    /// Suspends this node, preventing it from executing tasks.
    /// Messages sent to the node will be queued but not processed until resumed.
    /// </summary>
    public void Suspend()
    {
        _context.Suspend();
        _harness.LogNodeEvent(this, SimulationEventType.NodeSuspended, "Node suspended");
    }

    /// <summary>
    /// Resumes this node, allowing it to execute tasks again.
    /// </summary>
    public void Resume()
    {
        _context.Resume();
        _harness.LogNodeEvent(this, SimulationEventType.NodeResumed, "Node resumed");
    }

    /// <summary>
    /// Suspends this node for the specified duration, then automatically resumes it.
    /// The resume occurs when simulated time advances past the duration.
    /// </summary>
    /// <param name="duration">How long to suspend the node (in simulated time).</param>
    public void SuspendFor(TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);

        Suspend();

        // Schedule auto-resume on the harness queue
        _harness.TaskQueue.EnqueueAfter(() => Resume(), duration);

        _harness.LogNodeEvent(this, SimulationEventType.NodeSuspended, $"Node suspended for {duration}");
    }

    /// <summary>
    /// Executes one ready task from this node's queue.
    /// </summary>
    /// <returns>True if a task was executed; false if no tasks are ready or the node is suspended.</returns>
    public bool Step()
    {
        if (_context.Step())
        {
            _harness.IncrementLogicalTime();
            return true;
        }
        return false;
    }

    #endregion

    internal SimulationNode(
        SimulationHarness harness,
        NodeSimulationContext context,
        Endpoint address,
        Endpoint? seedAddress,
        Metadata? metadata,
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

        // Create the MembershipService (but don't initialize it yet)
        var rapidOptions = new RapidOptions
        {
            ListenAddress = address,
            SeedAddress = seedAddress,
            Metadata = metadata ?? new Metadata()
        };
        var broadcasterFactory = new UnicastToAllBroadcasterFactory(MessagingClient);
        _membershipService = new MembershipService(
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
    }

    /// <summary>
    /// Initializes the membership service (completes the join or cluster start).
    /// </summary>
    internal async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _membershipService.InitializeAsync(cancellationToken).ConfigureAwait(true);
        _initialized = true;

        _logger.LogInformation("Node {Address} initialized with {MembershipSize} members, ConfigId={ConfigId}",
            RapidUtils.Loggable(Address), CurrentView.Size, CurrentView.ConfigurationId);
    }

    /// <summary>
    /// Handles an incoming request from another node.
    /// </summary>
    internal async Task<RapidResponse> HandleRequestAsync(RapidRequest request, CancellationToken cancellationToken)
    {
        _logger.LogTrace("Node {Address} handling request of type {RequestType}",
            RapidUtils.Loggable(Address), request.ContentCase);

        return await _membershipService.HandleMessageAsync(request, cancellationToken).ConfigureAwait(true);
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

    /// <summary>
    /// Destroys the node and releases all resources. Called by the harness during node removal or disposal.
    /// </summary>
    internal void Destroy()
    {
        if (_disposed) return;
        _disposed = true;

        _logger.LogDebug("Node {Address} destroying", RapidUtils.Loggable(Address));

        // First shutdown shared resources to cancel the ShuttingDownToken
        // This will cause consensus instances to complete and prevent rejoins
        _sharedResources.StartShutdown();

        _membershipService.Shutdown();

        _sharedResources.Dispose();
        MessagingClient.Dispose();

        _logger.LogDebug("Node {Address} destroyed", RapidUtils.Loggable(Address));
    }

    /// <summary>
    /// Simple broadcaster factory that creates UnicastToAllBroadcaster instances.
    /// </summary>
    private sealed class UnicastToAllBroadcasterFactory(IMessagingClient messagingClient) : IBroadcasterFactory
    {
        public IBroadcaster Create() => new UnicastToAllBroadcaster(messagingClient);
    }
}
