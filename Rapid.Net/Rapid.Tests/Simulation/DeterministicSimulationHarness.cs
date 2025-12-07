using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Rapid.Tests.Simulation;

/// <summary>
/// Extended simulation harness for fully deterministic testing.
/// Provides control over task scheduling, time advancement, and event logging.
/// </summary>
internal sealed class DeterministicSimulationHarness : IAsyncDisposable
{
    private readonly DeterministicSynchronizationContext _syncContext;
    private readonly SynchronizationContext? _previousSyncContext;
    private readonly List<SimulationEvent> _eventLog = [];
    private readonly Lock _eventLogLock = new();
    private readonly ITestOutputHelper? _testOutput;

    /// <summary>
    /// Creates a new deterministic simulation harness.
    /// </summary>
    /// <param name="seed">The seed for deterministic random number generation.</param>
    /// <param name="loggerFactory">Optional logger factory.</param>
    /// <param name="testOutput">Optional xUnit test output helper for logging.</param>
    public DeterministicSimulationHarness(
        int seed,
        ILoggerFactory? loggerFactory = null,
        ITestOutputHelper? testOutput = null)
    {
        _testOutput = testOutput;
        InnerHarness = new SimulationTestHarness(seed, loggerFactory, useFakeTime: true);
        Scheduler = new DeterministicTaskScheduler(InnerHarness.FakeTimeProvider);
        _syncContext = new DeterministicSynchronizationContext(Scheduler);
        _previousSyncContext = _syncContext.Install();

        LogEvent(SimulationEventType.HarnessCreated, $"Seed: {seed}");
    }

    /// <summary>
    /// Creates a deterministic harness with a random seed, logging the seed for reproduction.
    /// </summary>
    public static DeterministicSimulationHarness CreateWithRandomSeed(
        ILoggerFactory? loggerFactory = null,
        ITestOutputHelper? testOutput = null)
    {
        var seed = Environment.TickCount;
        testOutput?.WriteLine($"[DeterministicSimulation] Using random seed: {seed}");
        return new DeterministicSimulationHarness(seed, loggerFactory, testOutput);
    }

    /// <summary>
    /// Gets the underlying simulation harness.
    /// </summary>
    public SimulationTestHarness InnerHarness { get; }

    /// <summary>
    /// Gets the deterministic task scheduler.
    /// </summary>
    public DeterministicTaskScheduler Scheduler { get; }

    /// <summary>
    /// Gets the fake time provider.
    /// </summary>
    public FakeTimeProvider TimeProvider => InnerHarness.FakeTimeProvider!;

    /// <summary>
    /// Gets the deterministic random instance.
    /// </summary>
    public DeterministicRandom Random => InnerHarness.Random;

    /// <summary>
    /// Gets the simulation network.
    /// </summary>
    public SimulationNetwork Network => InnerHarness.Network;

    /// <summary>
    /// Gets all nodes in the simulation.
    /// </summary>
    public IReadOnlyList<SimulationNode> Nodes => InnerHarness.Nodes;

    /// <summary>
    /// Gets the seed used for this harness.
    /// </summary>
    public int Seed => InnerHarness.Seed;

    /// <summary>
    /// Gets the current logical time (number of steps executed).
    /// </summary>
    public long LogicalTime { get; private set; }

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
    /// Creates and starts a new seed node.
    /// </summary>
    public SimulationNode CreateSeedNode(int nodeId = 0, RapidProtocolOptions? options = null)
    {
        var node = InnerHarness.CreateSeedNode(nodeId, options);
        LogEvent(SimulationEventType.NodeCreated, $"Seed node {nodeId} created");
        return node;
    }

    /// <summary>
    /// Creates and joins a new node to the cluster.
    /// Note: This requires stepping the scheduler to complete the join.
    /// </summary>
    public Task<SimulationNode> CreateJoinerNodeAsync(
        SimulationNode seedNode,
        int nodeId,
        RapidProtocolOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        LogEvent(SimulationEventType.NodeJoining, $"Node {nodeId} joining via seed");
        return InnerHarness.CreateJoinerNodeAsync(seedNode, nodeId, options, cancellationToken);
    }

    /// <summary>
    /// Executes a single pending task.
    /// </summary>
    /// <returns>True if a task was executed.</returns>
    public bool Step()
    {
        var result = Scheduler.TryExecuteOne();
        if (result)
        {
            LogicalTime++;
        }
        return result;
    }

    /// <summary>
    /// Executes the specified number of pending tasks.
    /// </summary>
    /// <param name="count">The maximum number of tasks to execute.</param>
    /// <returns>The number of tasks actually executed.</returns>
    public int Step(int count)
    {
        var executed = Scheduler.Step(count);
        LogicalTime += executed;
        return executed;
    }

    /// <summary>
    /// Executes all pending tasks.
    /// </summary>
    /// <returns>The number of tasks executed.</returns>
    public int StepAll()
    {
        var executed = Scheduler.StepAll();
        LogicalTime += executed;
        return executed;
    }

