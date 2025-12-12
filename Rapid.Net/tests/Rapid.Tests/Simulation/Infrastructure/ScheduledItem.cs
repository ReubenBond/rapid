namespace Rapid.Tests.Simulation.Infrastructure;

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
