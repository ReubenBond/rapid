using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Rapid.Tests.Simulation.Infrastructure.Logging;

namespace Rapid.Tests.Simulation.Infrastructure;

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
/// - Simulation driving APIs (Step, RunUntil, Run)
/// </summary>
internal sealed partial class SimulationHarness : IAsyncDisposable
{
    private readonly SortedDictionary<string, SimulationNode> _nodes = new(StringComparer.Ordinal);
    private readonly SimulationLogManager _logManager;
    private readonly ILogger<SimulationHarness> _logger;
    private readonly SimulationHarnessLogger _log;
    private readonly SimulationTimeProvider _timeProvider;
    private readonly CancellationTokenSource _teardownCts;
    private bool _disposed;

    /// <summary>
    /// Creates a new simulation harness with the specified seed.
    /// Logs are written to a unique file per simulation and attached to the test context.
    /// </summary>
    /// <param name="seed">The seed for deterministic random number generation.</param>
    public SimulationHarness(int seed)
    {
        var context = TestContext.Current;
        _teardownCts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        TeardownCancellationToken = _teardownCts.Token;
        Seed = seed;
        StartDateTime = DateTimeOffset.UtcNow;

        Random = new SimulationRandom(seed);

        // Create shared clock and harness-level queue
        Clock = new SimulationClock(StartDateTime);
        TaskQueue = new SimulationTaskQueue(Clock, Guard);
        TaskScheduler = new SimulationTaskScheduler(TaskQueue);

        // Create time provider using harness queue (for GetUtcNow queries)
        _timeProvider = new SimulationTimeProvider(TaskQueue, Clock);

        Network = new SimulationNetwork(this, Random);

        // Create log manager for logger factory and log attachment
        _logManager = new SimulationLogManager(_timeProvider, seed);
        LoggerFactory = _logManager.LoggerFactory;

        _logger = LoggerFactory.CreateLogger<SimulationHarness>();
        _log = new SimulationHarnessLogger(_logger);
        Network.SetLogger(LoggerFactory.CreateLogger<SimulationNetwork>());

        _log.HarnessCreated(seed);
    }

    /// <summary>
    /// Gets the seed used for this harness.
    /// </summary>
    public int Seed { get; }

    /// <summary>
    /// A cancellation token used to signal when the simulation is being torn down.
    /// </summary>
    public CancellationToken TeardownCancellationToken { get; }

    /// <summary>
    /// Maximum simulated time to advance before considering the simulation stuck.
    /// Default is 10 minutes of simulated time.
    /// </summary>
    public TimeSpan MaxSimulatedTimeAdvance { get; set; } = TimeSpan.FromMinutes(10);

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

    /// <summary>
    /// Gets the simulation time provider.
    /// </summary>
    public TimeProvider TimeProvider => _timeProvider;

    /// <summary>
    /// Gets the simulation network.
    /// </summary>
    public SimulationNetwork Network { get; }

    /// <summary>
    /// Gets all nodes in the simulation, including suspended nodes (snapshot).
    /// Consider using <see cref="ActiveNodes"/> for most operations.
    /// </summary>
    public IReadOnlyList<SimulationNode> Nodes => [.. _nodes.Values];

    /// <summary>
    /// Gets all active (non-suspended) nodes in the simulation (snapshot).
    /// Suspended nodes cannot process messages and are excluded from convergence checks.
    /// </summary>
    public IReadOnlyList<SimulationNode> ActiveNodes => [.. _nodes.Values.Where(n => !n.IsSuspended)];

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
    public SingleThreadedGuard Guard { get; } = new();

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

    /// <summary>
    /// Registers a node with the simulation.
    /// </summary>
    internal void RegisterNode(SimulationNode node)
    {
        var key = RapidUtils.Loggable(node.Address);
        using var _ = Guard.Enter();
        if (!_nodes.TryAdd(key, node))
        {
            throw new InvalidOperationException($"Node with address {key} already exists");
        }
    }

    /// <summary>
    /// Unregisters a node from the simulation.
    /// The node is removed from the routing table so it won't receive new messages.
    /// Note: This does NOT clear the node's task queue - the node may still have
    /// pending work that needs to complete (e.g., during disposal).
    /// </summary>
    internal void UnregisterNode(SimulationNode node)
    {
        var key = RapidUtils.Loggable(node.Address);
        using var _ = Guard.Enter();
        _nodes.Remove(key);
        // Note: We intentionally do NOT clear the queue here.
        // The node may still need to process disposal tasks.
    }

