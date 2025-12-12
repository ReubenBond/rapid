namespace Rapid.Tests.Simulation.Infrastructure;

/// <summary>
/// A deterministic task scheduler that queues tasks through a <see cref="SimulationTaskQueue"/>
/// and executes them only when explicitly stepped.
/// </summary>
internal sealed class SimulationTaskScheduler(SimulationTaskQueue taskQueue) : TaskScheduler
{
    protected override IEnumerable<Task>? GetScheduledTasks() => taskQueue.GetItemsOfType<ScheduledTaskItem, Task>(item => item.Task);

    internal IReadOnlyList<Task> Tasks => taskQueue.GetItemsOfType<ScheduledTaskItem, Task>(item => item.Task);

    protected override void QueueTask(Task task) => taskQueue.Enqueue(new ScheduledTaskItem(task, this));

    // For deterministic testing, we don't execute inline
    // All tasks go through the queue
    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

    public object UnderlyingScheduler => taskQueue;

    public bool IsSameScheduler(SynchronizationContext syncCtx) => syncCtx is SimulationSynchronizationContext simSyncCtx && simSyncCtx.UnderlyingScheduler.Equals(UnderlyingScheduler);

    private sealed class ScheduledTaskItem(Task task, SimulationTaskScheduler scheduler) : ScheduledItem
    {
        public Task Task => task;

        protected internal override void Invoke() => scheduler.TryExecuteTask(task);
    }
}
