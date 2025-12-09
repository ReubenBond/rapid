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
    /// The time offset from start when this item is due.
    /// Set internally by <see cref="SimulationTaskQueue"/> when the item is scheduled.
    /// </summary>
    public TimeSpan DueTime { get; private set; }

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
    /// <param name="dueTime">The time when this item is due.</param>
    /// <param name="sequenceNumber">The sequence number for ordering.</param>
    internal void OnScheduled(SimulationTaskQueue queue, TimeSpan dueTime, long sequenceNumber)
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
/// Items with DueTime &lt;= CurrentTime are considered "ready" for execution.
/// This enables deterministic simulation testing by providing unified control
/// over task execution order and time advancement.
/// </summary>
internal sealed class SimulationTaskQueue
{
    // Single queue ordered by due time, then sequence number
    private readonly SortedSet<ScheduledItem> _queue = new(new ScheduledItemComparer());

    private long _sequenceNumber;

    /// <summary>
    /// Gets the scheduled items in the queue, ordered by due time then sequence number.
    /// This is a read-only view that cannot be modified.
    /// </summary>
    public IReadOnlySet<ScheduledItem> ScheduledItems { get; }

    /// <summary>
    /// Creates a new simulation task queue.
    /// </summary>
    /// <param name="initialTime">The initial time offset. Default is <see cref="TimeSpan.Zero"/>.</param>
    public SimulationTaskQueue(TimeSpan initialTime = default)
    {
        CurrentTime = initialTime;
        SynchronizationContext = new SimulationSynchronizationContext(this);
        ScheduledItems = _queue.AsReadOnly();
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
    public bool HasItems => _queue.Count > 0;

    /// <summary>
    /// Gets the due time of the next waiting (not yet ready) task, or null if no waiting tasks exist.
    /// </summary>
    public TimeSpan? NextWaitingDueTime
    {
        get
        {
            foreach (var item in _queue)
            {
                if (item.DueTime > CurrentTime)
                    return item.DueTime;
            }
            return null;
        }
    }
    /// <summary>
    /// Enqueues a scheduled item to be executed immediately (at current time).
    /// The item's DueTime, SequenceNumber, and queue reference are set by this method.
    /// </summary>
    /// <param name="item">The scheduled item to enqueue.</param>
    public void Enqueue(ScheduledItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        ScheduleCore(item, CurrentTime);
    }

    /// <summary>
    /// Enqueues an action to be executed after a delay from the current time.
    /// Convenience method that creates a <see cref="ScheduledActionItem"/>.
    /// </summary>
    /// <param name="action">The action to execute.</param>
    /// <param name="delay">The delay from the current time.</param>
    public void EnqueueAfter(Action action, TimeSpan delay)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);
        ScheduleCore(new ScheduledActionItem(action), CurrentTime + delay);
    }

    /// <summary>
    /// Enqueues an item to be executed after a delay from the current time.
    /// </summary>
    /// <param name="item">The item to execute.</param>
    /// <param name="delay">The delay from the current time.</param>
    public TItem EnqueueAfter<TItem>(TItem item, TimeSpan delay) where TItem : ScheduledItem
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);
        ScheduleCore(item, CurrentTime + delay);
        return item;
    }

    /// <summary>
    /// Schedules an item to be executed at a specific time.
    /// The item's DueTime, SequenceNumber, and queue reference are set by this method.
    /// Returns the scheduled item which can be disposed to cancel it.
    /// </summary>
    /// <param name="item">The scheduled item to schedule.</param>
    /// <param name="dueTime">The time offset when the item should be executed.</param>
    /// <returns>The scheduled item that can be disposed to cancel it.</returns>
    private void ScheduleCore(ScheduledItem item, TimeSpan dueTime)
    {
        ArgumentNullException.ThrowIfNull(item);
        item.OnScheduled(this, dueTime, _sequenceNumber++);
        _queue.Add(item);
    }

    /// <summary>
    /// Removes an item from the queue. Called by ScheduledItem.Dispose().
    /// </summary>
    /// <param name="item">The item to remove.</param>
    internal void RemoveItem(ScheduledItem item) => _queue.Remove(item);

    /// <summary>
    /// Tries to dequeue and execute the next ready item.
    /// </summary>
    /// <returns>True if an item was dequeued and executed, false if no items are ready.</returns>
    public bool RunOnce()
    {
        if (_queue.Count == 0)
            return false;

        var item = _queue.Min!;
        if (item.DueTime > CurrentTime)
            return false; // No ready items

        _queue.Remove(item);

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
    public void Clear() => _queue.Clear();

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
