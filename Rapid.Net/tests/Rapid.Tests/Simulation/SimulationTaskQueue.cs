namespace Rapid.Tests.Simulation;

/// <summary>
/// Base class for all scheduled items in the queue.
/// Implements IDisposable for cancellation support.
/// </summary>
internal abstract class ScheduledItem : IDisposable, IComparable<ScheduledItem>
{
    private SimulationTaskQueue? _queue;
    private bool _disposed;

    /// <summary>
    /// The absolute time when this item is due.
    /// Set internally by <see cref="SimulationTaskQueue"/> when the item is scheduled.
    /// </summary>
    public DateTimeOffset DueTime { get; private set; }

    /// <summary>
    /// The sequence number for ordering items with the same due time.
    /// Set internally by <see cref="SimulationTaskQueue"/> when the item is scheduled.
    /// </summary>
    public long SequenceNumber { get; private set; }

    /// <summary>
    /// Called by <see cref="SimulationTaskQueue"/> when the item is added to the queue.
    /// Sets the queue reference, due time, and sequence number.
    /// </summary>
    /// <param name="queue">The queue this item belongs to.</param>
    /// <param name="dueTime">The absolute time when this item is due.</param>
    /// <param name="sequenceNumber">The sequence number for ordering.</param>
    internal void OnScheduled(SimulationTaskQueue queue, DateTimeOffset dueTime, long sequenceNumber)
    {
        if (_queue is not null)
        {
            throw new InvalidOperationException("Item has already been scheduled.");
        }

        _queue = queue;
        DueTime = dueTime;
        SequenceNumber = sequenceNumber;
    }

    /// <summary>
    /// Executes the scheduled item's action.
    /// </summary>
    protected internal abstract void Invoke();

    /// <summary>
    /// Cancels the item by removing it from the queue.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _queue?.RemoveItem(this);
    }

    /// <summary>
    /// Compares this item to another by due time, then by sequence number.
    /// </summary>
    public int CompareTo(ScheduledItem? other)
    {
        if (other is null) return 1;
        if (ReferenceEquals(this, other)) return 0;

        var dueTimeComparison = DueTime.CompareTo(other.DueTime);
        if (dueTimeComparison != 0)
            return dueTimeComparison;
        return SequenceNumber.CompareTo(other.SequenceNumber);
    }
}

/// <summary>
/// A simple scheduled item that wraps an Action callback.
/// Used for general-purpose delayed execution.
/// </summary>
internal sealed class ScheduledActionItem(Action callback) : ScheduledItem
{
    /// <inheritdoc />
    protected internal override void Invoke() => callback();
}

/// <summary>
/// A time-aware task queue that serves as the common core for both
/// <see cref="TaskScheduler"/> and <see cref="SimulationTimeProvider"/>.
/// 
/// Items are stored in a single queue ordered by due time, then sequence number.
/// Items with DueTime &lt;= UtcNow are considered "ready" for execution.
/// This enables deterministic simulation testing by providing unified control
/// over task execution order and time advancement.
/// 
/// The queue delegates time to a shared <see cref="SimulationClock"/>,
/// enabling multiple queues to share a unified view of time.
/// </summary>
internal sealed class SimulationTaskQueue
{
    // Single queue ordered by due time, then sequence number
    private readonly SortedSet<ScheduledItem> _queue = new(new ScheduledItemComparer());
    private readonly SimulationClock _clock;

    // Real lock for all queue operations since some can be called cross-thread
    // (e.g., Enqueue called from SimulationSynchronizationContext.Post on thread pool threads
    // due to CancellationToken callbacks or other async work escaping the simulation).
    private readonly Lock _queueLock = new();
    private long _sequenceNumber;

    /// <summary>
    /// Gets the scheduled items in the queue, ordered by due time then sequence number.
    /// This is a read-only view that cannot be modified.
    /// </summary>
    public IReadOnlySet<ScheduledItem> ScheduledItems { get; }

    /// <summary>
    /// Creates a new simulation task queue that uses the specified clock for time.
    /// Multiple queues can share the same clock for unified time coordination.
    /// </summary>
    /// <param name="clock">The clock to use for time.</param>
    public SimulationTaskQueue(SimulationClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
        SynchronizationContext = new SimulationSynchronizationContext(this);
        ScheduledItems = _queue.AsReadOnly();
    }

    /// <summary>
    /// Gets the current simulated date/time.
    /// </summary>
    public DateTimeOffset UtcNow => _clock.UtcNow;

