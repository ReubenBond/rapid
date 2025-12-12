namespace Rapid.Tests.Simulation;

/// <summary>
/// Represents the execution state of a simulated node.
/// </summary>
internal enum SimulationNodeState
{
    /// <summary>
    /// The node is running and will execute tasks during simulation stepping.
    /// </summary>
    Running,

    /// <summary>
    /// The node is suspended and will not execute tasks.
    /// Messages sent to the node will be queued but not processed until resumed.
    /// Timers will accumulate and fire when the node is resumed if their due time has passed.
    /// </summary>
    Suspended
}

/// <summary>
/// Encapsulates all per-node simulation state, including the node's task queue,
/// task scheduler, synchronization context, time provider, and random number generator.
/// 
/// Each <see cref="SimulationNode"/> has its own context, allowing fine-grained control
/// over individual node execution (pause, resume, step) while sharing a unified
/// <see cref="SimulationClock"/> for time synchronization.
/// </summary>
internal sealed class SimulationNodeContext
{
    /// <summary>
    /// Creates a new simulation node context using the specified shared clock and random generator.
    /// </summary>
    /// <param name="clock">The shared simulation clock for time coordination.</param>
    /// <param name="random">The deterministic random number generator for this node.</param>
    public SimulationNodeContext(SimulationClock clock, SimulationRandom random)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(random);

        Clock = clock;
        Random = random;
        TaskQueue = new SimulationTaskQueue(clock);
        TaskScheduler = new SimulationTaskScheduler(TaskQueue);
        TimeProvider = new SimulationTimeProvider(TaskQueue, clock);
    }

    /// <summary>
    /// Gets the shared simulation clock.
    /// </summary>
    public SimulationClock Clock { get; }

    /// <summary>
    /// Gets the deterministic random number generator for this node.
    /// </summary>
    public SimulationRandom Random { get; }

    /// <summary>
    /// Gets the task queue for this node.
    /// Tasks scheduled on this queue are only executed when this node is stepped.
    /// </summary>
    public SimulationTaskQueue TaskQueue { get; }

    /// <summary>
    /// Gets the task scheduler for this node.
    /// Used for scheduling TPL tasks on this node's queue.
    /// </summary>
    public SimulationTaskScheduler TaskScheduler { get; }

    /// <summary>
    /// Gets the synchronization context for this node.
    /// Used for async/await continuations on this node's queue.
    /// </summary>
    public SimulationSynchronizationContext SynchronizationContext => TaskQueue.SynchronizationContext;

    /// <summary>
    /// Gets the time provider for this node.
    /// Timers created through this provider are scheduled on this node's queue.
    /// </summary>
    public SimulationTimeProvider TimeProvider { get; }

    /// <summary>
    /// Gets the current execution state of this node.
    /// </summary>
    public SimulationNodeState State { get; private set; } = SimulationNodeState.Running;

    /// <summary>
    /// Gets whether this node has any tasks ready to execute at the current time.
    /// </summary>
    public bool HasReadyTasks
    {
        get
        {
            if (State == SimulationNodeState.Suspended)
                return false;

            // Check if the queue has any items due at or before the current time
            var items = TaskQueue.ScheduledItems;
            if (items.Count == 0)
                return false;

            // The first item in the sorted set has the earliest due time
            // Use FirstOrDefault since IReadOnlySet doesn't have Min
            var firstItem = items.FirstOrDefault();
            return firstItem != null && firstItem.DueTime <= Clock.UtcNow;
        }
    }

    /// <summary>
    /// Gets the due time of the next waiting (not yet ready) task on this node's queue,
    /// or null if no tasks are waiting.
    /// </summary>
    public DateTimeOffset? NextWaitingDueTime => TaskQueue.NextWaitingDueTime;

    /// <summary>
    /// Executes one ready task from this node's queue.
    /// </summary>
    /// <returns>True if a task was executed; false if no tasks are ready or the node is suspended.</returns>
    public bool Step()
    {
        if (State == SimulationNodeState.Suspended)
            return false;

        return TaskQueue.RunOnce();
    }

    /// <summary>
    /// Executes all ready tasks from this node's queue.
    /// </summary>
    /// <returns>The number of tasks executed. Returns 0 if the node is suspended.</returns>
    public int RunUntilIdle()
    {
        if (State == SimulationNodeState.Suspended)
            return 0;

        return TaskQueue.RunUntilIdle();
    }

    /// <summary>
    /// Suspends this node, preventing it from executing tasks.
    /// Messages sent to the node will be queued but not processed until resumed.
    /// </summary>
    internal void Suspend()
    {
        State = SimulationNodeState.Suspended;
    }

    /// <summary>
    /// Resumes this node, allowing it to execute tasks again.
    /// Any tasks that became ready while suspended will be executed on subsequent steps.
    /// </summary>
    internal void Resume()
    {
        State = SimulationNodeState.Running;
    }

    /// <summary>
    /// Clears all pending tasks from this node's queue.
    /// Typically called when a node is crashed or removed from the simulation.
    /// </summary>
    internal void Clear()
    {
        TaskQueue.Clear();
    }
}
