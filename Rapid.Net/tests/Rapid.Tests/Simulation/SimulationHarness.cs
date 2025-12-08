using System.Collections.Concurrent;
using MartinCostello.Logging.XUnit;
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
    private readonly List<SimulationEvent> _eventLog = [];
    private readonly Lock _eventLogLock = new();
    private readonly Lock _randomLock = new();
    private readonly ILogger<SimulationHarness>? _logger;
    private readonly bool _ownsLoggerFactory;
    private bool _disposed;

    /// <summary>
    /// Creates a new simulation harness with the specified seed and test output helper.
    /// The logger factory will be created automatically from the test output helper.
    /// </summary>
    /// <param name="seed">The seed for deterministic random number generation.</param>
    /// <param name="testOutput">The xUnit test output helper for logging.</param>
    public SimulationHarness(int seed, ITestOutputHelper testOutput)
        : this(seed, CreateLoggerFactory(testOutput), ownsLoggerFactory: true)
    {
    }

    /// <summary>
    /// Creates a new simulation harness with the specified seed, logger factory, and test output.
    /// </summary>
    /// <param name="seed">The seed for deterministic random number generation.</param>
    /// <param name="loggerFactory">The logger factory for logging (can be null).</param>
    /// <param name="ownsLoggerFactory">Whether this harness owns the logger factory and should dispose it.</param>
    private SimulationHarness(
        int seed,
        ILoggerFactory? loggerFactory,
        bool ownsLoggerFactory)
    {
        Seed = seed;
        LoggerFactory = loggerFactory;
        _logger = loggerFactory?.CreateLogger<SimulationHarness>();
        _ownsLoggerFactory = ownsLoggerFactory;

        // Create deterministic components
        Random = new SimulationRandom(seed);
        _taskQueue = new SimulationTaskQueue();
        _taskScheduler = new SimulationTaskScheduler(_taskQueue);
        _synchronizationContext = _taskQueue.SynchronizationContext;

        // Create time provider that shares the task queue with the scheduler
        var timeProviderLogger = loggerFactory?.CreateLogger<SimulationTimeProvider>();
        TimeProvider = new SimulationTimeProvider(_taskQueue, DateTimeOffset.UtcNow, timeProviderLogger);

        // Create network
        Network = new SimulationNetwork(this);

        LogEvent(SimulationEventType.HarnessCreated, $"Seed: {seed}");
    }

    /// <summary>
    /// Creates an ILoggerFactory that writes to the xUnit test output.
    /// </summary>
    private static ILoggerFactory CreateLoggerFactory(ITestOutputHelper testOutput) =>
        Microsoft.Extensions.Logging.LoggerFactory.Create(builder => builder
            .AddXUnit(testOutput)
            .SetMinimumLevel(LogLevel.Debug));

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

    private readonly SimulationTaskQueue _taskQueue;
    private readonly SimulationTaskScheduler _taskScheduler;
    private readonly SimulationSynchronizationContext _synchronizationContext;

    /// <summary>
    /// Gets the simulation task queue.
    /// </summary>
    public SimulationTaskQueue TaskQueue => _taskQueue;

    /// <summary>
    /// Gets the simulation task scheduler.
    /// </summary>
    public TaskScheduler Scheduler => _taskScheduler;

    /// <summary>
    /// Gets the simulation synchronization context.
    /// </summary>
    public SimulationSynchronizationContext SynchronizationContext => _synchronizationContext;

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
#pragma warning disable CA5394 // Do not use insecure randomness
            return new SimulationRandom(Random.Next());
