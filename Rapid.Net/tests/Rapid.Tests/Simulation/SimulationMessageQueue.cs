using System.Diagnostics.CodeAnalysis;

namespace Rapid.Tests.Simulation;

/// <summary>
/// A simulation message queue that delivers messages in a consistent order
/// based on simulated delivery time and tie-breaking criteria.
/// </summary>
[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Infrastructure class for deterministic simulation")]
internal sealed class SimulationMessageQueue
{
    private readonly PriorityQueue<PendingMessage, MessagePriority> _queue = new();
    private readonly Lock _lock = new();
    private long _sequenceNumber;

    /// <summary>
    /// Gets the number of pending messages.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _queue.Count;
            }
        }
    }

    /// <summary>
    /// Gets whether there are any pending messages.
    /// </summary>
    public bool HasPendingMessages
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
    /// Enqueues a message for delivery at the specified time.
    /// </summary>
    /// <param name="message">The message to deliver.</param>
    /// <param name="deliveryTime">The simulated delivery time.</param>
    /// <param name="sourceAddress">The source node address.</param>
    /// <param name="targetAddress">The target node address.</param>
    public void Enqueue(
        PendingMessage message,
        DateTimeOffset deliveryTime,
        string sourceAddress,
        string targetAddress)
    {
        lock (_lock)
        {
            var priority = new MessagePriority(
                deliveryTime.Ticks,
                GetAddressHash(sourceAddress),
                GetAddressHash(targetAddress),
                _sequenceNumber++);

            _queue.Enqueue(message, priority);
        }
    }

    /// <summary>
    /// Tries to dequeue the next message that should be delivered.
    /// </summary>
    /// <param name="currentTime">The current simulated time.</param>
    /// <param name="message">The dequeued message if successful.</param>
    /// <returns>True if a message was dequeued, false otherwise.</returns>
    public bool TryDequeue(DateTimeOffset currentTime, out PendingMessage message)
    {
        lock (_lock)
        {
            if (_queue.TryPeek(out var pending, out var priority))
            {
                if (priority.DeliveryTimeTicks <= currentTime.Ticks)
                {
                    _queue.Dequeue();
                    message = pending;
                    return true;
                }
            }

            message = default;
            return false;
        }
    }

    /// <summary>
    /// Dequeues all messages that should be delivered by the specified time.
    /// </summary>
    /// <param name="currentTime">The current simulated time.</param>
    /// <returns>The list of messages to deliver, in delivery order.</returns>
    public List<PendingMessage> DequeueUntil(DateTimeOffset currentTime)
    {
        var result = new List<PendingMessage>();

        lock (_lock)
        {
            while (_queue.TryPeek(out var pending, out var priority))
            {
                if (priority.DeliveryTimeTicks <= currentTime.Ticks)
                {
                    _queue.Dequeue();
                    result.Add(pending);
                }
                else
                {
                    break;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Gets the delivery time of the next pending message, if any.
    /// </summary>
    public DateTimeOffset? PeekNextDeliveryTime()
    {
        lock (_lock)
        {
            if (_queue.TryPeek(out _, out var priority))
            {
                return new DateTimeOffset(priority.DeliveryTimeTicks, TimeSpan.Zero);
            }
            return null;
        }
    }

    /// <summary>
    /// Clears all pending messages.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _queue.Clear();
            _sequenceNumber = 0;
        }
    }

    private static long GetAddressHash(string address)
    {
        // Use a deterministic hash for consistent ordering
        var hash = 0L;
        foreach (var c in address)
        {
            hash = (hash * 31) + c;
        }
        return hash;
    }

    /// <summary>
    /// Priority for message ordering.
    /// Messages are ordered by: delivery time, source address, target address, sequence number.
    /// </summary>
    private readonly record struct MessagePriority(
        long DeliveryTimeTicks,
        long SourceAddressHash,
        long TargetAddressHash,
        long SequenceNumber) : IComparable<MessagePriority>
    {
        public int CompareTo(MessagePriority other)
        {
            var cmp = DeliveryTimeTicks.CompareTo(other.DeliveryTimeTicks);
            if (cmp != 0) return cmp;

            cmp = SourceAddressHash.CompareTo(other.SourceAddressHash);
            if (cmp != 0) return cmp;

            cmp = TargetAddressHash.CompareTo(other.TargetAddressHash);
            if (cmp != 0) return cmp;

            return SequenceNumber.CompareTo(other.SequenceNumber);
        }
    }
}

/// <summary>
/// Represents a message pending delivery.
/// </summary>
internal readonly record struct PendingMessage(
    string SourceAddress,
    string TargetAddress,
    Rapid.Pb.RapidRequest Request,
    TaskCompletionSource<Rapid.Pb.RapidResponse> ResponseSource);
