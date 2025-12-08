namespace Rapid.Tests.Simulation;

/// <summary>
/// A deterministic task scheduler that queues tasks and executes them only when explicitly stepped.
/// This enables fully deterministic simulation testing by controlling task execution order.
/// 
/// Works in collaboration with <see cref="SimulationTimeProvider"/> through a shared
/// <see cref="SimulationTaskQueue"/>. Use <see cref="SimulationHarness"/> for high-level
/// simulation control including time advancement and running until conditions are met.
/// </summary>
internal sealed class SimulationTaskScheduler : TaskScheduler
{
    private readonly SimulationTaskQueue _taskQueue;
    private readonly Lock _taskLock = new();
    private readonly List<Task> _pendingTasks = [];

    /// <summary>
    /// Creates a new simulation task scheduler with its own task queue.
    /// </summary>
    public SimulationTaskScheduler()
    {
        _taskQueue = new SimulationTaskQueue();
    }

    /// <summary>
    /// Creates a new simulation task scheduler with a shared task queue.
    /// </summary>
    /// <param name="taskQueue">The shared task queue to use.</param>
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
    /// Gets the number of pending tasks in the queue.
    /// </summary>
    public int PendingCount
    {
        get
        {
            lock (_taskLock)
            {
                return _pendingTasks.Count;
            }
        }
    }

    /// <summary>
    /// Gets whether there are any pending tasks that are ready to execute.
    /// </summary>
    public bool HasPendingTasks
    {
        get
        {
            lock (_taskLock)
            {
                return _pendingTasks.Count > 0;
            }
        }
    }

    /// <summary>
    /// Gets whether the scheduler is idle (no ready tasks and no waiting tasks in the queue).
    /// </summary>
    public bool IsIdle => !_taskQueue.HasItems;

    /// <inheritdoc />
    protected override IEnumerable<Task>? GetScheduledTasks()
    {
        lock (_taskLock)
        {
            return [.. _pendingTasks];
        }
    }

    /// <inheritdoc />
    protected override void QueueTask(Task task)
    {
        lock (_taskLock)
        {
            _pendingTasks.Add(task);
        }

        _taskQueue.Enqueue(() => ExecuteQueuedTask(task));
    }

    /// <inheritdoc />
    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) =>
        // For deterministic testing, we don't execute inline
        // All tasks go through the queue
        false;

    /// <summary>
    /// Tries to dequeue and execute a single ready task.
    /// A task is ready if its due time has been reached.
    /// </summary>
    /// <returns>True if a task was executed, false if no tasks are ready.</returns>
    public bool TryExecuteOne()
    {
        return _taskQueue.TryExecuteNext();
    }

    /// <summary>
    /// Executes the specified number of ready tasks.
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
    /// Executes all ready tasks (tasks whose due time has been reached).
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
    /// Clears all pending tasks without executing them.
    /// </summary>
    public void Clear()
    {
        lock (_taskLock)
        {
            _pendingTasks.Clear();
        }
        _taskQueue.Clear();
    }

    private void ExecuteQueuedTask(Task task)
    {
        lock (_taskLock)
        {
            _pendingTasks.Remove(task);
        }

        TryExecuteTask(task);
    }
}
