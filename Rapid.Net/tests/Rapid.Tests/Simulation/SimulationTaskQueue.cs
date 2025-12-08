namespace Rapid.Tests.Simulation;

/// <summary>
/// Base class for all scheduled items in the queue.
/// </summary>
internal abstract class ScheduledItem(
    Action callback,
    TimeSpan dueTime,
    long sequenceNumber)
{
    /// <summary>
    /// The action to execute when this item is due.
    /// </summary>
    public Action Callback { get; } = callback;

    /// <summary>
    /// The time offset from start when this item is due.
    /// </summary>
    public TimeSpan DueTime { get; } = dueTime;

    /// <summary>
    /// The sequence number for ordering items with the same due time.
    /// </summary>
    public long SequenceNumber { get; } = sequenceNumber;
}

/// <summary>
/// A scheduled item representing a Task from the TaskScheduler.
/// </summary>
internal sealed class ScheduledTaskItem(
    Action callback,
    TimeSpan dueTime,
    long sequenceNumber,
    Task task) : ScheduledItem(callback, dueTime, sequenceNumber)
{
    /// <summary>
    /// The Task object being scheduled.
    /// </summary>
    public Task Task { get; } = task;
}

/// <summary>
/// A scheduled item representing a SynchronizationContext callback.
/// </summary>
internal sealed class ScheduledSyncContextItem(
    Action callback,
    TimeSpan dueTime,
    long sequenceNumber) : ScheduledItem(callback, dueTime, sequenceNumber);

/// <summary>
/// A scheduled item representing a timer callback.
/// Implements IDisposable for efficient timer cancellation.
/// </summary>
internal sealed class ScheduledTimerItem : ScheduledItem, IDisposable
{
    private readonly SimulationTaskQueue _queue;

    /// <summary>
    /// Gets the period for recurring timers (Zero for one-shot).
    /// </summary>
    public TimeSpan Period { get; }

    /// <summary>
    /// Gets whether this timer has been cancelled.
    /// </summary>
    public bool IsCancelled { get; private set; }

    public ScheduledTimerItem(
        Action callback,
        TimeSpan dueTime,
        long sequenceNumber,
        TimeSpan period,
        SimulationTaskQueue queue)
        : base(callback, dueTime, sequenceNumber)
    {
        Period = period;
        _queue = queue;
    }

    /// <summary>
    /// Cancels the timer by removing it from the queue.
    /// </summary>
    public void Dispose()
    {
        if (IsCancelled)
            return;

        IsCancelled = true;
        _queue.RemoveTimer(this);
    }
}

/// <summary>
/// A time-aware task queue that serves as the common core for both
/// <see cref="TaskScheduler"/> and <see cref="SimulationTimeProvider"/>.
/// 
/// Items are stored in a single queue ordered by due time, then sequence number.
/// Items with DueTime &lt;= CurrentTime are considered "ready" for execution.
/// This enables deterministic simulation testing by providing unified control
/// over task execution order and time advancement.
/// </summary>
internal sealed class SimulationTaskQueue
{
    // Single queue ordered by due time, then sequence number
    private readonly SortedSet<ScheduledItem> _queue = new(new ScheduledItemComparer());

    private readonly Lock _lock = new();
    private long _sequenceNumber;

    /// <summary>
    /// Creates a new simulation task queue.
    /// </summary>
    /// <param name="initialTime">The initial time offset. Default is <see cref="TimeSpan.Zero"/>.</param>
    public SimulationTaskQueue(TimeSpan initialTime = default)
    {
        CurrentTime = initialTime;
        SynchronizationContext = new SimulationSynchronizationContext(this);
    }

    /// <summary>
    /// Gets or sets the current time offset from the start.
    /// </summary>
    public TimeSpan CurrentTime { get; set; }

    /// <summary>
    /// Gets the synchronization context used to execute callbacks.
    /// </summary>
    public SimulationSynchronizationContext SynchronizationContext { get; }

    /// <summary>
    /// Gets whether there are any items in the queue.
    /// </summary>
    public bool HasItems
    {
        get
        {
            lock (_lock)
            {
                return _queue.Count > 0;
            }
        }
    }

