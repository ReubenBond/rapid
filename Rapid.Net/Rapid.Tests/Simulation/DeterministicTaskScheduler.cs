using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;

namespace Rapid.Tests.Simulation;

/// <summary>
/// A deterministic task scheduler that queues tasks and executes them only when explicitly stepped.
/// This enables fully deterministic simulation testing by controlling task execution order.
/// </summary>
internal sealed class DeterministicTaskScheduler : TaskScheduler
{
    private readonly PriorityQueue<ScheduledTask, long> _taskQueue = new();
    private readonly Lock _lock = new();
    private readonly FakeTimeProvider? _timeProvider;
    private long _sequenceNumber;

    /// <summary>
    /// Creates a new deterministic task scheduler.
    /// </summary>
    /// <param name="timeProvider">Optional time provider for time-based ordering.</param>
    public DeterministicTaskScheduler(FakeTimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Gets the number of pending tasks in the queue.
    /// </summary>
    public int PendingCount
    {
        get
        {
            lock (_lock)
            {
                return _taskQueue.Count;
            }
        }
    }

    /// <summary>
    /// Gets whether there are any pending tasks.
    /// </summary>
    public bool HasPendingTasks
    {
        get
        {
            lock (_lock)
            {
                return _taskQueue.Count > 0;
            }
        }
    }

    /// <inheritdoc />
    protected override IEnumerable<Task>? GetScheduledTasks()
    {
        lock (_lock)
        {
            // Return a snapshot of the scheduled tasks
            return _taskQueue.UnorderedItems.Select(x => x.Element.Task).ToList();
        }
    }

    /// <inheritdoc />
    protected override void QueueTask(Task task)
    {
        lock (_lock)
        {
            var scheduledTask = new ScheduledTask(task, _timeProvider?.GetUtcNow().Ticks ?? 0);
            var priority = GetPriority(scheduledTask);
            _taskQueue.Enqueue(scheduledTask, priority);
        }
    }

    /// <inheritdoc />
    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) =>
        // For deterministic testing, we don't execute inline
        // All tasks go through the queue
        false;

    /// <summary>
    /// Tries to dequeue and execute a single task.
    /// </summary>
    /// <returns>True if a task was executed, false if no tasks were pending.</returns>
    public bool TryExecuteOne()
    {
        ScheduledTask scheduledTask;

        lock (_lock)
        {
            if (!_taskQueue.TryDequeue(out scheduledTask, out _))
            {
                return false;
            }
        }

        TryExecuteTask(scheduledTask.Task);
        return true;
    }

    /// <summary>
    /// Executes the specified number of pending tasks.
    /// </summary>
    /// <param name="count">The maximum number of tasks to execute.</param>
    /// <returns>The number of tasks actually executed.</returns>
    public int Step(int count = 1)
    {
        var executed = 0;
        for (var i = 0; i < count; i++)
        {
            if (!TryExecuteOne())
            {
                break;
            }
            executed++;
        }
        return executed;
    }

    /// <summary>
    /// Executes all pending tasks.
    /// </summary>
    /// <returns>The number of tasks executed.</returns>
    public int StepAll()
    {
        var executed = 0;
        while (TryExecuteOne())
        {
            executed++;
        }
        return executed;
    }

    /// <summary>
    /// Executes tasks until the specified condition is met or no more tasks are pending.
    /// </summary>
    /// <param name="condition">The condition to check after each task execution.</param>
    /// <param name="maxSteps">Maximum number of steps to execute.</param>
    /// <returns>The number of tasks executed.</returns>
    public int StepUntil(Func<bool> condition, int maxSteps = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(condition);

        var executed = 0;
        while (executed < maxSteps && !condition())
        {
            if (!TryExecuteOne())
            {
                break;
            }
            executed++;
        }
        return executed;
    }

    /// <summary>
    /// Clears all pending tasks without executing them.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _taskQueue.Clear();
        }
    }

    private long GetPriority(ScheduledTask task)
    {
        // Priority is based on scheduled time (ticks) and sequence number for deterministic ordering
        // Lower priority = executed first
        lock (_lock)
        {
            return (task.ScheduledTicks << 20) | (_sequenceNumber++ & 0xFFFFF);
        }
    }

    private readonly record struct ScheduledTask(Task Task, long ScheduledTicks);
}
