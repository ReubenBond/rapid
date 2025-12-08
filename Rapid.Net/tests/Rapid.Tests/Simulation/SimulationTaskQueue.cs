namespace Rapid.Tests.Simulation;

/// <summary>
/// A time-aware task queue that serves as the common core for both
/// <see cref="TaskScheduler"/> and <see cref="SimulationTimeProvider"/>.
/// 
/// Uses separate data structures for ready and waiting tasks:
/// - Ready queue: Tasks that are due now (FIFO ordered by sequence number)
/// - Waiting queue: Tasks scheduled for a future time (ordered by due time, then sequence)
/// 
/// When time advances, tasks are moved from waiting to ready as they become due.
/// This enables deterministic simulation testing by providing unified control
/// over task execution order and time advancement.
/// </summary>
/// <remarks>
/// Creates a new simulation task queue.
/// </remarks>
/// <param name="initialTime">The initial time offset. Default is <see cref="TimeSpan.Zero"/>.</param>
internal sealed class SimulationTaskQueue(TimeSpan initialTime = default)
{
    // Ready tasks: ordered by sequence number only (FIFO)
    private readonly SortedList<long, ScheduledItem> _readyQueue = [];

    // Waiting tasks: ordered by due time, then sequence number using custom comparer
    private readonly SortedSet<ScheduledItem> _waitingQueue = new(new ScheduledItemComparer());

    // Timers that can be cancelled - maps timer ID to the scheduled item
    private readonly Dictionary<long, ScheduledTimerItem> _timerItemMap = [];

    private readonly Lock _lock = new();
    private long _sequenceNumber;
    private long _nextTimerId;

    /// <summary>
    /// Gets or sets the current time offset from the start.
    /// Setting this value will move any waiting tasks that are now due to the ready queue.
    /// </summary>
    public TimeSpan CurrentTime
    {
        get => field;
        set
        {
            lock (_lock)
            {
                field = value;
                MoveWaitingToReady();
            }
        }
    } = initialTime;

    /// <summary>
    /// Gets whether there are any items in either queue.
    /// </summary>
    public bool HasItems
    {
        get
        {
            lock (_lock)
            {
                return _readyQueue.Count > 0 || _waitingQueue.Count > 0;
            }
        }
    }

    /// <summary>
    /// Gets the due time of the next waiting task, or null if no waiting tasks exist.
    /// </summary>
    public TimeSpan? NextWaitingDueTime
    {
        get
        {
            lock (_lock)
            {
                if (_waitingQueue.Count == 0)
                    return null;

                return _waitingQueue.Min!.DueTime;
            }
        }
    }

    /// <summary>
    /// Gets the number of timers waiting in the waiting queue (not yet due).
    /// This excludes timers that have been moved to the ready queue.
    /// </summary>
    public int WaitingTimerCount
    {
        get
        {
            lock (_lock)
            {
                var count = 0;
                foreach (var item in _waitingQueue)
                {
                    if (item is ScheduledTimerItem)
                    {
                        count++;
                    }
                }
                return count;
            }
        }
    }

    /// <summary>
    /// Enqueues a Task to be executed immediately (added to ready queue).
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
            _readyQueue.Add(seq, item);
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
            var item = new ScheduledTaskItem(action, dueTime, seq, Task: null!);