    /// <summary>
    /// Gets the synchronization context used to execute callbacks.
    /// </summary>
    public SimulationSynchronizationContext SynchronizationContext { get; }

    /// <summary>
    /// Gets whether there are any items in the queue.
    /// This is called from the simulation thread only.
    /// </summary>
    public bool HasItems
    {
        get
        {
            lock (_queueLock)
            {
                return _queue.Count > 0;
            }
        }
    }

    /// <summary>
    /// Gets the due time of the next waiting (not yet ready) task, or null if no waiting tasks exist.
    /// This is called from the simulation thread only.
    /// </summary>
    public DateTimeOffset? NextWaitingDueTime
    {
        get
        {
            lock (_queueLock)
            {
                foreach (var item in _queue)
                {
                    if (item.DueTime > UtcNow)
                        return item.DueTime;
                }
                return null;
            }
        }
    }
    /// <summary>
    /// Enqueues a scheduled item to be executed immediately (at current time).
    /// The item's DueTime, SequenceNumber, and queue reference are set by this method.
    /// This method is thread-safe and can be called from any thread (e.g., from SynchronizationContext.Post).
    /// </summary>
    /// <param name="item">The scheduled item to enqueue.</param>
    public void Enqueue(ScheduledItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (_queueLock)
        {
            ScheduleCore(item, UtcNow);
        }
    }

    /// <summary>
    /// Enqueues an action to be executed after a delay from the current time.
    /// Convenience method that creates a <see cref="ScheduledActionItem"/>.
    /// This is called from the simulation thread only.
    /// </summary>
    /// <param name="action">The action to execute.</param>
    /// <param name="delay">The delay from the current time.</param>
    public void EnqueueAfter(Action action, TimeSpan delay)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);
        lock (_queueLock)
        {
            ScheduleCore(new ScheduledActionItem(action), UtcNow + delay);
        }
    }

    /// <summary>
    /// Enqueues an item to be executed after a delay from the current time.
    /// This is called from the simulation thread only.
    /// </summary>
    /// <param name="item">The item to execute.</param>
    /// <param name="delay">The delay from the current time.</param>
    public TItem EnqueueAfter<TItem>(TItem item, TimeSpan delay) where TItem : ScheduledItem
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);
        lock (_queueLock)
        {
            ScheduleCore(item, UtcNow + delay);
        }
        return item;
    }

    /// <summary>
    /// Schedules an item to be executed at a specific absolute time.
    /// The item's DueTime, SequenceNumber, and queue reference are set by this method.
    /// Returns the scheduled item which can be disposed to cancel it.
    /// </summary>
    /// <param name="item">The scheduled item to schedule.</param>
    /// <param name="dueTime">The absolute time when the item should be executed.</param>
    /// <returns>The scheduled item that can be disposed to cancel it.</returns>
    private void ScheduleCore(ScheduledItem item, DateTimeOffset dueTime)
    {
        ArgumentNullException.ThrowIfNull(item);
        item.OnScheduled(this, dueTime, _sequenceNumber++);
        _queue.Add(item);
    }

    /// <summary>
    /// Removes an item from the queue. Called by ScheduledItem.Dispose().
    /// This method is thread-safe as it can be called from any thread.
    /// </summary>
    /// <param name="item">The item to remove.</param>
    internal void RemoveItem(ScheduledItem item)
    {
        lock (_queueLock)
        {
            _queue.Remove(item);
        }
    }

    /// <summary>
    /// Tries to dequeue and execute the next ready item.
    /// This is called from the simulation thread only.
    /// </summary>
    /// <returns>True if an item was dequeued and executed, false if no items are ready.</returns>
    public bool RunOnce()
    {
        ScheduledItem? item;
        lock (_queueLock)
        {
            if (_queue.Count == 0)
                return false;

            item = _queue.Min!;
            if (item.DueTime > UtcNow)
                return false; // No ready items

            _queue.Remove(item);
        }

        using (SynchronizationContext.Install())
        {
            item.Invoke();
        }

        return true;
    }

    /// <summary>
    /// Executes all ready items in the queue.
    /// </summary>
    /// <returns>The number of items executed.</returns>
    public int RunUntilIdle()
    {
        var count = 0;
        while (RunOnce())
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// Clears all items from the queue.
    /// This is called from the simulation thread only.
    /// </summary>
    public void Clear()
    {
        lock (_queueLock)
        {
            _queue.Clear();
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

            return x.CompareTo(y);
        }
    }
}