    /// <summary>
    /// Gets a node by its address string.
    /// </summary>
    internal SimulationNode? GetNode(string address)
    {
        using var _ = Guard.Enter();
        _nodes.TryGetValue(address, out var node);
        return node;
    }

    /// <summary>
    /// Creates a new deterministic random instance derived from the harness's random.
    /// </summary>
    internal SimulationRandom CreateDerivedRandom()
    {
        using var _ = Guard.Enter();
#pragma warning disable CA5394 // Do not use insecure randomness
        return new SimulationRandom(Random.Next());
#pragma warning restore CA5394 // Do not use insecure randomness
    }

    /// <summary>
    /// Creates a node without initializing it (for testing edge cases).
    /// The node is registered but not started or joined to any cluster.
    /// </summary>
    /// <param name="nodeId">The node ID.</param>
    /// <param name="seedNode">Optional seed node for joining. If null, the node will start its own cluster when initialized.</param>
    /// <param name="options">Optional protocol options.</param>
    public SimulationNode CreateUninitializedNode(int nodeId, SimulationNode? seedNode = null, RapidProtocolOptions? options = null)
    {
        var address = RapidUtils.HostFromParts("node", nodeId);
        var node = new SimulationNode(this, address, seedNode?.Address, metadata: null, options, LoggerFactory);
        RegisterNode(node);
        _log.UninitializedNodeCreated(nodeId);
        return node;
    }

    /// <summary>
    /// Creates and starts a new seed node.
    /// </summary>
    public SimulationNode CreateSeedNode(int nodeId = 0, RapidProtocolOptions? options = null)
    {
        var address = RapidUtils.HostFromParts("node", nodeId);
        var node = new SimulationNode(this, address, seedAddress: null, metadata: null, options, LoggerFactory);
        RegisterNode(node);

        // For seed nodes, initialization is synchronous (no network I/O needed),
        // but we still drive it through DriveToCompletion for consistency.
        Run(() => node.InitializeAsync());

        _log.SeedNodeCreated(nodeId);
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
        var address = RapidUtils.HostFromParts("node", nodeId);
        var node = new SimulationNode(this, address, seedNode.Address, metadata: null, options, LoggerFactory);
        RegisterNode(node);

        _log.NodeJoining(nodeId);

        // Drive the initialization to completion
        Run(() => node.InitializeAsync());

        _log.NodeJoined(nodeId);
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

        WaitForConvergence();
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
                var address = RapidUtils.HostFromParts("node", nodeId++);
                var node = new SimulationNode(this, address, seedNode.Address, metadata: null, options, LoggerFactory);
                RegisterNode(node);

                _log.NodeJoiningParallel(RapidUtils.Loggable(node.Address));

                batchNodes.Add(node);
            }

            // Drive the simulation until all joins in this batch complete.
            // IMPORTANT: Join tasks must be started inside DriveToCompletion so they
            // capture the simulation's SynchronizationContext for their continuations.
            Run(() => Task.WhenAll(batchNodes.Select(node => node.InitializeAsync())), maxIterationsPerBatch);

            // Log completion
            foreach (var node in batchNodes)
            {
                _log.NodeJoinedParallel(RapidUtils.Loggable(node.Address));
            }