            if (delay == TimeSpan.Zero)
            {
                // No delay - add to ready queue
                _readyQueue.Add(seq, item);
            }
            else
            {
                // Has delay - add to waiting queue
                _waitingQueue.Add(item);
            }
        }
    }

    /// <summary>
    /// Schedules a timer callback to be executed at a specific time.
    /// Returns a timer ID that can be used to cancel or modify the timer.
    /// </summary>
    /// <param name="callback">The callback to execute.</param>
    /// <param name="dueTime">The time offset when the callback should be executed.</param>
    /// <param name="period">The period for recurring timers (<see cref="TimeSpan.Zero"/> for one-shot).</param>
    /// <returns>A timer ID that can be used to cancel the timer.</returns>
    public long ScheduleTimer(Action callback, TimeSpan dueTime, TimeSpan period = default)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ArgumentOutOfRangeException.ThrowIfLessThan(period, TimeSpan.Zero);

        lock (_lock)
        {
            var timerId = _nextTimerId++;
            var seq = _sequenceNumber++;
            var item = new ScheduledTimerItem(callback, dueTime, seq, timerId, period);

            if (dueTime <= CurrentTime)
            {
                // Due now or in the past - add to ready queue
                _readyQueue.Add(seq, item);
            }
            else
            {
                // Due in the future - add to waiting queue
                _waitingQueue.Add(item);
            }

            _timerItemMap[timerId] = item;
            return timerId;
        }
    }

    /// <summary>
    /// Cancels a timer by its ID.
    /// </summary>
    /// <param name="timerId">The timer ID returned by ScheduleTimer.</param>
    /// <returns>True if the timer was found and cancelled, false if it was already executed or not found.</returns>
    public bool CancelTimer(long timerId)
    {
        lock (_lock)
        {
            if (!_timerItemMap.TryGetValue(timerId, out var item))
            {
                return false;
            }

            _timerItemMap.Remove(timerId);

            // Try to remove from waiting queue first
            if (_waitingQueue.Remove(item))
            {
                return true;
            }

            // Try to remove from ready queue
            if (_readyQueue.Remove(item.SequenceNumber))
            {
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Gets all scheduled Task objects from both queues.
    /// Used by SimulationTaskScheduler for debugger support (GetScheduledTasks).
    /// </summary>
    /// <returns>An enumerable of all scheduled tasks.</returns>
    public IEnumerable<Task> GetScheduledTasks()
    {
        lock (_lock)
        {
            var tasks = new List<Task>();

            foreach (var item in _readyQueue.Values)
            {
                if (item is ScheduledTaskItem { Task: not null } taskItem)
                {
                    tasks.Add(taskItem.Task);
                }
            }

            foreach (var item in _waitingQueue)
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
    /// Gets the count of scheduled Task objects in the ready queue.
    /// </summary>
    public int ScheduledTaskCount
    {
        get
        {
            lock (_lock)
            {
                var count = 0;
                foreach (var item in _readyQueue.Values)
                {
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
            if (_readyQueue.Count == 0)
                return false;

            item = _readyQueue.Values[0];
            _readyQueue.RemoveAt(0);

            // If this was a timer, handle rescheduling for periodic timers
            if (item is ScheduledTimerItem timerItem)
            {
                var timerId = timerItem.TimerId;

                if (timerItem.Period > TimeSpan.Zero)
                {
                    // Periodic timer - reschedule for next period
                    var nextDueTime = CurrentTime + timerItem.Period;
                    var seq = _sequenceNumber++;
                    var newItem = new ScheduledTimerItem(timerItem.Callback, nextDueTime, seq, timerId, timerItem.Period);
                    _waitingQueue.Add(newItem);
                    _timerItemMap[timerId] = newItem;
                }
                else
                {
                    // One-shot timer - remove from tracking
                    _timerItemMap.Remove(timerId);
                }
            }
        }

        item.Callback();
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
            readyCount = _readyQueue.Count;
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
    /// This moves all tasks that become due to the ready queue.
    /// </summary>
    /// <param name="delta">The amount to advance.</param>
    public void AdvanceTime(TimeSpan delta)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delta, TimeSpan.Zero);

        lock (_lock)
        {
            CurrentTime += delta;
            MoveWaitingToReady();
        }
    }

    /// <summary>
    /// Clears all items from both queues.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _readyQueue.Clear();
            _waitingQueue.Clear();
            _timerItemMap.Clear();
        }
    }

    /// <summary>
    /// Gets a snapshot of all scheduled items (for debugging/testing).
    /// </summary>
    public (IReadOnlyList<(Action Action, TimeSpan DueTime)> Ready, IReadOnlyList<(Action Action, TimeSpan DueTime)> Waiting) GetSnapshot()
    {
        lock (_lock)
        {
            var ready = _readyQueue.Values
                .Select(item => (item.Callback, item.DueTime))
                .ToList();
            var waiting = _waitingQueue
                .Select(item => (item.Callback, item.DueTime))
                .ToList();
            return (ready, waiting);
        }
    }

    /// <summary>
    /// Moves all waiting tasks that are now due to the ready queue.
    /// Must be called while holding the lock.
    /// </summary>
    private void MoveWaitingToReady()
    {
        while (_waitingQueue.Count > 0)
        {
            var item = _waitingQueue.Min!;
            if (item.DueTime > CurrentTime)
                break;

            _waitingQueue.Remove(item);
            
            // Assign a new sequence number to preserve due-time ordering in the ready queue.
            // This ensures items are executed in the order they became due, not creation order.
            var readySeq = _sequenceNumber++;
            ScheduledItem readyItem = item switch
            {
                ScheduledTaskItem taskItem => taskItem with { SequenceNumber = readySeq },
                ScheduledTimerItem timerItem => timerItem with { SequenceNumber = readySeq },
                _ => throw new InvalidOperationException($"Unknown scheduled item type: {item.GetType().Name}")
            };
            _readyQueue.Add(readySeq, readyItem);

            // Update timer tracking if this is a timer
            if (readyItem is ScheduledTimerItem updatedTimerItem)
            {
                _timerItemMap[updatedTimerItem.TimerId] = updatedTimerItem;
            }
        }
    }

    /// <summary>
    /// Base class for all scheduled items in the queue.
    /// </summary>
    /// <param name="Callback">The action to execute when this item is due.</param>
    /// <param name="DueTime">The time offset from start when this item is due.</param>
    /// <param name="SequenceNumber">The sequence number for ordering items with the same due time.</param>
    private abstract record ScheduledItem(
        Action Callback,
        TimeSpan DueTime,
        long SequenceNumber);

    /// <summary>
    /// A scheduled item representing a Task from the TaskScheduler.
    /// </summary>
    /// <param name="Callback">The action to execute when this item is due.</param>
    /// <param name="DueTime">The time offset from start when this item is due.</param>
    /// <param name="SequenceNumber">The sequence number for ordering items with the same due time.</param>
    /// <param name="Task">The Task object being scheduled.</param>
    private sealed record ScheduledTaskItem(
        Action Callback,
        TimeSpan DueTime,
        long SequenceNumber,
        Task Task) : ScheduledItem(Callback, DueTime, SequenceNumber);

    /// <summary>
    /// A scheduled item representing a timer callback.
    /// </summary>
    /// <param name="Callback">The action to execute when this item is due.</param>
    /// <param name="DueTime">The time offset from start when this item is due.</param>
    /// <param name="SequenceNumber">The sequence number for ordering items with the same due time.</param>
    /// <param name="TimerId">The unique identifier for this timer.</param>
    /// <param name="Period">The period for recurring timers (Zero for one-shot).</param>
    private sealed record ScheduledTimerItem(
        Action Callback,
        TimeSpan DueTime,
        long SequenceNumber,
        long TimerId,
        TimeSpan Period) : ScheduledItem(Callback, DueTime, SequenceNumber);

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
