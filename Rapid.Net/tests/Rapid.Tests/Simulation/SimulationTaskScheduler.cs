namespace Rapid.Tests.Simulation;

/// <summary>
/// A scheduled item representing a Task from the TaskScheduler.
/// </summary>
internal sealed class ScheduledTaskItem : ScheduledItem
{
    private readonly Action _executeTask;

    public ScheduledTaskItem(Task task, Action executeTask)
    {
        Task = task;
        _executeTask = executeTask;
    }

    /// <summary>
    /// The Task object being scheduled.
    /// </summary>
    public Task Task { get; }

    /// <inheritdoc />
    protected internal override void Invoke() => _executeTask();
}

/// <summary>
/// A deterministic task scheduler that queues tasks through a <see cref="SimulationTaskQueue"/>
/// and executes them only when explicitly stepped.
/// </summary>
internal sealed class SimulationTaskScheduler(SimulationTaskQueue taskQueue) : TaskScheduler
{
    protected override IEnumerable<Task>? GetScheduledTasks() =>
        taskQueue.GetItemsOfType<ScheduledTaskItem, Task>(item => item.Task);

    protected override void QueueTask(Task task) =>
        taskQueue.Enqueue(new ScheduledTaskItem(task, () => TryExecuteTask(task)));

    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) =>
        // For deterministic testing, we don't execute inline
        // All tasks go through the queue
        false;
}