#pragma warning restore CA5394 // Do not use insecure randomness
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

        using var _ = _synchronizationContext.Install();

        return RunUntilCore(condition, maxIterations);
    }

    /// <summary>
    /// Core implementation of RunUntil without context installation (for internal use).
    /// </summary>
    private bool RunUntilCore(Func<bool> condition, int maxIterations)
    {
        var startTime = TimeProvider.GetUtcNow();
        var maxEndTime = startTime + MaxSimulatedTimeAdvance;
        var timeAdvanceCount = 0;

        for (var i = 0; i < maxIterations; i++)
        {
            if (condition())
            {
                LogEvent(SimulationEventType.ConditionMet, $"Condition met after {i} iterations");
                return true;
            }

            if (_taskQueue.TryExecuteNext())
            {
                LogicalTime++;
                timeAdvanceCount = 0; // Reset time advance counter when real work happens
                continue;
            }

            // No tasks to execute - need to advance time
            var nextScheduledTime = GetNextScheduledTime();
            if (!nextScheduledTime.HasValue)
            {
                // No more scheduled work - simulation is idle and cannot make progress
                LogEvent(SimulationEventType.MaxStepsReached,
                    $"Simulation is idle with no pending work - condition cannot be met. " +
                    $"Iterations: {i}, Simulated time: {TimeProvider.GetUtcNow():O}");
                return false;
            }

            // Check if we've been advancing time without making progress
            if (nextScheduledTime.Value > maxEndTime)
            {
                LogEvent(SimulationEventType.MaxStepsReached,
                    $"Simulation appears stuck: exceeded max simulated time ({MaxSimulatedTimeAdvance}). " +
                    $"Start: {startTime:O}, Current: {TimeProvider.GetUtcNow():O}, Next scheduled: {nextScheduledTime.Value:O}");
                return false;
            }

            // Advance time
            TimeProvider.SetUtcNow(nextScheduledTime.Value);
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
    /// Runs until all nodes have the expected membership size.
    /// </summary>
    public bool RunUntilConverged(int expectedSize, int maxIterations = 100000) =>
        RunUntil(() => Nodes.All(n => n.MembershipSize == expectedSize), maxIterations);

    /// <summary>
    /// Runs the simulation until it becomes idle.
    /// </summary>
    public bool RunUntilIdle(TimeSpan? maxSimulatedTime = null, int maxIterations = 100000)
    {
        using var _ = _synchronizationContext.Install();

        return RunUntilIdleCore(maxSimulatedTime, maxIterations);
    }

    /// <summary>
    /// Core implementation of RunUntilIdle without context installation (for internal use).
    /// </summary>
    private bool RunUntilIdleCore(TimeSpan? maxSimulatedTime, int maxIterations)
    {
        var startTime = TimeProvider.GetUtcNow();
        var maxEndTime = startTime + (maxSimulatedTime ?? MaxSimulatedTimeAdvance);
        var timeAdvanceCount = 0;

        for (var i = 0; i < maxIterations; i++)
        {
            if (_taskQueue.TryExecuteNext())
            {
                LogicalTime++;
                timeAdvanceCount = 0; // Reset time advance counter when real work happens
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
                LogEvent(SimulationEventType.MaxStepsReached,
                    $"Simulation appears stuck: exceeded max simulated time ({maxSimulatedTime ?? MaxSimulatedTimeAdvance}). " +
                    $"Start: {startTime:O}, Current: {TimeProvider.GetUtcNow():O}, Next scheduled: {nextScheduledTime.Value:O}");
                return false;
            }

            TimeProvider.SetUtcNow(nextScheduledTime.Value);
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
    /// Drives a task to completion by running the simulation.
    /// </summary>
    public T DriveToCompletion<T>(Func<Task<T>> taskFactory, int maxIterations = 100000)
    {
        ArgumentNullException.ThrowIfNull(taskFactory);

        using var _ = _synchronizationContext.Install();

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

        using var _ = _synchronizationContext.Install();

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
    /// Advances simulated time by the specified amount and runs the simulation until idle.
    /// This is the preferred method for advancing time in tests, as it ensures that any
    /// tasks triggered by timers are processed before returning.
    /// </summary>
    /// <param name="delta">The amount of time to advance.</param>
    /// <param name="maxIterations">Maximum iterations to run while processing tasks.</param>
    /// <returns>True if the simulation reached an idle state; false if max iterations reached.</returns>
    public bool AdvanceTime(TimeSpan delta, int maxIterations = 100000)
    {
        if (delta < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delta), "Time delta cannot be negative");
        }

        if (delta == TimeSpan.Zero)
        {
            return true;
        }

        using var _ = _synchronizationContext.Install();

        var targetTime = TimeProvider.GetUtcNow() + delta;

        // Advance time to trigger timers, then run until idle
        TimeProvider.SetUtcNow(targetTime);
        LogEvent(SimulationEventType.TimeAdvanced, $"Advanced time by {delta}");

        return RunUntilIdleCore(maxSimulatedTime: null, maxIterations);
    }

    private DateTimeOffset? GetNextScheduledTime()
    {
        var nextDueTime = _taskQueue.NextWaitingDueTime;
        return nextDueTime.HasValue ? TimeProvider.Start + nextDueTime.Value : null;
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
    public void LogSeedForReproduction() => _logger?.LogInformation("[SEED FOR REPRODUCTION] {Seed}", Seed);

    /// <summary>
    /// Dumps the event log to the test output.
    /// </summary>
    public void DumpEventLog()
    {
        if (_logger == null) return;

        _logger.LogInformation("=== Simulation Event Log ===");
        lock (_eventLogLock)
        {
            foreach (var evt in _eventLog)
            {
                _logger.LogInformation("[{LogicalTime:D6}] [{SimulatedTime:O}] {Type}: {Description}",
                    evt.LogicalTime, evt.SimulatedTime, evt.Type, evt.Description);
            }
        }
        _logger.LogInformation("============================");
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

        _logger?.LogDebug("[{LogicalTime:D6}] {Type}: {Description}", evt.LogicalTime, type, description);
    }

    #endregion

    #region Disposal

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _taskQueue.Clear();

        foreach (var node in _nodes)
        {
            node.Shutdown();
            node.Dispose();
        }
        _nodes.Clear();
        _nodeRegistry.Clear();

        if (_ownsLoggerFactory)
        {
            LoggerFactory?.Dispose();
        }

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
