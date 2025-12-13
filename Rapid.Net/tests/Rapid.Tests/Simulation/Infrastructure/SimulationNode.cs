using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Rapid.Messaging;
using Rapid.Monitoring;
using Rapid.Pb;
using Rapid.Tests.Simulation.Infrastructure.Logging;

namespace Rapid.Tests.Simulation.Infrastructure;

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
    private readonly SimulationNodeContext _context;
    private readonly RapidProtocolOptions _protocolOptions;
    private readonly ILoggerFactory _loggerFactory;
    private readonly SimulationNodeLogger _log;
    private readonly MembershipService _membershipService;
    private readonly SharedResources _sharedResources;
    private readonly PingPongFailureDetectorFactory _failureDetectorFactory;
    private readonly IConsensusCoordinatorFactory _consensusCoordinatorFactory;
    private readonly CutDetectorFactory _cutDetectorFactory;
    private readonly MembershipViewAccessor _viewAccessor;
    private readonly ILogger<MembershipService> _membershipServiceLogger;
    private readonly CancellationTokenSource _disposeCts = new();
    private bool _disposed;

    /// <summary>
    /// Gets the endpoint address of this node.
    /// </summary>
    public Endpoint Address { get; }

    /// <summary>
    /// Gets the simulation context for this node.
    /// </summary>
    public SimulationNodeContext Context => _context;

    /// <summary>
    /// Gets the simulation random instance for this node.
    /// </summary>
    public SimulationRandom Random => _context.Random;

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
    public bool IsInitialized { get; private set; }

    /// <summary>
    /// Gets the membership size of this node's view.
    /// </summary>
    public int MembershipSize => _viewAccessor.CurrentView.Size;

    /// <summary>
    /// Gets the messaging client for testing purposes.
    /// </summary>
    internal InMemoryMessagingClient MessagingClient { get; }

    /// <summary>
    /// Gets whether this node is currently suspended.
    /// </summary>
    public bool IsSuspended => _context.State == SimulationNodeState.Suspended;

    /// <summary>
    /// Gets the cancellation token that is triggered when this node is being torn down.
    /// </summary>
    internal CancellationToken TeardownCancellationToken => _disposeCts.Token;

    /// <summary>
    /// Suspends this node, preventing it from executing tasks.
    /// Messages sent to the node will be queued but not processed until resumed.
    /// </summary>
    public void Suspend()
    {
        _context.Suspend();
        _log.NodeSuspended();
    }

    /// <summary>
    /// Resumes this node, allowing it to execute tasks again.
    /// </summary>
    public void Resume()
    {
        _context.Resume();
        _log.NodeResumed();
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
        _harness.TaskQueue.EnqueueAfter(Resume, duration);

        _log.NodeSuspendedFor(duration);
    }

    /// <summary>
    /// Executes one ready task from this node's queue.
    /// </summary>
    /// <returns>True if a task was executed; false if no tasks are ready or the node is suspended.</returns>
    public bool Step()
    {
        return _context.Step();
    }


    internal SimulationNode(
        SimulationHarness harness,
        Endpoint address,
        Endpoint? seedAddress,
        Metadata? metadata,
        RapidProtocolOptions? protocolOptions,
        ILoggerFactory? loggerFactory)
    {
        _harness = harness;
        _context = new SimulationNodeContext(harness.Clock, harness.Guard, harness.CreateDerivedRandom());
        Address = address;

        // Wrap the logger factory to prepend the node name to all log messages
        var baseLoggerFactory = loggerFactory ?? harness.LoggerFactory;
        var nodeName = $"{address.Hostname.ToStringUtf8()}:{address.Port}";
        _loggerFactory = baseLoggerFactory != null
            ? new NodePrefixedLoggerFactory(baseLoggerFactory, nodeName)
            : NullLoggerFactory.Instance;
        _log = new SimulationNodeLogger(_loggerFactory.CreateLogger<SimulationNode>());
        _membershipServiceLogger = _loggerFactory.CreateLogger<MembershipService>();

        // Create protocol options
        _protocolOptions = protocolOptions ?? new RapidProtocolOptions();

        // Create shared resources with the node's time provider, task scheduler, and harness teardown token
        _sharedResources = new SharedResources(
            _context.TimeProvider,
            _context.TaskScheduler,
            _context.Random,
            _context.Random.NextGuid,
            harness.TeardownCancellationToken);

        // Create in-memory messaging client using GrpcTimeout from protocol options.
        // For tests with suspended nodes requiring Classic Paxos fallback,
        // a longer timeout (e.g., 30 seconds) may be needed to allow
        // for the random jitter delay before Classic Paxos starts.
        MessagingClient = new InMemoryMessagingClient(harness, this, address, _protocolOptions);

        // Create view accessor
        _viewAccessor = new MembershipViewAccessor();

        // Create failure detector factory
        var failureDetectorLogger = _loggerFactory.CreateLogger<PingPongFailureDetector>();
        _failureDetectorFactory = new PingPongFailureDetectorFactory(
            address,
            MessagingClient,
            _sharedResources,
            Options.Create(_protocolOptions),
            failureDetectorLogger);

        // Create consensus coordinator factory
        var consensusCoordinatorLogger = _loggerFactory.CreateLogger<ConsensusCoordinator>();
        var fastPaxosLogger = _loggerFactory.CreateLogger<FastPaxos>();
        var paxosLogger = _loggerFactory.CreateLogger<Paxos>();
        _consensusCoordinatorFactory = new ConsensusCoordinatorFactory(
            MessagingClient,
            Options.Create(_protocolOptions),
            _sharedResources,
            consensusCoordinatorLogger,
            fastPaxosLogger,
            paxosLogger);

        // Create cut detector factory
        var simpleCutDetectorLogger = _loggerFactory.CreateLogger<SimpleCutDetector>();
        var multiNodeCutDetectorLogger = _loggerFactory.CreateLogger<MultiNodeCutDetector>();
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
        IsInitialized = true;

        _log.NodeInitialized(RapidUtils.Loggable(Address), CurrentView.Size, CurrentView.ConfigurationId);
    }

    /// <summary>
    /// Handles an incoming request from another node.
    /// </summary>
    internal async Task<RapidResponse> HandleRequestAsync(RapidRequest request, CancellationToken cancellationToken)
    {
        _log.HandlingRequest(RapidUtils.Loggable(Address), request.ContentCase);

        // Link the caller's cancellation token with our disposal token so that
        // in-flight requests complete when this node is disposed
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        return await _membershipService.HandleMessageAsync(request, linkedCts.Token).ConfigureAwait(true);
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
    /// Gracefully stops the node by notifying observers and waiting for background tasks.
    /// The node can still receive messages after this method returns.
    /// Call <see cref="DisposeAsync"/> after unregistering from the network to release resources.
    /// </summary>
    public async Task StopAsync()
    {
        if (_disposed) return;

        _log.NodeLeaving(RapidUtils.Loggable(Address));

        // Graceful stop: notify observers and wait for background tasks
        // Keep MessagingClient alive so we can still participate in consensus
        await _membershipService.StopAsync().ConfigureAwait(true);

        _log.NodeLeftGracefully(RapidUtils.Loggable(Address));
    }

    /// <summary>
    /// Disposes the node's resources. Should be called after unregistering from the network.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        // Cancel any in-flight requests first so they complete promptly
#pragma warning disable CA1849 // CancelAsync posts to SynchronizationContext which breaks simulation determinism
        _disposeCts.Cancel();
#pragma warning restore CA1849

        await _membershipService.DisposeAsync().ConfigureAwait(true);
        await MessagingClient.DisposeAsync().ConfigureAwait(true);

        _disposeCts.Dispose();
    }

    /// <summary>
    /// Simple broadcaster factory that creates UnicastToAllBroadcaster instances.
    /// </summary>
    private sealed class UnicastToAllBroadcasterFactory(IMessagingClient messagingClient) : IBroadcasterFactory
    {
        public IBroadcaster Create() => new UnicastToAllBroadcaster(messagingClient);
    }
}
