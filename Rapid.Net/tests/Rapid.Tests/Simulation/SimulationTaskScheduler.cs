namespace Rapid.Tests.Simulation;

/// <summary>
/// A deterministic task scheduler that queues tasks through a <see cref="SimulationTaskQueue"/>
/// and executes them only when explicitly stepped.
/// </summary>
internal sealed class SimulationTaskScheduler(SimulationTaskQueue taskQueue) : TaskScheduler
{
    protected override IEnumerable<Task>? GetScheduledTasks() => taskQueue.GetScheduledTasks();

    protected override void QueueTask(Task task) => taskQueue.EnqueueTask(task, () => TryExecuteTask(task));

    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) =>
        // For deterministic testing, we don't execute inline
        // All tasks go through the queue
        false;
}