    /// <summary>
    /// Gets the due time of the next waiting (not yet ready) task, or null if no waiting tasks exist.
    /// </summary>
    public TimeSpan? NextWaitingDueTime
    {
        get
        {
            lock (_lock)
            {
                foreach (var item in _queue)
                {
                    if (item.DueTime > CurrentTime)
                        return item.DueTime;
                }
                return null;
            }
        }
    }

    /// <summary>
    /// Gets the number of timers waiting in the queue (not yet due).
    /// </summary>
    public int WaitingTimerCount
    {
        get
        {
            lock (_lock)
            {
                var count = 0;
                foreach (var item in _queue)
                {
                    if (item.DueTime > CurrentTime && item is ScheduledTimerItem)
                    {
                        count++;
                    }
                }
                return count;
            }
        }
    }

    /// <summary>
    /// Enqueues a Task to be executed immediately (at current time).
    /// The task object is stored for debugger introspection via GetScheduledTasks.
    /// </summary>
    /// <param name="task">The task to schedule.</param>
    /// <param name="executeTask">The action that executes the task.</param>
    public void EnqueueTask(Task task, Action executeTask)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(executeTask);

        lock (_lock)
        {
            var seq = _sequenceNumber++;
            var item = new ScheduledTaskItem(executeTask, CurrentTime, seq, task);
            _queue.Add(item);
        }
    }

    /// <summary>
    /// Enqueues an action to be executed after a delay from the current time.
    /// </summary>
    /// <param name="action">The action to execute.</param>
    /// <param name="delay">The delay from the current time.</param>
    public void EnqueueAfter(Action action, TimeSpan delay)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);

        lock (_lock)
        {
            var dueTime = CurrentTime + delay;
            var seq = _sequenceNumber++;
            var item = new ScheduledTaskItem(action, dueTime, seq, task: null!);
            _queue.Add(item);
        }
    }

    /// <summary>
    /// Enqueues a SynchronizationContext callback to be executed immediately (at current time).
    /// </summary>
    /// <param name="callback">The callback to execute.</param>
    internal void EnqueueSyncContextCallback(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        lock (_lock)
        {
            var seq = _sequenceNumber++;
            var item = new ScheduledSyncContextItem(callback, CurrentTime, seq);
            _queue.Add(item);
        }
    }

    /// <summary>
    /// Schedules a timer callback to be executed at a specific time.
    /// Returns an IDisposable that can be used to cancel the timer.
    /// </summary>
    /// <param name="callback">The callback to execute.</param>
    /// <param name="dueTime">The time offset when the callback should be executed.</param>
    /// <param name="period">The period for recurring timers (<see cref="TimeSpan.Zero"/> for one-shot).</param>
    /// <returns>A <see cref="ScheduledTimerItem"/> that can be disposed to cancel the timer.</returns>
    public IDisposable ScheduleTimer(Action callback, TimeSpan dueTime, TimeSpan period = default)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ArgumentOutOfRangeException.ThrowIfLessThan(period, TimeSpan.Zero);

        lock (_lock)
        {
            var seq = _sequenceNumber++;
            var item = new ScheduledTimerItem(callback, dueTime, seq, period, this);
            _queue.Add(item);
            return item;
        }
    }

    /// <summary>
    /// Removes a timer from the queue. Called by ScheduledTimerItem.Dispose().
    /// </summary>
    /// <param name="timer">The timer to remove.</param>
    internal void RemoveTimer(ScheduledTimerItem timer)
    {
        lock (_lock)
        {
            _queue.Remove(timer);
        }
    }

    /// <summary>
    /// Gets all scheduled Task objects from the queue.
    /// Used by SimulationTaskScheduler for debugger support (GetScheduledTasks).
    /// </summary>
    /// <returns>An enumerable of all scheduled tasks.</returns>
    public IEnumerable<Task> GetScheduledTasks()
    {
        lock (_lock)
        {
            var tasks = new List<Task>();

            foreach (var item in _queue)
            {
                if (item is ScheduledTaskItem { Task: not null } taskItem)
                {
                    tasks.Add(taskItem.Task);
                }
            }

            return tasks;
        }
    }

    /// <summary>
    /// Gets the count of scheduled Task objects that are ready (due time &lt;= current time).
    /// </summary>
    public int ScheduledTaskCount
    {
        get
        {
            lock (_lock)
            {
                var count = 0;
                foreach (var item in _queue)
                {
                    if (item.DueTime > CurrentTime)
                        break; // Queue is sorted by due time, no more ready items
                    if (item is ScheduledTaskItem)
                    {
                        count++;
                    }
                }
                return count;
            }
        }
    }

    /// <summary>
    /// Tries to dequeue and execute the next ready item.
    /// </summary>
    /// <returns>True if an item was dequeued and executed, false if no items are ready.</returns>
    public bool TryExecuteNext()
    {
        ScheduledItem item;

        lock (_lock)
        {
            if (_queue.Count == 0)
                return false;

            item = _queue.Min!;
            if (item.DueTime > CurrentTime)
                return false; // No ready items

            _queue.Remove(item);

            // If this was a periodic timer, reschedule it
            if (item is ScheduledTimerItem { Period: var period } timerItem && period > TimeSpan.Zero)
            {
                var nextDueTime = CurrentTime + period;
                var seq = _sequenceNumber++;
                var newItem = new ScheduledTimerItem(timerItem.Callback, nextDueTime, seq, period, this);
                _queue.Add(newItem);
            }
        }

        using (SynchronizationContext.Install())
        {
            item.Callback();
        }
        return true;
    }

    /// <summary>
    /// Executes all ready items in the queue.
    /// Note: Items added during execution are also executed (use ExecuteAllCurrently for bounded execution).
    /// </summary>
    /// <returns>The number of items executed.</returns>
    public int ExecuteAll()
    {
        var count = 0;
        while (TryExecuteNext())
        {
            count++;
        }
        return count;
    }

    /// <summary>
    /// Executes all currently ready items, but not items added during execution.
    /// This prevents infinite loops when callbacks enqueue more items.
    /// </summary>
    /// <returns>The number of items executed.</returns>
    public int ExecuteAllCurrently()
    {
        int readyCount;
        lock (_lock)
        {
            readyCount = 0;
            foreach (var item in _queue)
            {
                if (item.DueTime > CurrentTime)
                    break;
                readyCount++;
            }
        }

        var count = 0;
        for (var i = 0; i < readyCount; i++)
        {
            if (!TryExecuteNext())
            {
                break;
            }
            count++;
        }
        return count;
    }

    /// <summary>
    /// Advances the current time by the specified amount.
    /// </summary>
    /// <param name="delta">The amount to advance.</param>
    public void AdvanceTime(TimeSpan delta)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delta, TimeSpan.Zero);
        CurrentTime += delta;
    }

    /// <summary>
    /// Clears all items from the queue.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _queue.Clear();
        }
    }

    /// <summary>
    /// Gets a snapshot of all scheduled items (for debugging/testing).
    /// </summary>
    public (IReadOnlyList<ScheduledItem> Ready, IReadOnlyList<ScheduledItem> Waiting) GetSnapshot()
    {
        lock (_lock)
        {
            var ready = new List<ScheduledItem>();
            var waiting = new List<ScheduledItem>();

            foreach (var item in _queue)
            {
                if (item.DueTime <= CurrentTime)
                    ready.Add(item);
                else
                    waiting.Add(item);
            }

            return (ready, waiting);
        }
    }

    /// <summary>
    /// Comparer for ordering scheduled items by due time, then by sequence number.
    /// </summary>
    private sealed class ScheduledItemComparer : IComparer<ScheduledItem>
    {
        public int Compare(ScheduledItem? x, ScheduledItem? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            var dueTimeComparison = x.DueTime.CompareTo(y.DueTime);
            if (dueTimeComparison != 0)
                return dueTimeComparison;
            return x.SequenceNumber.CompareTo(y.SequenceNumber);
        }
    }
}
