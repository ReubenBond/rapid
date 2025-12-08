namespace Rapid.Tests.Simulation;

/// <summary>
/// A deterministic task scheduler that queues tasks and executes them only when explicitly stepped.
/// This enables fully deterministic simulation testing by controlling task execution order.
/// 
/// This is a thin adapter that provides a <see cref="TaskScheduler"/> interface over
/// <see cref="SimulationTaskQueue"/>. Use <see cref="SimulationHarness"/> for high-level
/// simulation control including time advancement and running until conditions are met.
/// </summary>
internal sealed class SimulationTaskScheduler : TaskScheduler
{
    private readonly SimulationTaskQueue _taskQueue;

    /// <summary>
    /// Creates a new simulation task scheduler with the specified task queue.
    /// </summary>
    /// <param name="taskQueue">The task queue to use for scheduling.</param>
    public SimulationTaskScheduler(SimulationTaskQueue taskQueue)
    {
        ArgumentNullException.ThrowIfNull(taskQueue);
        _taskQueue = taskQueue;
    }

    /// <summary>
    /// Gets the underlying task queue.
    /// </summary>
    public SimulationTaskQueue TaskQueue => _taskQueue;

    /// <summary>
    /// Gets the number of pending tasks tracked by this scheduler.
    /// </summary>
    public int PendingCount => _taskQueue.ScheduledTaskCount;

    /// <summary>
    /// Gets whether there are any pending tasks tracked by this scheduler.
    /// </summary>
    public bool HasPendingTasks => _taskQueue.ScheduledTaskCount > 0;

    /// <summary>
    /// Gets whether the scheduler is idle (no ready tasks and no waiting tasks in the queue).
    /// </summary>
    public bool IsIdle => !_taskQueue.HasItems;

    /// <inheritdoc />
    protected override IEnumerable<Task>? GetScheduledTasks() => _taskQueue.GetScheduledTasks();

    /// <inheritdoc />
    protected override void QueueTask(Task task)
    {
        _taskQueue.EnqueueTask(task, () => TryExecuteTask(task));
    }

    /// <inheritdoc />
    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) =>
        // For deterministic testing, we don't execute inline
        // All tasks go through the queue
        false;

    /// <summary>
    /// Tries to dequeue and execute a single ready task.
    /// </summary>
    /// <returns>True if a task was executed, false if no tasks are ready.</returns>
    public bool TryExecuteOne() => _taskQueue.TryExecuteNext();

    /// <summary>
    /// Executes the specified number of ready tasks.
    /// </summary>
    /// <param name="count">The maximum number of tasks to execute.</param>
    /// <returns>The number of tasks actually executed.</returns>
    public int Step(int count = 1)
    {
        var executed = 0;
        for (var i = 0; i < count && _taskQueue.TryExecuteNext(); i++)
        {
            executed++;
        }
        return executed;
    }

    /// <summary>
    /// Executes all ready tasks (tasks whose due time has been reached).
    /// </summary>
    /// <returns>The number of tasks executed.</returns>
    public int StepAll() => _taskQueue.ExecuteAll();

    /// <summary>
    /// Clears all pending tasks without executing them.
    /// </summary>
    public void Clear() => _taskQueue.Clear();
}
