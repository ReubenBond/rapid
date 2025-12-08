using Microsoft.Extensions.Time.Testing;

namespace Rapid.Tests.Simulation;

/// <summary>
/// A deterministic task scheduler that queues tasks and executes them only when explicitly stepped.
/// This enables fully deterministic simulation testing by controlling task execution order.
/// 
/// When <see cref="AutoAdvanceTime"/> is enabled and a <see cref="FakeTimeProvider"/> is configured,
/// the scheduler will automatically advance time when idle (no tasks queued). This allows
/// timer-based operations (like timeouts) to fire without manual time management.
/// </summary>
internal sealed class DeterministicTaskScheduler : TaskScheduler
{
    private readonly PriorityQueue<ScheduledTask, long> _taskQueue = new();
    private readonly Lock _lock = new();
    private long _sequenceNumber;
    private FakeTimeProvider? _timeProvider;

    /// <summary>
    /// Creates a new deterministic task scheduler.
    /// </summary>
    /// <param name="timeProvider">Optional time provider for time-based ordering and auto-advance.</param>
    /// <param name="autoAdvanceTime">Whether to automatically advance time when idle. Default is false.</param>
    public DeterministicTaskScheduler(FakeTimeProvider? timeProvider = null, bool autoAdvanceTime = false)
    {
        _timeProvider = timeProvider;
        AutoAdvanceTime = autoAdvanceTime;
    }

    /// <summary>
    /// Gets or sets whether to automatically advance time when the scheduler is idle.
    /// When enabled and a FakeTimeProvider is configured, time will be advanced by
    /// <see cref="AutoAdvanceStep"/> whenever there are no pending tasks.
    /// </summary>
    public bool AutoAdvanceTime { get; set; }

    /// <summary>
    /// Gets or sets the amount of time to advance when auto-advancing.
    /// Default is 100 milliseconds.
    /// </summary>
    public TimeSpan AutoAdvanceStep { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Sets the time provider for time-based ordering and auto-advance.
    /// Call this after the harness is created if the time provider wasn't available at construction.
    /// </summary>
    public void SetTimeProvider(FakeTimeProvider timeProvider)
    {
        lock (_lock)
        {
            _timeProvider = timeProvider;
        }
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

    /// <summary>
    /// Executes all pending tasks, automatically advancing time when idle until the task completes.
    /// This is the primary method for running async operations in simulation tests.
    /// 
    /// The method works by:
    /// 1. Executing all currently queued tasks
    /// 2. When idle (no tasks), advancing fake time to trigger timers
    /// 3. Repeating until the provided task completes or timeout is reached
    /// </summary>
    /// <typeparam name="T">The result type of the task.</typeparam>
    /// <param name="task">The task to wait for.</param>
    /// <param name="timeout">Maximum real time to wait. Default is 30 seconds.</param>
    /// <returns>The result of the task.</returns>
    /// <exception cref="TimeoutException">Thrown if the task doesn't complete within the timeout.</exception>
    /// <exception cref="InvalidOperationException">Thrown if no FakeTimeProvider is configured.</exception>
    public async Task<T> RunUntilAsync<T>(Task<T> task, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        
        if (_timeProvider == null)
        {
            throw new InvalidOperationException("RunUntilAsync requires a FakeTimeProvider to be configured");
        }

        var actualTimeout = timeout ?? TimeSpan.FromSeconds(30);
        var deadline = DateTime.UtcNow + actualTimeout;
        var idleCount = 0;
        const int maxIdleAdvances = 1000; // Safety limit to prevent infinite loops

        while (!task.IsCompleted)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Task did not complete within {actualTimeout}");
            }

            // Execute all pending tasks
            var executed = StepAll();

            if (executed == 0)
            {
                // No tasks were executed - we're idle
                // Advance time to trigger any pending timers
                _timeProvider.Advance(AutoAdvanceStep);
                idleCount++;

                if (idleCount > maxIdleAdvances)
                {
                    throw new TimeoutException($"Task did not complete after {maxIdleAdvances} time advances " +
                        $"(total simulated time advanced: {TimeSpan.FromMilliseconds(maxIdleAdvances * AutoAdvanceStep.TotalMilliseconds)})");
                }

                // Small real delay to prevent tight spin and allow other threads to queue work
                await Task.Delay(1).ConfigureAwait(false);
            }
            else
            {
                // Reset idle count when we execute tasks
                idleCount = 0;
            }
        }

        return await task.ConfigureAwait(false);
    }

    /// <summary>
    /// Executes all pending tasks, automatically advancing time when idle until the task completes.
    /// </summary>
    /// <param name="task">The task to wait for.</param>
    /// <param name="timeout">Maximum real time to wait. Default is 30 seconds.</param>
    /// <exception cref="TimeoutException">Thrown if the task doesn't complete within the timeout.</exception>
    /// <exception cref="InvalidOperationException">Thrown if no FakeTimeProvider is configured.</exception>
    public async Task RunUntilAsync(Task task, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        await RunUntilAsync(Task.Run(async () => { await task.ConfigureAwait(false); return 0; }), timeout).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes all pending tasks, automatically advancing time when idle until the condition is met.
    /// </summary>
    /// <param name="condition">The condition to wait for.</param>
    /// <param name="timeout">Maximum real time to wait. Default is 30 seconds.</param>
    /// <exception cref="TimeoutException">Thrown if the condition isn't met within the timeout.</exception>
    /// <exception cref="InvalidOperationException">Thrown if no FakeTimeProvider is configured.</exception>
    public async Task RunUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(condition);
        
        if (_timeProvider == null)
        {
            throw new InvalidOperationException("RunUntilAsync requires a FakeTimeProvider to be configured");
        }

        var actualTimeout = timeout ?? TimeSpan.FromSeconds(30);
        var deadline = DateTime.UtcNow + actualTimeout;
        var idleCount = 0;
        const int maxIdleAdvances = 1000;

        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Condition was not met within {actualTimeout}");
            }

            var executed = StepAll();

            if (executed == 0)
            {
                _timeProvider.Advance(AutoAdvanceStep);
                idleCount++;

                if (idleCount > maxIdleAdvances)
                {
                    throw new TimeoutException($"Condition was not met after {maxIdleAdvances} time advances");
                }

                await Task.Delay(1).ConfigureAwait(false);
            }
            else
            {
                idleCount = 0;
            }
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
