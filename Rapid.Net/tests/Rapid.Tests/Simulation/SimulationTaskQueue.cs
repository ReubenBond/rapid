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
    /// Gets the number of waiting items of a specific type in the queue (not yet due).
    /// </summary>
    /// <typeparam name="T">The type of scheduled item to count.</typeparam>
    /// <returns>The count of waiting items of the specified type.</returns>
    public int GetWaitingCount<T>() where T : ScheduledItem
    {
        lock (_lock)
        {
            var count = 0;
            foreach (var item in _queue)
            {
                if (item.DueTime > CurrentTime && item is T)
                {
                    count++;
                }
            }
            return count;
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

        lock (_lock)
        {
            item.OnScheduled(this, CurrentTime, _sequenceNumber++);
            _queue.Add(item);
        }
    }

    /// <summary>
    /// Schedules an item to be executed at a specific time.
    /// The item's DueTime, SequenceNumber, and queue reference are set by this method.
    /// Returns the scheduled item which can be disposed to cancel it.
    /// </summary>
    /// <param name="item">The scheduled item to schedule.</param>
    /// <param name="dueTime">The time offset when the item should be executed.</param>
    /// <returns>The scheduled item that can be disposed to cancel it.</returns>
    public TItem Schedule<TItem>(TItem item, TimeSpan dueTime)
        where TItem : ScheduledItem
    {
        ArgumentNullException.ThrowIfNull(item);

        lock (_lock)
        {
            item.OnScheduled(this, dueTime, _sequenceNumber++);
            _queue.Add(item);
            return item;
        }
    }

    /// <summary>
    /// Removes an item from the queue. Called by ScheduledItem.Dispose().
    /// </summary>
    /// <param name="item">The item to remove.</param>
    internal void RemoveItem(ScheduledItem item)
    {
        lock (_lock)
        {
            _queue.Remove(item);
        }
    }

    /// <summary>
    /// Gets all items of a specific type from the queue, passing each to an extractor function.
    /// </summary>
    /// <typeparam name="TItem">The type of scheduled item to find.</typeparam>
    /// <typeparam name="TResult">The type of result to extract from each item.</typeparam>
    /// <param name="extractor">A function to extract the result from each matching item.</param>
    /// <returns>An enumerable of extracted results.</returns>
    public IEnumerable<TResult> GetItemsOfType<TItem, TResult>(Func<TItem, TResult?> extractor)
        where TItem : ScheduledItem
        where TResult : class
    {
        lock (_lock)
        {
            var results = new List<TResult>();

            foreach (var item in _queue)
            {
                if (item is TItem typedItem)
                {
                    var result = extractor(typedItem);
                    if (result is not null)
                    {
                        results.Add(result);
                    }
                }
            }

            return results;
        }
    }

    /// <summary>
    /// Gets the count of ready items of a specific type (due time &lt;= current time).
    /// </summary>
    /// <typeparam name="T">The type of scheduled item to count.</typeparam>
    /// <returns>The count of ready items of the specified type.</returns>
    public int GetReadyCount<T>() where T : ScheduledItem
    {
        lock (_lock)
        {
            var count = 0;
            foreach (var item in _queue)
            {
                if (item.DueTime > CurrentTime)
                    break; // Queue is sorted by due time, no more ready items
                if (item is T)
                {
                    count++;
                }
            }
            return count;
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
        }

        using (SynchronizationContext.Install())
        {
            item.Invoke();
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

            return x.CompareTo(y);
        }
    }
}
