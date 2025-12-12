using Microsoft.Extensions.Logging;

namespace Rapid.Tests.Simulation;

/// <summary>
/// Unified simulation harness for fully deterministic testing of Rapid clusters.
/// 
/// Provides:
/// - Deterministic task scheduling via per-node <see cref="SimulationTaskScheduler"/> instances
/// - Controlled time via shared <see cref="SimulationClock"/>
/// - Seeded random number generation via <see cref="SimulationRandom"/>
/// - Simulated network with partition injection via <see cref="SimulationNetwork"/>
/// - Node lifecycle management (create, join, crash, leave)
/// - Per-node execution control (suspend, resume, step)
/// - Event logging for debugging and verification
/// - Simulation driving APIs (Step, RunUntil, DriveToCompletion)
/// </summary>
internal sealed class SimulationHarness : IAsyncDisposable
{
    private readonly SortedDictionary<string, SimulationNode> _nodes = new(StringComparer.Ordinal);
    private readonly SingleThreadedGuard _lock = new();
    private readonly List<SimulationEvent> _eventLog = [];
    private readonly ILogger<SimulationHarness>? _logger;
    private readonly InMemoryLoggerProvider _inMemoryLoggerProvider;
    private bool _disposed;

    /// <summary>
    /// Maximum size in bytes for full log attachment (1 MB).
    /// If logs exceed this size, only Information level and above will be attached.
    /// </summary>
    private const long MaxFullLogSizeBytes = 100 * 1024 * 1024;

    /// <summary>
    /// Creates a new simulation harness with the specified seed.
    /// Logs are written to a unique file per simulation and attached to the test context.
    /// </summary>
    /// <param name="seed">The seed for deterministic random number generation.</param>
    public SimulationHarness(int seed)
    {
        var context = TestContext.Current;
        TeardownCancellationToken = context.CancellationToken;
        Seed = seed;
        StartDateTime = DateTimeOffset.UtcNow;

        Random = new SimulationRandom(seed);

        // Create shared clock and harness-level queue
        Clock = new SimulationClock(StartDateTime);
        TaskQueue = new SimulationTaskQueue(Clock, _lock);
        TaskScheduler = new SimulationTaskScheduler(TaskQueue);

        // Create time provider using harness queue (for GetUtcNow queries)
        _timeProvider = new SimulationTimeProvider(TaskQueue, Clock);

        Network = new SimulationNetwork(this, Random);

        // Create logger factory with in-memory provider for attachment to test context
        _inMemoryLoggerProvider = new InMemoryLoggerProvider(_timeProvider);
        LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
        {
            builder.AddProvider(_inMemoryLoggerProvider);
            builder.SetMinimumLevel(LogLevel.Debug);
        });

        _logger = LoggerFactory.CreateLogger<SimulationHarness>();
        Network.SetLogger(LoggerFactory.CreateLogger<SimulationNetwork>());