    /// <summary>
    /// Runs the simulation until the specified condition is met.
    /// Alternates between stepping tasks and advancing time.
    /// </summary>
    /// <param name="condition">The condition to check.</param>
    /// <param name="maxSteps">Maximum number of steps.</param>
    /// <param name="timeStepSize">Time to advance when no tasks are pending.</param>
    /// <returns>True if the condition was met, false if max steps reached.</returns>
    public bool RunUntil(
        Func<bool> condition,
        int maxSteps = 10000,
        TimeSpan? timeStepSize = null)
    {
        ArgumentNullException.ThrowIfNull(condition);
        var timeStep = timeStepSize ?? TimeSpan.FromMilliseconds(10);

        for (var i = 0; i < maxSteps; i++)
        {
            if (condition())
            {
                LogEvent(SimulationEventType.ConditionMet, $"Condition met after {i} iterations");
                return true;
            }

            // Try to execute pending tasks
            if (Scheduler.TryExecuteOne())
            {
                LogicalTime++;
            }
            else
            {
                // No pending tasks, advance time to trigger timers
                TimeProvider.Advance(timeStep);
            }
        }

        LogEvent(SimulationEventType.MaxStepsReached, $"Max steps ({maxSteps}) reached without condition");
        return false;
    }

    /// <summary>
    /// Runs until all nodes have the expected membership size.
    /// </summary>
    public bool RunUntilConverged(int expectedSize, int maxSteps = 10000) => RunUntil(() => Nodes.All(n => n.MembershipSize == expectedSize), maxSteps);

    /// <summary>
    /// Advances simulation time and executes any tasks that become ready.
    /// </summary>
    /// <param name="duration">The duration to advance.</param>
    /// <returns>The number of tasks executed.</returns>
    public int AdvanceTimeAndStep(TimeSpan duration)
    {
        TimeProvider.Advance(duration);
        LogEvent(SimulationEventType.TimeAdvanced, $"Time advanced by {duration}");
        return StepAll();
    }

    /// <summary>
    /// Skips time to the next scheduled event.
    /// </summary>
    /// <param name="maxSkip">Maximum time to skip.</param>
    /// <returns>True if time was skipped to an event, false if no events pending.</returns>
    public bool FastForwardToNextEvent(TimeSpan? maxSkip = null)
    {
        var max = maxSkip ?? TimeSpan.FromHours(1);

        // If there are pending tasks, don't skip
        if (Scheduler.HasPendingTasks)
        {
            return false;
        }

        // Advance time in small increments until tasks appear
        var elapsed = TimeSpan.Zero;
        var increment = TimeSpan.FromMilliseconds(1);

        while (elapsed < max && !Scheduler.HasPendingTasks)
        {
            TimeProvider.Advance(increment);
            elapsed += increment;
        }

        if (Scheduler.HasPendingTasks)
        {
            LogEvent(SimulationEventType.FastForward, $"Fast-forwarded {elapsed}");
            return true;
        }

        return false;
    }

    /// <summary>
    /// Injects a network partition between two nodes.
    /// </summary>
    public void PartitionNodes(SimulationNode node1, SimulationNode node2)
    {
        InnerHarness.PartitionNodes(node1, node2);
        LogEvent(SimulationEventType.PartitionCreated, $"Partition between nodes");
    }

    /// <summary>
    /// Heals a network partition between two nodes.
    /// </summary>
    public void HealPartition(SimulationNode node1, SimulationNode node2)
    {
        InnerHarness.HealPartition(node1, node2);
        LogEvent(SimulationEventType.PartitionHealed, $"Partition healed between nodes");
    }

    /// <summary>
    /// Isolates a node from the cluster.
    /// </summary>
    public void IsolateNode(SimulationNode node)
    {
        InnerHarness.IsolateNode(node);
        LogEvent(SimulationEventType.NodeIsolated, $"Node isolated");
    }

    /// <summary>
    /// Reconnects an isolated node.
    /// </summary>
    public void ReconnectNode(SimulationNode node)
    {
        InnerHarness.ReconnectNode(node);
        LogEvent(SimulationEventType.NodeReconnected, $"Node reconnected");
    }

    /// <summary>
    /// Crashes a node (simulates sudden failure).
    /// </summary>
    public void CrashNode(SimulationNode node)
    {
        InnerHarness.CrashNode(node);
        LogEvent(SimulationEventType.NodeCrashed, $"Node crashed");
    }

    /// <summary>
    /// Gracefully removes a node.
    /// </summary>
    public Task RemoveNodeGracefullyAsync(SimulationNode node)
    {
        LogEvent(SimulationEventType.NodeLeaving, $"Node leaving gracefully");
        return InnerHarness.RemoveNodeGracefullyAsync(node);
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

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        DeterministicSynchronizationContext.Restore(_previousSyncContext);
        Scheduler.Clear();
        await InnerHarness.DisposeAsync().ConfigureAwait(false);
    }
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
