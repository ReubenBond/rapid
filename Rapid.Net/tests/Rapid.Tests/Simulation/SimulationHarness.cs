using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Rapid.Tests.Simulation;

/// <summary>
/// Unified simulation harness for fully deterministic testing of Rapid clusters.
/// 
/// Provides:
/// - Deterministic task scheduling via <see cref="SimulationTaskScheduler"/>
/// - Controlled time via <see cref="SimulationTimeProvider"/>
/// - Seeded random number generation via <see cref="SimulationRandom"/>
/// - Simulated network with partition injection via <see cref="SimulationNetwork"/>
/// - Node lifecycle management (create, join, crash, leave)
/// - Event logging for debugging and verification
/// - Simulation driving APIs (Step, RunUntil, DriveToCompletion)
/// </summary>
internal sealed class SimulationHarness : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, SimulationNode> _nodeRegistry = new();
    private readonly List<SimulationNode> _nodes = [];
    private readonly SimulationSynchronizationContext _syncContext;
    private readonly List<SimulationEvent> _eventLog = [];
    private readonly Lock _eventLogLock = new();
    private readonly Lock _randomLock = new();
    private readonly ITestOutputHelper? _testOutput;
    private bool _disposed;

    /// <summary>
    /// Creates a new simulation harness with the specified seed.
    /// </summary>
    /// <param name="seed">The seed for deterministic random number generation.</param>
    /// <param name="loggerFactory">Optional logger factory for logging.</param>
    /// <param name="testOutput">Optional xUnit test output helper for logging.</param>
    public SimulationHarness(
        int seed,
        ILoggerFactory? loggerFactory = null,
        ITestOutputHelper? testOutput = null)
    {
        Seed = seed;
        _testOutput = testOutput;
        LoggerFactory = loggerFactory;

        // Create deterministic components
        Random = new SimulationRandom(seed);
        Scheduler = new SimulationTaskScheduler();

        // Create time provider that shares the task queue with the scheduler
        var timeProviderLogger = loggerFactory?.CreateLogger<SimulationTimeProvider>();
        TimeProvider = new SimulationTimeProvider(Scheduler.TaskQueue, DateTimeOffset.UtcNow, timeProviderLogger);

        // Create network
        Network = new SimulationNetwork(this);

        // Create synchronization context (but don't install it globally - install per-operation)
        _syncContext = new SimulationSynchronizationContext(Scheduler);

        LogEvent(SimulationEventType.HarnessCreated, $"Seed: {seed}");
    }

    /// <summary>
    /// Creates a simulation harness with a random seed, logging the seed for reproduction.
    /// </summary>
    public static SimulationHarness CreateWithRandomSeed(
        ILoggerFactory? loggerFactory = null,
        ITestOutputHelper? testOutput = null)
    {
        var seed = Environment.TickCount;
        testOutput?.WriteLine($"[Simulation] Using random seed: {seed}");
        return new SimulationHarness(seed, loggerFactory, testOutput);
    }

    #region Core Components

    /// <summary>
    /// Gets the seed used for this harness.
    /// </summary>
    public int Seed { get; }

    /// <summary>
    /// Gets the logger factory.
    /// </summary>
    public ILoggerFactory? LoggerFactory { get; }

    /// <summary>
    /// Gets the simulation random instance.
    /// </summary>
    public SimulationRandom Random { get; }

    /// <summary>
    /// Gets the simulation task scheduler.
    /// </summary>
    public SimulationTaskScheduler Scheduler { get; }

    /// <summary>
    /// Gets the simulation time provider.
    /// </summary>
    public SimulationTimeProvider TimeProvider { get; }

    /// <summary>
    /// Gets the simulation network.
    /// </summary>
    public SimulationNetwork Network { get; }

    /// <summary>
    /// Gets all nodes in the simulation.
    /// </summary>
    public IReadOnlyList<SimulationNode> Nodes => _nodes;

    /// <summary>
    /// Gets the current logical time (number of tasks executed).
    /// </summary>
    public long LogicalTime { get; private set; }

    #endregion

    #region Node Registry (Internal)

    /// <summary>
    /// Registers a node with the simulation.
    /// </summary>
    internal void RegisterNode(SimulationNode node)
    {
        var key = RapidUtils.Loggable(node.Address);
        if (!_nodeRegistry.TryAdd(key, node))
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
        _nodeRegistry.TryRemove(key, out _);
    }

    /// <summary>
    /// Gets a node by its address string.
    /// </summary>
    internal SimulationNode? GetNode(string address)
    {
        _nodeRegistry.TryGetValue(address, out var node);
        return node;
    }

    /// <summary>
    /// Creates a new deterministic random instance derived from the harness's random.
    /// </summary>
    internal SimulationRandom CreateDerivedRandom()
    {
        lock (_randomLock)
        {
            return new SimulationRandom(Random.Next());
        }
    }

    #endregion

    #region Node Lifecycle

    /// <summary>
    /// Creates and starts a new seed node.
    /// </summary>
    public SimulationNode CreateSeedNode(int nodeId = 0, RapidProtocolOptions? options = null)
    {
        var opts = ConfigureOptions(options);
        var node = SimulationNode.Create(this, nodeId, opts, LoggerFactory);
        node.StartCluster();
        _nodes.Add(node);
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
        var opts = ConfigureOptions(options);
        var node = SimulationNode.Create(this, nodeId, opts, LoggerFactory);

        LogEvent(SimulationEventType.NodeJoining, $"Node {nodeId} joining via seed");

        // Drive the join to completion
        DriveToCompletion(() => node.JoinClusterAsync(seedNode));

        _nodes.Add(node);
        LogEvent(SimulationEventType.NodeJoined, $"Node {nodeId} joined cluster");
        return node;
    }

    /// <summary>
    /// Creates and joins a new node to the cluster (async version for compatibility).
    /// </summary>
    public Task<SimulationNode> CreateJoinerNodeAsync(
        SimulationNode seedNode,
        int nodeId,
        RapidProtocolOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(CreateJoinerNode(seedNode, nodeId, options));
    }

    /// <summary>
    /// Creates a cluster of the specified size.
    /// </summary>
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
    /// Creates a cluster of the specified size (async version for compatibility).
    /// </summary>
    public Task<IReadOnlyList<SimulationNode>> CreateClusterAsync(
        int size,
        RapidProtocolOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(CreateCluster(size, options));
    }

    /// <summary>
    /// Crashes a node (simulates sudden failure).
    /// </summary>
    public void CrashNode(SimulationNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        node.Shutdown();
        node.Dispose();
        _nodes.Remove(node);
        LogEvent(SimulationEventType.NodeCrashed, $"Node crashed");
    }

    /// <summary>
    /// Gracefully removes a node from the cluster.
    /// </summary>
    public void RemoveNodeGracefully(SimulationNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        DriveToCompletion(() => node.LeaveAsync());
        node.Shutdown();
        node.Dispose();
        _nodes.Remove(node);
        LogEvent(SimulationEventType.NodeLeft, $"Node left gracefully");
    }

    /// <summary>
    /// Gracefully removes a node (async version for compatibility).
    /// </summary>
    public Task RemoveNodeGracefullyAsync(SimulationNode node)
    {
        RemoveNodeGracefully(node);
        return Task.CompletedTask;
    }

    private static RapidProtocolOptions ConfigureOptions(RapidProtocolOptions? options)
    {
        var opts = options ?? new RapidProtocolOptions();
        opts.BatchingWindow = TimeSpan.Zero; // Immediate processing
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
        ArgumentNullException.ThrowIfNull(node1);
        ArgumentNullException.ThrowIfNull(node2);
        var addr1 = RapidUtils.Loggable(node1.Address);
        var addr2 = RapidUtils.Loggable(node2.Address);
        Network.HealBidirectionalPartition(addr1, addr2);
        LogEvent(SimulationEventType.PartitionHealed, $"Partition healed between nodes");
    }

    #endregion

    #region Time Control

    /// <summary>
    /// Advances simulation time by the specified duration.
    /// </summary>
    public void AdvanceTime(TimeSpan duration)
    {
        TimeProvider.Advance(duration);
        LogEvent(SimulationEventType.TimeAdvanced, $"Time advanced by {duration}");
    }

    /// <summary>
    /// Advances simulation time and executes any tasks that become ready.
    /// </summary>
    public int AdvanceTimeAndStep(TimeSpan duration)
    {
        using var _ = _syncContext.Install();

        TimeProvider.Advance(duration);
        LogEvent(SimulationEventType.TimeAdvanced, $"Time advanced by {duration}");
        return StepAllCore();
    }

    #endregion

    #region Stepping

    /// <summary>
    /// Executes a single pending task.
    /// </summary>
    public bool Step()
    {
        using var _ = _syncContext.Install();
        return StepCore();
    }

    /// <summary>
    /// Executes the specified number of pending tasks.
    /// </summary>
    public int Step(int count)
    {
        using var _ = _syncContext.Install();
        return StepCore(count);
    }

    /// <summary>
    /// Executes all pending tasks.
    /// </summary>
    public int StepAll()
    {
        using var _ = _syncContext.Install();
        return StepAllCore();
    }

    /// <summary>
    /// Core implementation of Step without context installation (for internal use).
    /// </summary>
    private bool StepCore()
    {
        var result = Scheduler.TryExecuteOne();
        if (result)
        {
            LogicalTime++;
        }
        return result;
    }

    /// <summary>
    /// Core implementation of Step(count) without context installation (for internal use).
    /// </summary>
    private int StepCore(int count)
    {
        var executed = Scheduler.Step(count);
        LogicalTime += executed;
        return executed;
    }

    /// <summary>
    /// Core implementation of StepAll without context installation (for internal use).
    /// </summary>
    private int StepAllCore()
    {
        var executed = Scheduler.StepAll();
        LogicalTime += executed;
        return executed;
    }

    #endregion

    #region Simulation Driving

    /// <summary>
    /// Runs the simulation until the specified condition is met.
    /// </summary>
    public bool RunUntil(Func<bool> condition, int maxIterations = 100000)
    {
        ArgumentNullException.ThrowIfNull(condition);

        using var _ = _syncContext.Install();

        return RunUntilCore(condition, maxIterations);
    }

    /// <summary>
    /// Core implementation of RunUntil without context installation (for internal use).
    /// </summary>
    private bool RunUntilCore(Func<bool> condition, int maxIterations)
    {
        for (var i = 0; i < maxIterations; i++)
        {
            if (condition())
            {
                LogEvent(SimulationEventType.ConditionMet, $"Condition met after {i} iterations");
                return true;
            }

            if (Scheduler.TryExecuteOne())
            {
                LogicalTime++;
                continue;
            }

            if (!AdvanceToNextScheduledTime())
            {
                return condition();
            }
        }

        LogEvent(SimulationEventType.MaxStepsReached, $"Max iterations ({maxIterations}) reached");
        return false;
    }

    /// <summary>
    /// Runs until all nodes have the expected membership size.
    /// </summary>
    public bool RunUntilConverged(int expectedSize, int maxIterations = 100000) =>
        RunUntil(() => Nodes.All(n => n.MembershipSize == expectedSize), maxIterations);

    /// <summary>
    /// Runs the simulation until it becomes idle.
    /// </summary>
    public bool RunUntilIdle(TimeSpan? maxSimulatedTime = null, int maxIterations = 100000)
    {
        using var _ = _syncContext.Install();

        return RunUntilIdleCore(maxSimulatedTime, maxIterations);
    }

    /// <summary>
    /// Core implementation of RunUntilIdle without context installation (for internal use).
    /// </summary>
    private bool RunUntilIdleCore(TimeSpan? maxSimulatedTime, int maxIterations)
    {
        var startTime = TimeProvider.GetUtcNow();
        var maxEndTime = maxSimulatedTime.HasValue ? startTime + maxSimulatedTime.Value : DateTimeOffset.MaxValue;

        for (var i = 0; i < maxIterations; i++)
        {
            if (Scheduler.TryExecuteOne())
            {
                LogicalTime++;
                continue;
            }

            var nextScheduledTime = GetNextScheduledTime();
            if (!nextScheduledTime.HasValue)
            {
                LogEvent(SimulationEventType.ConditionMet, "Simulation reached idle state");
                return true;
            }

            if (nextScheduledTime.Value > maxEndTime)
            {
                LogEvent(SimulationEventType.MaxStepsReached, $"Max simulated time ({maxSimulatedTime}) reached");
                return false;
            }

            AdvanceToNextScheduledTime();
        }

        LogEvent(SimulationEventType.MaxStepsReached, $"Max iterations ({maxIterations}) reached");
        return false;
    }

    /// <summary>
    /// Drives a task to completion by running the simulation.
    /// </summary>
    public T DriveToCompletion<T>(Func<Task<T>> taskFactory, int maxIterations = 100000)
    {
        ArgumentNullException.ThrowIfNull(taskFactory);

        using var _ = _syncContext.Install();

        var task = taskFactory();

        if (!RunUntilCore(() => task.IsCompleted, maxIterations))
        {
            if (!task.IsCompleted)
            {
                throw new TimeoutException($"Task did not complete within {maxIterations} iterations");
            }
        }

        return task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Drives a task to completion by running the simulation.
    /// </summary>
    public void DriveToCompletion(Func<Task> taskFactory, int maxIterations = 100000)
    {
        ArgumentNullException.ThrowIfNull(taskFactory);

        using var _ = _syncContext.Install();

        var task = taskFactory();

        if (!RunUntilCore(() => task.IsCompleted, maxIterations))
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
                $"Current sizes: [{string.Join(", ", _nodes.Select(n => n.MembershipSize))}]");
        }
    }

    /// <summary>
    /// Waits for convergence (async version for compatibility).
    /// </summary>
    public Task WaitForConvergenceAsync(
        int expectedSize,
        TimeSpan timeout,
        TimeSpan? stepSize = null,
        CancellationToken cancellationToken = default)
    {
        WaitForConvergence(expectedSize);
        return Task.CompletedTask;
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
    /// Waits for a specific node to reach the expected membership size (async version for compatibility).
    /// </summary>
    public Task WaitForNodeSizeAsync(
        SimulationNode node,
        int expectedSize,
        TimeSpan timeout,
        TimeSpan? stepSize = null,
        CancellationToken cancellationToken = default)
    {
        WaitForNodeSize(node, expectedSize);
        return Task.CompletedTask;
    }

    private DateTimeOffset? GetNextScheduledTime()
    {
        var nextQueueDueTime = Scheduler.TaskQueue.NextWaitingDueTimeTicks;
        var nextTimerDueTime = TimeProvider.NextTimerDueTicks;

        long? nextDueTime = null;

        if (nextQueueDueTime.HasValue && nextTimerDueTime.HasValue)
        {
            nextDueTime = Math.Min(nextQueueDueTime.Value, nextTimerDueTime.Value);
        }
        else if (nextQueueDueTime.HasValue)
        {
            nextDueTime = nextQueueDueTime.Value;
        }
        else if (nextTimerDueTime.HasValue)
        {
            nextDueTime = nextTimerDueTime.Value;
        }

        return nextDueTime.HasValue ? new DateTimeOffset(nextDueTime.Value, TimeSpan.Zero) : null;
    }

    private bool AdvanceToNextScheduledTime()
    {
        var nextScheduledTime = GetNextScheduledTime();
        if (!nextScheduledTime.HasValue)
        {
            return false;
        }

        if (nextScheduledTime.Value > TimeProvider.GetUtcNow())
        {
            TimeProvider.SetUtcNow(nextScheduledTime.Value);
        }

        return true;
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
            lock (_eventLogLock)
            {
                return [.. _eventLog];
            }
        }
    }

    /// <summary>
    /// Logs the seed to the test output for reproduction.
    /// </summary>
    public void LogSeedForReproduction() => _testOutput?.WriteLine($"[SEED FOR REPRODUCTION] {Seed}");

    /// <summary>
    /// Dumps the event log to the test output.
    /// </summary>
    public void DumpEventLog()
    {
        if (_testOutput == null) return;

        _testOutput.WriteLine("=== Simulation Event Log ===");
        lock (_eventLogLock)
        {
            foreach (var evt in _eventLog)
            {
                _testOutput.WriteLine($"[{evt.LogicalTime:D6}] [{evt.SimulatedTime:O}] {evt.Type}: {evt.Description}");
            }
        }
        _testOutput.WriteLine("============================");
    }

    private void LogEvent(SimulationEventType type, string description)
    {
        var evt = new SimulationEvent(
            LogicalTime,
            TimeProvider.GetUtcNow(),
            type,
            description);

        lock (_eventLogLock)
        {
            _eventLog.Add(evt);
        }

        _testOutput?.WriteLine($"[{evt.LogicalTime:D6}] {type}: {description}");
    }

    #endregion

    #region Disposal

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        Scheduler.Clear();

        foreach (var node in _nodes)
        {
            node.Shutdown();
            node.Dispose();
        }
        _nodes.Clear();
        _nodeRegistry.Clear();

        await Task.CompletedTask.ConfigureAwait(false);
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