        LogEvent(SimulationEventType.HarnessCreated, $"Seed: {seed}");
    }

    #region Core Components

    /// <summary>
    /// Gets the seed used for this harness.
    /// </summary>
    public int Seed { get; }

    /// <summary>
    /// Gets the starting date/time for the simulation.
    /// </summary>
    public DateTimeOffset StartDateTime { get; }

    /// <summary>
    /// Gets the logger factory.
    /// </summary>
    public ILoggerFactory LoggerFactory { get; }

    /// <summary>
    /// Gets the simulation random instance.
    /// </summary>
    public SimulationRandom Random { get; }

    /// <summary>
    /// Gets the shared simulation clock.
    /// </summary>
    public SimulationClock Clock { get; }

    private readonly SimulationTimeProvider _timeProvider;

    /// <summary>
    /// Gets the simulation time provider.
    /// </summary>
    public TimeProvider TimeProvider => _timeProvider;

    /// <summary>
    /// Gets the simulation network.
    /// </summary>
    public SimulationNetwork Network { get; }

    /// <summary>
    /// Gets all nodes in the simulation (snapshot).
    /// </summary>
    public IReadOnlyList<SimulationNode> Nodes => [.. _nodes.Values];

    /// <summary>
    /// Gets the current logical time (number of tasks executed).
    /// </summary>
    public long LogicalTime { get; private set; }

    /// <summary>
    /// Gets the harness-level task queue for scheduling general simulation work.
    /// For node-specific work, use <see cref="GetNodeContext"/> to get the node's queue.
    /// </summary>
    public SimulationTaskQueue TaskQueue { get; }

    /// <summary>
    /// Gets the harness-level task scheduler for scheduling general simulation work.
    /// For node-specific work, use <see cref="GetNodeContext"/> to get the node's scheduler.
    /// </summary>
    public SimulationTaskScheduler TaskScheduler { get; }

    /// <summary>
    /// Gets the harness-level synchronization context.
    /// Install this on the test thread to capture async continuations in the simulation.
    /// </summary>
    public SimulationSynchronizationContext SynchronizationContext => TaskQueue.SynchronizationContext;

    /// <summary>
    /// Gets the single-threaded guard used to detect accidental concurrent access.
    /// This guard should be shared with all simulation components to ensure single-threaded execution.
    /// </summary>
    public SingleThreadedGuard Guard => _lock;

    #endregion

    #region Node Context Management

    /// <summary>
    /// Gets the simulation context for a specific node.
    /// </summary>
    /// <param name="node">The node to get the context for.</param>
    /// <returns>The node's simulation context.</returns>
    public SimulationNodeContext GetNodeContext(SimulationNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.Context;
    }

    #endregion

    #region Per-Node Execution Control (Internal Helpers)

    /// <summary>
    /// Logs an event related to a specific node. Called by SimulationNode.
    /// </summary>
    internal void LogNodeEvent(SimulationNode node, SimulationEventType type, string description)
    {
        LogEvent(type, $"[{RapidUtils.Loggable(node.Address)}] {description}");
    }

    /// <summary>
    /// Increments the logical time counter. Called by SimulationNode.Step().
    /// </summary>
    internal void IncrementLogicalTime()
    {
        LogicalTime++;
    }

    #endregion

    #region Node Registry (Internal)

    /// <summary>
    /// Registers a node with the simulation.
    /// </summary>
    internal void RegisterNode(SimulationNode node)
    {
        var key = RapidUtils.Loggable(node.Address);
        using var _ = _lock.Enter();
        if (!_nodes.TryAdd(key, node))
        {
            throw new InvalidOperationException($"Node with address {key} already exists");
        }
    }

    /// <summary>
    /// Unregisters a node from the simulation.
    /// </summary>
    internal void UnregisterNode(SimulationNode node)
    {
        var key = RapidUtils.Loggable(node.Address);
        using var _ = _lock.Enter();
        _nodes.Remove(key);
        node.Context.Clear();
    }

    /// <summary>
    /// Gets a node by its address string.
    /// </summary>
    internal SimulationNode? GetNode(string address)
    {
        using var _ = _lock.Enter();
        _nodes.TryGetValue(address, out var node);
        return node;
    }

    /// <summary>
    /// Creates a new deterministic random instance derived from the harness's random.
    /// </summary>
    internal SimulationRandom CreateDerivedRandom()
    {
        using var _ = _lock.Enter();
#pragma warning disable CA5394 // Do not use insecure randomness
        return new SimulationRandom(Random.Next());
#pragma warning restore CA5394 // Do not use insecure randomness
    }

    #endregion

    #region Node Lifecycle

    /// <summary>
    /// Creates a node without initializing it (for testing edge cases).
    /// The node is registered but not started or joined to any cluster.
    /// </summary>
    /// <param name="nodeId">The node ID.</param>
    /// <param name="seedNode">Optional seed node for joining. If null, the node will start its own cluster when initialized.</param>
    /// <param name="options">Optional protocol options.</param>
    public SimulationNode CreateUninitializedNode(int nodeId, SimulationNode? seedNode = null, RapidProtocolOptions? options = null)
    {
        var opts = ConfigureOptions(options);
        var address = RapidUtils.HostFromParts("node", nodeId);
        var node = new SimulationNode(this, address, seedNode?.Address, metadata: null, opts, LoggerFactory);
        RegisterNode(node);
        LogEvent(SimulationEventType.NodeCreated, $"Uninitialized node {nodeId} created");
        return node;
    }

    /// <summary>
    /// Creates and starts a new seed node.
    /// </summary>
    public SimulationNode CreateSeedNode(int nodeId = 0, RapidProtocolOptions? options = null)
    {
        var opts = ConfigureOptions(options);
        var address = RapidUtils.HostFromParts("node", nodeId);
        var node = new SimulationNode(this, address, seedAddress: null, metadata: null, opts, LoggerFactory);
        RegisterNode(node);

        // For seed nodes, initialization is synchronous (no network I/O needed),
        // but we still drive it through DriveToCompletion for consistency.
        DriveToCompletion(() => node.InitializeAsync());

        LogEvent(SimulationEventType.NodeCreated, $"Seed node {nodeId} created");
        return node;
    }

    /// <summary>
    /// Creates and joins a new node to the cluster through the specified seed.
    /// Drives the simulation to complete the join.
    /// </summary>
    public SimulationNode CreateJoinerNode(
        SimulationNode seedNode,
        int nodeId,
        RapidProtocolOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(seedNode);
        var opts = ConfigureOptions(options);
        var address = RapidUtils.HostFromParts("node", nodeId);
        var node = new SimulationNode(this, address, seedNode.Address, metadata: null, opts, LoggerFactory);
        RegisterNode(node);

        LogEvent(SimulationEventType.NodeJoining, $"Node {nodeId} joining via seed");

        // Drive the initialization to completion
        DriveToCompletion(() => node.InitializeAsync());

        LogEvent(SimulationEventType.NodeJoined, $"Node {nodeId} joined cluster");
        return node;
    }

    /// <summary>
    /// Creates a cluster of the specified size using sequential joins.
    /// Each node joins and waits for consensus before the next node joins.
    /// This results in O(N) consensus rounds but guarantees deterministic behavior.
    /// </summary>
    /// <remarks>
    /// For large clusters (50+ nodes), consider using <see cref="CreateClusterParallel"/>
    /// which batches joins together for O(log N) consensus rounds.
    /// </remarks>
    public IReadOnlyList<SimulationNode> CreateCluster(int size, RapidProtocolOptions? options = null)
    {
        if (size < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "Cluster size must be at least 1");
        }

        var result = new List<SimulationNode>(size);

        // Create seed node
        var seedNode = CreateSeedNode(0, options);
        result.Add(seedNode);

        // Create joiner nodes
        for (var i = 1; i < size; i++)
        {
            var joiner = CreateJoinerNode(seedNode, i, options);
            result.Add(joiner);
        }

        return result;
    }

    /// <summary>
    /// Creates a cluster of the specified size using parallel joins.
    /// Multiple nodes join concurrently, allowing the multi-node cut detection
    /// to batch them into fewer consensus rounds (O(log N) instead of O(N)).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This matches the behavior described in the Rapid paper where 2000 nodes
    /// were bootstrapped with only 8 configuration changes. The multi-node cut
    /// detection aggregates pending JOIN alerts and proposes them together.
    /// </para>
    /// <para>
    /// For small clusters or when deterministic single-node-at-a-time behavior
    /// is needed, use <see cref="CreateCluster"/> instead.
    /// </para>
    /// </remarks>
    /// <param name="size">The number of nodes in the cluster.</param>
    /// <param name="options">Optional protocol options.</param>
    /// <param name="batchSize">
    /// Number of nodes to initiate joining simultaneously. Default is 0 which means all nodes.
    /// Smaller batch sizes provide more control over join ordering while still enabling batching.
    /// </param>
    /// <param name="maxIterationsPerBatch">
    /// Maximum simulation iterations to run for each batch of joins. Default is 100000.
    /// Larger clusters may need higher values (e.g., 500000 for 200+ nodes).
    /// </param>
    /// <returns>List of all nodes in the cluster.</returns>
    public IReadOnlyList<SimulationNode> CreateClusterParallel(
        int size,
        RapidProtocolOptions? options = null,
        int batchSize = 0,
        int maxIterationsPerBatch = 100000)
    {
        if (size < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "Cluster size must be at least 1");
        }

        var result = new List<SimulationNode>(size);

        // Create seed node first
        var seedNode = CreateSeedNode(0, options);
        result.Add(seedNode);

        if (size == 1)
        {
            return result;
        }

        // Use all remaining nodes as batch size if not specified or invalid
        var effectiveBatchSize = batchSize <= 0 ? size - 1 : batchSize;

        // Process nodes in batches
        var remainingNodes = size - 1;
        var nodeId = 1;

        while (remainingNodes > 0)
        {
            var currentBatchSize = Math.Min(effectiveBatchSize, remainingNodes);
            var batchNodes = new List<SimulationNode>(currentBatchSize);

            // Create all nodes in this batch first (with seedAddress so MembershipService is ready)
            for (var i = 0; i < currentBatchSize; i++)
            {
                var opts = ConfigureOptions(options);
                var address = RapidUtils.HostFromParts("node", nodeId++);
                var node = new SimulationNode(this, address, seedNode.Address, metadata: null, opts, LoggerFactory);
                RegisterNode(node);

                LogEvent(SimulationEventType.NodeJoining, $"Node {node.Address} joining via seed (parallel batch)");

                batchNodes.Add(node);
            }

            // Drive the simulation until all joins in this batch complete.
            // IMPORTANT: Join tasks must be started inside DriveToCompletion so they
            // capture the simulation's SynchronizationContext for their continuations.
            DriveToCompletion(() =>
            {
                var joinTasks = batchNodes.Select(node => node.InitializeAsync()).ToList();
                return Task.WhenAll(joinTasks);
            }, maxIterationsPerBatch);

            // Log completion
            foreach (var node in batchNodes)
            {
                LogEvent(SimulationEventType.NodeJoined, $"Node {node.Address} joined cluster (parallel batch)");
            }

            result.AddRange(batchNodes);
            remainingNodes -= currentBatchSize;
        }

        return result;
    }

    /// <summary>
    /// Crashes a node (simulates sudden failure).
    /// </summary>
    public void CrashNode(SimulationNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        UnregisterNode(node);
        node.Destroy();
        LogEvent(SimulationEventType.NodeCrashed, $"Node crashed");
    }

    /// <summary>
    /// Gracefully removes a node from the cluster.
    /// The leaving node must participate in the consensus round that removes it,
    /// so we keep it alive until consensus completes and all remaining nodes converge.
    /// </summary>
    public void RemoveNodeGracefully(SimulationNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        LogEvent(SimulationEventType.NodeLeaving, $"Node beginning graceful leave");

        var remainingNodes = Nodes.Where(n => n != node).ToList();
        var targetSize = remainingNodes.Count;

        // Drive the leave operation to completion (sends LeaveMessages to observers)
        DriveToCompletion(node.LeaveAsync);

        // The leaving node must remain active to participate in consensus.
        // Run the simulation until all remaining nodes converge to the new size.
        var converged = RunUntil(
            () => remainingNodes.All(n => n.MembershipSize == targetSize),
            maxIterations: 100000);

        if (!converged)
        {
            // Log current state for debugging
            var sizes = string.Join(", ", remainingNodes.Select(n => n.MembershipSize));
            _logger?.LogWarning(
                "RemoveNodeGracefully: remaining nodes did not converge to size {TargetSize}. Current sizes: [{Sizes}]",
                targetSize, sizes);
        }

        // Now that consensus is complete, clean up the leaving node
        UnregisterNode(node);
        node.Destroy();

        LogEvent(SimulationEventType.NodeLeft, $"Node left gracefully");
    }

    /// <summary>
    /// Gracefully removes multiple nodes from the cluster in parallel.
    /// This allows the multi-node cut detection to batch multiple leaves into
    /// fewer consensus rounds, similar to how parallel joins work.
    /// 
    /// All leaving nodes initiate their leave concurrently, enabling the
    /// batching mechanism to combine their alerts into single view changes.
    /// </summary>
    /// <param name="nodesToRemove">The nodes to remove from the cluster.</param>
    /// <returns>The number of configuration changes that occurred during the parallel leave.</returns>
    public int RemoveNodesGracefullyParallel(IReadOnlyList<SimulationNode> nodesToRemove)
    {
        ArgumentNullException.ThrowIfNull(nodesToRemove);
        if (nodesToRemove.Count == 0)
        {
            return 0;
        }

        // Get the starting configuration version to measure changes
        var remainingNodes = Nodes.Where(n => !nodesToRemove.Contains(n)).ToList();
        var startingConfigVersion = remainingNodes[0].CurrentView.ConfigurationId.Version;
        var targetSize = remainingNodes.Count;

        LogEvent(SimulationEventType.NodeLeaving, $"{nodesToRemove.Count} nodes beginning parallel graceful leave");

        foreach (var node in nodesToRemove)
        {
            LogEvent(SimulationEventType.NodeLeaving, $"Node {node.Address} initiating leave (parallel batch)");
        }

        // Drive the simulation until all leave operations complete.
        // IMPORTANT: Leave tasks must be started inside DriveToCompletion so they
        // capture the simulation's SynchronizationContext for their continuations.
        DriveToCompletion(() =>
        {
            var leaveTasks = nodesToRemove.Select(node => node.LeaveAsync());
            return Task.WhenAll(leaveTasks);
        });

        // Wait for remaining nodes to converge to the new size
        var converged = RunUntil(
            () => remainingNodes.All(n => n.MembershipSize == targetSize),
            maxIterations: 500000);

        if (!converged)
        {
            var sizes = string.Join(", ", remainingNodes.Select(n => n.MembershipSize));
            _logger?.LogWarning(
                "RemoveNodesGracefullyParallel: remaining nodes did not converge to size {TargetSize}. Current sizes: [{Sizes}]",
                targetSize, sizes);
        }

        // Clean up leaving nodes
        foreach (var node in nodesToRemove)
        {
            UnregisterNode(node);
            node.Destroy();
            LogEvent(SimulationEventType.NodeLeft, $"Node {node.Address} left gracefully (parallel batch)");
        }

        // Calculate configuration changes
        var endingConfigVersion = remainingNodes[0].CurrentView.ConfigurationId.Version;
        var configChanges = (int)(endingConfigVersion - startingConfigVersion);

        LogEvent(SimulationEventType.NodeLeft,
            $"Parallel leave completed: {nodesToRemove.Count} nodes removed in {configChanges} configuration changes");

        return configChanges;
    }

    private static RapidProtocolOptions ConfigureOptions(RapidProtocolOptions? options)
    {
        var opts = options ?? new RapidProtocolOptions();
        opts.FailureDetectorInterval = TimeSpan.FromSeconds(1);
        return opts;
    }

    #endregion

    #region Network Partitions

    /// <summary>
    /// Isolates a node from the cluster.
    /// </summary>
    public void IsolateNode(SimulationNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var addr = RapidUtils.Loggable(node.Address);
        Network.IsolateNode(addr);
        LogEvent(SimulationEventType.NodeIsolated, $"Node isolated");
    }

    /// <summary>
    /// Reconnects an isolated node.
    /// </summary>
    public void ReconnectNode(SimulationNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var addr = RapidUtils.Loggable(node.Address);
        Network.ReconnectNode(addr);
        LogEvent(SimulationEventType.NodeReconnected, $"Node reconnected");
    }

    /// <summary>
    /// Creates a partition between two nodes.
    /// </summary>
    public void PartitionNodes(SimulationNode node1, SimulationNode node2)
    {
        ArgumentNullException.ThrowIfNull(node1);
        ArgumentNullException.ThrowIfNull(node2);
        var addr1 = RapidUtils.Loggable(node1.Address);
        var addr2 = RapidUtils.Loggable(node2.Address);
        Network.CreateBidirectionalPartition(addr1, addr2);
        LogEvent(SimulationEventType.PartitionCreated, $"Partition between nodes");
    }

    /// <summary>
    /// Heals a partition between two nodes.
    /// </summary>
    public void HealPartition(SimulationNode node1, SimulationNode node2)
    {
        using var _ = _lock.Enter();
        ArgumentNullException.ThrowIfNull(node1);
        ArgumentNullException.ThrowIfNull(node2);
        var addr1 = RapidUtils.Loggable(node1.Address);
        var addr2 = RapidUtils.Loggable(node2.Address);
        Network.HealBidirectionalPartition(addr1, addr2);
        LogEvent(SimulationEventType.PartitionHealed, $"Partition healed between nodes");
    }

    #endregion

    #region Simulation Driving

    /// <summary>
    /// Maximum simulated time to advance before considering the simulation stuck.
    /// Default is 10 minutes of simulated time.
    /// </summary>
    public TimeSpan MaxSimulatedTimeAdvance { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Runs the simulation until the specified condition is met.
    /// </summary>
    public bool RunUntil(Func<bool> condition, int maxIterations = 100000)
    {
        ArgumentNullException.ThrowIfNull(condition);

        return RunUntilCore(condition, maxIterations);
    }

    /// <summary>
    /// Core implementation of RunUntil without context installation (for internal use).
    /// Uses round-robin execution across all non-suspended node contexts, plus the harness queue.
    /// </summary>
    private bool RunUntilCore(Func<bool> condition, int maxIterations)
    {
        using var _ = _lock.Enter();
        var startTime = TimeProvider.GetUtcNow();
        var maxEndTime = MaxSimulatedTimeAdvance;
        var timeAdvanceCount = 0;

        for (var i = 0; i < maxIterations; i++)
        {
            // Check for teardown cancellation
            if (TeardownCancellationToken.IsCancellationRequested)
            {
                LogEvent(SimulationEventType.MaxStepsReached, "Teardown cancellation requested - exiting simulation loop");
                return false;
            }

            if (condition())
            {
                LogEvent(SimulationEventType.ConditionMet, $"Condition met after {i} iterations");
                return true;
            }

            // Try to execute one ready task using round-robin across all sources
            if (RunOneTaskRoundRobin())
            {
                LogicalTime++;
                timeAdvanceCount = 0; // Reset time advance counter when real work happens
                continue;
            }

            // No tasks to execute - need to advance time
            var nextScheduledTime = GetNextWaitingDueTime();
            if (!nextScheduledTime.HasValue)
            {
                // No more scheduled work - simulation is idle and cannot make progress
                LogEvent(SimulationEventType.MaxStepsReached,
                    $"Simulation is idle with no pending work - condition cannot be met. " +
                    $"Iterations: {i}, Simulated time: {TimeProvider.GetUtcNow():O}");
                return false;
            }

            // Check if we've been advancing time without making progress
            var timeDelta = nextScheduledTime.Value - Clock.UtcNow;
            if (timeDelta > maxEndTime)
            {
                LogEvent(SimulationEventType.MaxStepsReached,
                    $"Simulation appears stuck: exceeded max simulated time ({MaxSimulatedTimeAdvance}). " +
                    $"Start: {startTime:O}, Current: {TimeProvider.GetUtcNow():O}, Next scheduled time delta: {timeDelta}");
                return false;
            }

            // Advance time to the next scheduled task
            if (timeDelta > TimeSpan.Zero)
            {
                Clock.Advance(timeDelta);
            }
            timeAdvanceCount++;

            // Safety check: if we've advanced time many times without executing tasks, we might be stuck
            if (timeAdvanceCount > 10000)
            {
                LogEvent(SimulationEventType.MaxStepsReached,
                    $"Simulation appears stuck: {timeAdvanceCount} consecutive time advances without task execution");
                return false;
            }
        }

        LogEvent(SimulationEventType.MaxStepsReached, $"Max iterations ({maxIterations}) reached");
        return false;
    }

    /// <summary>
    /// Attempts to execute one ready task using round-robin across all node contexts and the harness queue.
    /// Returns true if a task was executed.
    /// </summary>
    private bool RunOneTaskRoundRobin()
    {
        using var _ = _lock.Enter();

        // Try the harness queue (for scheduled operations like auto-resume)
        if (TaskQueue.RunOnce())
        {
            return true;
        }

        // Try to execute from non-suspended node contexts (round-robin)
        foreach (var node in Nodes)
        {
            var context = node.Context;
            if (context.State == SimulationNodeState.Running && context.Step())
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets the earliest due time across all queues (node contexts + harness queue).
    /// </summary>
    private DateTimeOffset? GetNextWaitingDueTime()
    {
        using var _ = _lock.Enter();
        return Nodes.Select(n => n.Context.NextWaitingDueTime).Concat([TaskQueue.NextWaitingDueTime]).Min();
    }

    /// <summary>
    /// Runs until all nodes have the expected membership size.
    /// </summary>
    public bool RunUntilConverged(int expectedSize, int maxIterations = 100000) =>
        RunUntil(() => Nodes.All(n => n.MembershipSize == expectedSize), maxIterations);

    /// <summary>
    /// Runs until the specified nodes have the expected membership size.
    /// Use this overload when some nodes (e.g., isolated/partitioned nodes) should be excluded from the check.
    /// </summary>
    public bool RunUntilConverged(IEnumerable<SimulationNode> nodes, int expectedSize, int maxIterations = 100000)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var nodeList = nodes.ToList();
        return RunUntil(() => nodeList.All(n => n.MembershipSize == expectedSize), maxIterations);
    }

    /// <summary>
    /// Runs the simulation until it becomes idle.
    /// </summary>
    /// <returns>The number of iterations executed. Callers can compare this to maxIterations
    /// and the current time to determine which limit was reached.</returns>
    public int RunUntilIdle(TimeSpan? maxSimulatedTime = null, int maxIterations = 100000) => RunUntilIdleCore(maxSimulatedTime, maxIterations);

    /// <summary>
    /// Core implementation of RunUntilIdle without context installation (for internal use).
    /// Uses round-robin execution across all non-suspended node contexts, plus the harness queue.
    /// </summary>
    /// <returns>The number of iterations executed.</returns>
    private int RunUntilIdleCore(TimeSpan? maxSimulatedTime, int maxIterations)
    {
        using var _ = _lock.Enter();
        var startTime = TimeProvider.GetUtcNow();
        var maxEndTime = maxSimulatedTime ?? MaxSimulatedTimeAdvance;
        var timeAdvanceCount = 0;

        for (var i = 0; i < maxIterations; i++)
        {
            // Check for teardown cancellation
            if (TeardownCancellationToken.IsCancellationRequested)
            {
                LogEvent(SimulationEventType.MaxStepsReached, "Teardown cancellation requested - exiting simulation loop");
                return i;
            }

            if (RunOneTaskRoundRobin())
            {
                LogicalTime++;
                timeAdvanceCount = 0; // Reset time advance counter when real work happens
                continue;
            }

            var nextScheduledTime = GetNextWaitingDueTime();
            if (!nextScheduledTime.HasValue)
            {
                LogEvent(SimulationEventType.ConditionMet, "Simulation reached idle state");
                return i;
            }

            var timeDelta = nextScheduledTime.Value - Clock.UtcNow;
            if (timeDelta > maxEndTime)
            {
                LogEvent(SimulationEventType.MaxStepsReached,
                    $"Simulation appears stuck: exceeded max simulated time ({maxSimulatedTime ?? MaxSimulatedTimeAdvance}). " +
                    $"Start: {startTime:O}, Current: {TimeProvider.GetUtcNow():O}, Next scheduled time delta: {timeDelta}");
                return i;
            }

            // Advance time to the next scheduled task
            if (timeDelta > TimeSpan.Zero)
            {
                Clock.Advance(timeDelta);
            }
            timeAdvanceCount++;

            // Safety check: if we've advanced time many times without executing tasks, we might be stuck
            if (timeAdvanceCount > 10000)
            {
                LogEvent(SimulationEventType.MaxStepsReached,
                    $"Simulation appears stuck: {timeAdvanceCount} consecutive time advances without task execution");
                return i;
            }
        }

        LogEvent(SimulationEventType.MaxStepsReached, $"Max iterations ({maxIterations}) reached");
        return maxIterations;
    }

    /// <summary>
    /// Drives a task to completion by running the simulation.
    /// The task factory is invoked with the harness's synchronization context installed,
    /// ensuring async continuations are captured on the simulation scheduler.
    /// </summary>
    public void DriveToCompletion(Func<Task> taskFactory, int maxIterations = 100000)
    {
        ArgumentNullException.ThrowIfNull(taskFactory);
        using var lockScope = _lock.Enter();

        // Use the harness queue's sync context for the task factory invocation
        using var _ = TaskQueue.SynchronizationContext.Install();

        var task = new Task<Task>(taskFactory);
        task.Start(TaskScheduler);

        if (!RunUntilCore(() => task.IsCompleted && task.Result.IsCompleted, maxIterations))
        {
            if (!task.IsCompleted)
            {
                throw new TimeoutException($"Task did not complete within {maxIterations} iterations");
            }
        }

        task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Waits for all nodes to converge to the same membership size.
    /// </summary>
    public void WaitForConvergence(int expectedSize, int maxIterations = 100000)
    {
        if (!RunUntilConverged(expectedSize, maxIterations))
        {
            throw new TimeoutException($"Nodes did not converge to size {expectedSize}. " +
                $"Current sizes: [{string.Join(", ", Nodes.Select(n => n.MembershipSize))}]");
        }
    }

    /// <summary>
    /// Waits for the specified nodes to converge to the same membership size.
    /// Use this overload when some nodes (e.g., isolated/partitioned nodes) should be excluded from the check.
    /// </summary>
    public void WaitForConvergence(IEnumerable<SimulationNode> nodes, int expectedSize, int maxIterations = 100000)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var nodeList = nodes.ToList();
        if (!RunUntilConverged(nodeList, expectedSize, maxIterations))
        {
            throw new TimeoutException($"Nodes did not converge to size {expectedSize}. " +
                $"Current sizes: [{string.Join(", ", nodeList.Select(n => n.MembershipSize))}]");
        }
    }

    /// <summary>
    /// Waits for a specific node to reach the expected membership size.
    /// </summary>
    public void WaitForNodeSize(SimulationNode node, int expectedSize, int maxIterations = 100000)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!RunUntil(() => node.MembershipSize == expectedSize, maxIterations))
        {
            throw new TimeoutException($"Node did not reach size {expectedSize}. Current size: {node.MembershipSize}");
        }
    }

    /// <summary>
    /// Runs the simulation for the specified duration or until the maximum iterations are exceeded.
    /// This is the preferred method for advancing time in tests, as it ensures that any
    /// tasks triggered by timers are processed before returning.
    /// </summary>
    /// <param name="delta">The amount of time to advance.</param>
    /// <param name="maxIterations">Maximum iterations to run while processing tasks.</param>
    /// <returns>True if the simulation reached an idle state; false if max iterations reached.</returns>
    public bool RunForDuration(TimeSpan delta, int maxIterations = 100000)
    {
        if (delta < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delta), "Time delta cannot be negative");
        }

        if (delta == TimeSpan.Zero)
        {
            return true;
        }

        // Advance time to trigger timers, then run until idle
        LogEvent(SimulationEventType.TimeAdvanced, $"Advancing time until {delta}");

        using var lockScope = _lock.Enter();

        var targetTime = Clock.UtcNow + delta;
        var iterations = RunUntilIdleCore(maxSimulatedTime: delta, maxIterations);
        if (Clock.UtcNow < targetTime)
        {
            Clock.Advance(targetTime - Clock.UtcNow);
        }

        // If first call didn't exhaust max iterations, it reached idle or a time limit
        // Run again to process any remaining work
        var remainingIterations = maxIterations - iterations;
        if (remainingIterations == 0)
        {
            return false;
        }

        return RunUntilIdleCore(null, remainingIterations) < remainingIterations;
    }

    #endregion

    #region Event Logging

    /// <summary>
    /// Gets a copy of the event log.
    /// </summary>
    public IReadOnlyList<SimulationEvent> EventLog
    {
        get
        {
            using var _ = _lock.Enter();
            return [.. _eventLog];
        }
    }

    public CancellationToken TeardownCancellationToken { get; }

    /// <summary>
    /// Logs the seed to the test output for reproduction.
    /// </summary>
    public void LogSeedForReproduction() => _logger?.LogInformation("[SEED FOR REPRODUCTION] {Seed}", Seed);

    /// <summary>
    /// Dumps the event log to the test output.
    /// </summary>
    public void DumpEventLog()
    {
        using var _ = _lock.Enter();
        if (_logger == null) return;

        _logger.LogInformation("=== Simulation Event Log ===");
        foreach (var evt in _eventLog)
        {
            _logger.LogInformation("[{LogicalTime:D6}] [{SimulatedTime:O}] {Type}: {Description}",
                evt.LogicalTime, evt.SimulatedTime, evt.Type, evt.Description);
        }
        _logger.LogInformation("============================");
    }

    private void LogEvent(SimulationEventType type, string description)
    {
        using var _ = _lock.Enter();
        var evt = new SimulationEvent(
            LogicalTime,
            TimeProvider.GetUtcNow(),
            type,
            description);
        _eventLog.Add(evt);

        _logger?.LogDebug("[{LogicalTime:D6}] {Type}: {Description}", evt.LogicalTime, type, description);
    }

    #endregion

    #region Disposal

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        // Clear harness queue
        TaskQueue.Clear();

        // Destroy all nodes (take snapshot since Destroy calls UnregisterNode)
        foreach (var node in Nodes.ToList())
        {
            UnregisterNode(node);
            node.Destroy();
        }

        // Attach logs to test context BEFORE disposing the provider
        AttachLogsToTestContext(TestContext.Current);

        // Dispose the logger factory first (removes reference to provider)
        LoggerFactory.Dispose();

        // Explicitly dispose the in-memory logger provider to satisfy CA2213
        _inMemoryLoggerProvider.Dispose();

        await Task.CompletedTask.ConfigureAwait(true);
    }

    /// <summary>
    /// Attaches buffered logs to the test context.
    /// If the full log exceeds 1MB, a warning is added and only Information level and above are attached.
    /// </summary>
    private void AttachLogsToTestContext(ITestContext? testContext)
    {
        if (testContext == null)
        {
            return;
        }

        var buffer = _inMemoryLoggerProvider.Buffer;
        var (fullContent, fullSizeBytes) = buffer.FormatAllEntriesWithSize();

        string logContent;
        string logFileName;

        if (fullSizeBytes <= MaxFullLogSizeBytes)
        {
            // Full log is under 1MB - attach it directly
            logContent = fullContent;
            logFileName = GenerateLogFileName(testContext.Test?.TestDisplayName, Seed);
        }
        else
        {
            // Full log exceeds 1MB - warn and attach only Information and above
            testContext.TestOutputHelper?.WriteLine(
                $"Warning: Full simulation log ({fullSizeBytes:N0} bytes) exceeds 1MB limit. " +
                $"Attaching only Information level and above.");

            var (filteredContent, _) = buffer.FormatEntriesWithSize(LogLevel.Information);
            logContent = filteredContent;
            logFileName = GenerateLogFileName(testContext.Test?.TestDisplayName, Seed, filtered: true);
        }

        // Attach log content directly to the test context
        testContext.AddAttachment(logFileName, logContent);
    }

    /// <summary>
    /// Generates a unique log file name for a simulation.
    /// </summary>
    private static string GenerateLogFileName(string? testName, int seed, bool filtered = false)
    {
        var sanitizedTestName = SanitizeFileName(testName ?? "unknown_test");
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var suffix = filtered ? "_info_and_above" : "";

        return $"rapid_sim_{sanitizedTestName}_{seed}_{uniqueId}{suffix}.log";
    }

    /// <summary>
    /// Sanitizes a string to be used as a file name by removing invalid characters.
    /// </summary>
    private static string SanitizeFileName(string name)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new System.Text.StringBuilder();
        foreach (var c in name)
        {
            sanitized.Append(invalidChars.Contains(c) ? '_' : c);
        }
        // Truncate to a reasonable length to avoid path length issues
        var result = sanitized.ToString();
        return result.Length > 100 ? result[..100] : result;
    }

    #endregion
}

/// <summary>
/// Types of events that can be logged during simulation.
/// </summary>
internal enum SimulationEventType
{
    HarnessCreated,
    NodeCreated,
    NodeJoining,
    NodeJoined,
    NodeLeaving,
    NodeLeft,
    NodeCrashed,
    NodeIsolated,
    NodeReconnected,
    NodeSuspended,
    NodeResumed,
    PartitionCreated,
    PartitionHealed,
    MessageSent,
    MessageReceived,
    MessageDropped,
    TimeAdvanced,
    FastForward,
    ConditionMet,
    MaxStepsReached,
    InvariantViolation
}

/// <summary>
/// Represents an event that occurred during simulation.
/// </summary>
internal readonly record struct SimulationEvent(
    long LogicalTime,
    DateTimeOffset SimulatedTime,
    SimulationEventType Type,
    string Description);