            result.AddRange(batchNodes);
            remainingNodes -= currentBatchSize;
        }

        return result;
    }

    /// <summary>
    /// Crashes a node (simulates sudden failure).
    /// Disposes and unregisters the node - no cleanup or leave messages are sent.
    /// The disposal cancels in-flight tasks and releases resources to prevent memory leaks.
    /// </summary>
    public void CrashNode(SimulationNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        // Dispose the node while it's still registered so Run() can drive its task queue.
        // This cancels in-flight tasks and releases resources.
        Run(() => node.DisposeAsync().AsTask());

        // Unregister after disposal - no new messages will be delivered
        UnregisterNode(node);

        _log.NodeCrashed();
    }

    /// <summary>
    /// Gracefully removes a node from the cluster.
    /// The leaving node must participate in the consensus round that removes it,
    /// so we keep it alive until consensus completes and all remaining nodes converge.
    /// </summary>
    public void RemoveNodeGracefully(SimulationNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        _log.NodeLeaving();

        var remainingNodes = Nodes.Where(n => n != node).ToList();
        var targetSize = remainingNodes.Count;

        // Drive the stop operation to completion (sends LeaveMessages to observers)
        // Node can still receive messages after this
        Run(node.StopAsync);

        // The leaving node must remain active to participate in consensus.
        // Run the simulation until all remaining nodes converge to the new size.
        var converged = RunUntil(
            () => remainingNodes.All(n => n.MembershipSize == targetSize),
            maxIterations: 100000);

        if (!converged)
        {
            // Log current state for debugging
            var sizes = string.Join(", ", remainingNodes.Select(n => n.MembershipSize));
            _logger.LogWarning(
                "RemoveNodeGracefully: remaining nodes did not converge to size {TargetSize}. Current sizes: [{Sizes}]",
                targetSize, sizes);
        }

        // Dispose the node's resources while it's still registered.
        // This ensures Run() can drive the node's task queue during disposal.
        Run(() => node.DisposeAsync().AsTask());

        // Unregister the node from the network (no more messages will be delivered)
        UnregisterNode(node);

        _log.NodeLeft();
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

        _log.NodesLeavingParallel(nodesToRemove.Count);

        foreach (var node in nodesToRemove)
        {
            _log.NodeLeavingParallel(RapidUtils.Loggable(node.Address));
        }

        // Drive the simulation until all leave operations complete.
        // IMPORTANT: Leave tasks must be started inside DriveToCompletion so they
        // capture the simulation's SynchronizationContext for their continuations.
        // StopAsync sends leave messages but keeps nodes alive to participate in consensus.
        Run(() =>
        {
            var leaveTasks = nodesToRemove.Select(node => node.StopAsync());
            return Task.WhenAll(leaveTasks);
        });

        // Wait for remaining nodes to converge to the new size
        var converged = RunUntil(
            () => remainingNodes.All(n => n.MembershipSize == targetSize),
            maxIterations: 500000);

        if (!converged)
        {
            var sizes = string.Join(", ", remainingNodes.Select(n => n.MembershipSize));
            _logger.LogWarning(
                "RemoveNodesGracefullyParallel: remaining nodes did not converge to size {TargetSize}. Current sizes: [{Sizes}]",
                targetSize, sizes);
        }

        // Dispose all leaving nodes' resources while they're still registered.
        // This ensures Run() can drive each node's task queue during disposal.
        Run(() =>
        {
            var disposeTasks = nodesToRemove.Select(node => node.DisposeAsync().AsTask());
            return Task.WhenAll(disposeTasks);
        });

        // Unregister leaving nodes from the network (no more messages will be delivered)
        foreach (var node in nodesToRemove)
        {
            UnregisterNode(node);
            _log.NodeLeftParallel(RapidUtils.Loggable(node.Address));
        }

        // Calculate configuration changes
        var endingConfigVersion = remainingNodes[0].CurrentView.ConfigurationId.Version;
        var configChanges = (int)(endingConfigVersion - startingConfigVersion);

        _log.ParallelLeaveCompleted(nodesToRemove.Count, configChanges);

        return configChanges;
    }

    /// <summary>
    /// Isolates a node from the cluster.
    /// </summary>
    public void IsolateNode(SimulationNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var addr = RapidUtils.Loggable(node.Address);
        Network.IsolateNode(addr);
        _log.NodeIsolated();
    }

    /// <summary>
    /// Reconnects an isolated node.
    /// </summary>
    public void ReconnectNode(SimulationNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var addr = RapidUtils.Loggable(node.Address);
        Network.ReconnectNode(addr);
        _log.NodeReconnected();
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
        _log.PartitionCreated();
    }

    /// <summary>
    /// Heals a partition between two nodes.
    /// </summary>
    public void HealPartition(SimulationNode node1, SimulationNode node2)
    {
        using var _ = Guard.Enter();
        ArgumentNullException.ThrowIfNull(node1);
        ArgumentNullException.ThrowIfNull(node2);
        var addr1 = RapidUtils.Loggable(node1.Address);
        var addr2 = RapidUtils.Loggable(node2.Address);
        Network.HealBidirectionalPartition(addr1, addr2);
        _log.PartitionHealed();
    }

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
        using var _ = Guard.Enter();
        var startTime = TimeProvider.GetUtcNow();
        var maxEndTime = MaxSimulatedTimeAdvance;
        var timeAdvanceCount = 0;

        for (var i = 0; i < maxIterations; i++)
        {
            // Check for teardown cancellation
            if (TeardownCancellationToken.IsCancellationRequested)
            {
                _log.TeardownCancellationRequested();
                return false;
            }

            if (condition())
            {
                _log.ConditionMet(i);
                return true;
            }

            // Try to execute one ready task using round-robin across all sources
            if (RunOneTaskRoundRobin())
            {
                timeAdvanceCount = 0; // Reset time advance counter when real work happens
                continue;
            }

            // No tasks to execute - need to advance time
            var nextScheduledTime = GetNextWaitingDueTime();
            if (!nextScheduledTime.HasValue)
            {
                // No more scheduled work - simulation is idle and cannot make progress
                _log.SimulationIdleNoPendingWork(i, $"{TimeProvider.GetUtcNow():O}");
                return false;
            }

            // Check if we've been advancing time without making progress
            var timeDelta = nextScheduledTime.Value - Clock.UtcNow;
            if (timeDelta > maxEndTime)
            {
                _log.SimulationStuckMaxTime(
                    $"{MaxSimulatedTimeAdvance}",
                    $"{startTime:O}",
                    $"{TimeProvider.GetUtcNow():O}",
                    $"{timeDelta}");
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
                _log.SimulationStuckConsecutiveTimeAdvances(timeAdvanceCount);
                return false;
            }
        }

        _log.MaxIterationsReached(maxIterations);
        return false;
    }

    /// <summary>
    /// Attempts to execute one ready task using round-robin across all node contexts and the harness queue.
    /// Returns true if a task was executed.
    /// </summary>
    private bool RunOneTaskRoundRobin()
    {
        using var _ = Guard.Enter();

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
        using var _ = Guard.Enter();
        return Nodes.Select(n => n.Context.NextWaitingDueTime).Concat([TaskQueue.NextWaitingDueTime]).Min();
    }

    /// <summary>
    /// Runs until all non-suspended nodes have the expected membership size.
    /// Suspended nodes are excluded from the check since they cannot process messages.
    /// </summary>
    public bool RunUntilConverged(int expectedSize, int maxIterations = 100000) =>
        RunUntil(() => ActiveNodes.All(n => n.MembershipSize == expectedSize), maxIterations);

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
        using var _ = Guard.Enter();
        var startTime = TimeProvider.GetUtcNow();
        var maxEndTime = maxSimulatedTime ?? MaxSimulatedTimeAdvance;
        var timeAdvanceCount = 0;

        for (var i = 0; i < maxIterations; i++)
        {
            // Check for teardown cancellation
            if (TeardownCancellationToken.IsCancellationRequested)
            {
                _log.TeardownCancellationRequested();
                return i;
            }

            if (RunOneTaskRoundRobin())
            {
                timeAdvanceCount = 0; // Reset time advance counter when real work happens
                continue;
            }

            var nextScheduledTime = GetNextWaitingDueTime();
            if (!nextScheduledTime.HasValue)
            {
                _log.SimulationReachedIdleState();
                return i;
            }

            var timeDelta = nextScheduledTime.Value - Clock.UtcNow;
            if (timeDelta > maxEndTime)
            {
                _log.SimulationStuckMaxTime(
                    $"{maxSimulatedTime ?? MaxSimulatedTimeAdvance}",
                    $"{startTime:O}",
                    $"{TimeProvider.GetUtcNow():O}",
                    $"{timeDelta}");
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
                _log.SimulationStuckConsecutiveTimeAdvances(timeAdvanceCount);
                return i;
            }
        }

        _log.MaxIterationsReached(maxIterations);
        return maxIterations;
    }

    /// <summary>
    /// Drives a task to completion by running the simulation.
    /// The task factory is invoked with the harness's synchronization context installed,
    /// ensuring async continuations are captured on the simulation scheduler.
    /// </summary>
    public void Run(Func<Task> taskFactory, int maxIterations = 100000)
    {
        ArgumentNullException.ThrowIfNull(taskFactory);
        using var lockScope = Guard.Enter();

        var task = new Task<Task>(taskFactory);
        task.Start(TaskScheduler);

        if (!RunUntilCore(() => task.IsCompleted && task.Result.IsCompleted, maxIterations))
        {
            if (!task.IsCompleted || !task.GetAwaiter().GetResult().IsCompleted)
            {
                Debugger.Launch();
                throw new TimeoutException($"Task did not complete within {maxIterations} iterations");
            }
        }

        task.GetAwaiter().GetResult().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Drives a task to completion by running the simulation asynchronously.
    /// The task factory is invoked with the harness's synchronization context installed,
    /// ensuring async continuations are captured on the simulation scheduler.
    /// Unlike <see cref="Run"/>, this method awaits the task instead of calling Wait(),
    /// allowing exceptions to propagate properly through async/await.
    /// </summary>
    public async Task RunAsync(Func<Task> taskFactory, int maxIterations = 100000)
    {
        ArgumentNullException.ThrowIfNull(taskFactory);
        var task = new Task<Task>(taskFactory);
        using (Guard.Enter())
        {
            task.Start(TaskScheduler);

            if (!RunUntilCore(() => task.IsCompleted && task.Result.IsCompleted, maxIterations))
            {
                if (!task.IsCompleted)
                {
                    throw new TimeoutException($"Task did not complete within {maxIterations} iterations");
                }
            }
        }

        var innerTask = await task.ConfigureAwait(true);
        await innerTask.ConfigureAwait(true);
    }

    /// <summary>
    /// Waits for all active (non-suspended) nodes to converge to a consistent view where
    /// each node sees exactly the number of active nodes in its membership.
    /// This is the preferred overload when nodes may be suspended during the test.
    /// </summary>
    public void WaitForConvergence(int maxIterations = 100000)
    {
        var converged = RunUntil(() =>
        {
            var activeCount = ActiveNodes.Count;
            return activeCount > 0 && ActiveNodes.All(n => n.MembershipSize == activeCount);
        }, maxIterations);

        if (!converged)
        {
            var suspendedNodes = Nodes.Where(n => n.IsSuspended).ToList();
            throw new TimeoutException($"Nodes did not converge. " +
                $"Active node count: {ActiveNodes.Count}, " +
                $"Active node sizes: [{string.Join(", ", ActiveNodes.Select(n => n.MembershipSize))}], " +
                $"Suspended nodes: {suspendedNodes.Count}");
        }
    }

    /// <summary>
    /// Waits for all non-suspended nodes to converge to the same membership size.
    /// Suspended nodes are excluded from the check since they cannot process messages.
    /// </summary>
    public void WaitForConvergence(int expectedSize, int maxIterations = 100000)
    {
        if (!RunUntilConverged(expectedSize, maxIterations))
        {
            var suspendedNodes = Nodes.Where(n => n.IsSuspended).ToList();
            throw new TimeoutException($"Nodes did not converge to size {expectedSize}. " +
                $"Active node sizes: [{string.Join(", ", ActiveNodes.Select(n => n.MembershipSize))}], " +
                $"Suspended nodes: {suspendedNodes.Count}");
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
        _log.TimeAdvancing($"{delta}");

        using var lockScope = Guard.Enter();

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

    /// <summary>
    /// Logs the seed to the test output for reproduction.
    /// </summary>
    public void LogSeedForReproduction() => _logger.LogInformation("[SEED FOR REPRODUCTION] {Seed}", Seed);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
#pragma warning disable CA1849 // Call async methods when in an async method
        Run(async () =>
        {
#pragma warning disable CA1849 // Call async methods when in an async method
            _teardownCts.Cancel();
#pragma warning restore CA1849 // Call async methods when in an async method

            // Dispose all nodes to cancel in-flight tasks and release resources.
            // This triggers cancellation of each node's _disposeCts, which propagates
            // to MembershipService and MessagingClient, allowing pending tasks to complete.
            var nodes = Nodes.ToList();
            foreach (var node in nodes)
            {
                await node.DisposeAsync().ConfigureAwait(true);
                UnregisterNode(node);
            }
        });
#pragma warning restore CA1849 // Call async methods when in an async method

        // Attach logs to test context BEFORE disposing the provider
        _logManager.AttachLogsToTestContext(TestContext.Current);

        // Dispose the log manager (disposes logger factory and provider)
        _logManager.Dispose();
        _teardownCts.Dispose();
    }
}
