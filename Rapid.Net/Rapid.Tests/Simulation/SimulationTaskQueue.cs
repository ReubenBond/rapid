namespace Rapid.Tests.Simulation;

/// <summary>
/// A time-aware task queue that serves as the common core for both
/// <see cref="SimulationTaskScheduler"/> and <see cref="SimulationTimeProvider"/>.
/// 
/// Uses separate data structures for ready and waiting tasks:
/// - Ready queue: Tasks that are due now (FIFO ordered by sequence number)
/// - Waiting queue: Tasks scheduled for a future time (ordered by due time, then sequence)
/// 
/// When time advances, tasks are moved from waiting to ready as they become due.
/// This enables deterministic simulation testing by providing unified control
/// over task execution order and time advancement.
/// </summary>
internal sealed class SimulationTaskQueue
{
    // Ready tasks: ordered by sequence number only (FIFO)
    private readonly SortedList<long, ScheduledItem> _readyQueue = new();
    
    // Waiting tasks: ordered by due time, then sequence number
    private readonly SortedList<long, ScheduledItem> _waitingQueue = new();
    
    private readonly Lock _lock = new();
    private long _sequenceNumber;
    private long _currentTimeTicks;

    /// <summary>
    /// Creates a new simulation task queue.
    /// </summary>
    /// <param name="initialTimeTicks">The initial time in ticks. Default is 0.</param>
    public SimulationTaskQueue(long initialTimeTicks = 0)
    {
        _currentTimeTicks = initialTimeTicks;
    }

    /// <summary>
    /// Gets or sets the current time in ticks.
    /// Setting this value will move any waiting tasks that are now due to the ready queue.
    /// </summary>
    public long CurrentTimeTicks
    {
        get
        {
            lock (_lock)
            {
                return _currentTimeTicks;
            }
        }
        set
        {
            lock (_lock)
            {
                _currentTimeTicks = value;
                MoveWaitingToReady();
            }
        }
    }

    /// <summary>
    /// Gets the total number of items in both queues.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _readyQueue.Count + _waitingQueue.Count;
            }
        }
    }

    /// <summary>
    /// Gets the number of ready tasks.
    /// </summary>
    public int ReadyCount
    {
        get
        {
            lock (_lock)
            {
                return _readyQueue.Count;
            }
        }
    }

    /// <summary>
    /// Gets the number of waiting tasks.
    /// </summary>
    public int WaitingCount
    {
        get
        {
            lock (_lock)
            {
                return _waitingQueue.Count;
            }
        }
    }

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
    /// Gets whether there are any ready tasks to execute.
    /// </summary>
    public bool HasReadyItems
    {
        get
        {
            lock (_lock)
            {
                return _readyQueue.Count > 0;
            }
        }
    }

    /// <summary>
    /// Gets whether there are any waiting tasks.
    /// </summary>
    public bool HasWaitingItems
    {
        get
        {
            lock (_lock)
            {
                return _waitingQueue.Count > 0;
            }
        }
    }

    /// <summary>
    /// Gets the due time of the next waiting task, or null if no waiting tasks exist.
    /// </summary>
    public long? NextWaitingDueTimeTicks
    {
        get
        {
            lock (_lock)
            {
                if (_waitingQueue.Count == 0)
                    return null;

                return _waitingQueue.Values[0].DueTimeTicks;
            }
        }
    }

    /// <summary>
    /// Enqueues an action to be executed immediately (added to ready queue).
    /// </summary>
    /// <param name="action">The action to execute.</param>
    public void Enqueue(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        lock (_lock)
        {
            var seq = _sequenceNumber++;
            var item = new ScheduledItem(action, _currentTimeTicks, seq);
            _readyQueue.Add(seq, item);
        }
    }

    /// <summary>
    /// Enqueues an action to be executed at a specific time.
    /// If the time is now or in the past, it goes to the ready queue.
    /// Otherwise, it goes to the waiting queue.
    /// </summary>
    /// <param name="action">The action to execute.</param>
    /// <param name="dueTimeTicks">The time in ticks when the action should be executed.</param>
    public void EnqueueAt(Action action, long dueTimeTicks)
    {
        ArgumentNullException.ThrowIfNull(action);

        lock (_lock)
        {
            var seq = _sequenceNumber++;
            var item = new ScheduledItem(action, dueTimeTicks, seq);

            if (dueTimeTicks <= _currentTimeTicks)
            {
                // Due now or in the past - add to ready queue
                _readyQueue.Add(seq, item);
            }
            else
            {
                // Due in the future - add to waiting queue
                // Key combines due time and sequence for proper ordering
                var key = (dueTimeTicks << 20) | (seq & 0xFFFFF);
                _waitingQueue.Add(key, item);
            }
        }
    }

    /// <summary>
    /// Enqueues an action to be executed after a delay from the current time.
    /// </summary>
    /// <param name="action">The action to execute.</param>
    /// <param name="delayTicks">The delay in ticks from the current time.</param>
    public void EnqueueAfter(Action action, long delayTicks)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentOutOfRangeException.ThrowIfNegative(delayTicks);

        lock (_lock)
        {
            var dueTime = _currentTimeTicks + delayTicks;
            var seq = _sequenceNumber++;
            var item = new ScheduledItem(action, dueTime, seq);

            if (delayTicks == 0)
            {
                // No delay - add to ready queue
                _readyQueue.Add(seq, item);
            }
            else
            {
                // Has delay - add to waiting queue
                var key = (dueTime << 20) | (seq & 0xFFFFF);
                _waitingQueue.Add(key, item);
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
        }

        item.Action();
        return true;
    }

    /// <summary>
    /// Tries to dequeue the next ready item without executing it.
    /// </summary>
    /// <param name="action">The dequeued action, if any.</param>
    /// <returns>True if an item was dequeued, false if no items are ready.</returns>
    public bool TryDequeue(out Action? action)
    {
        lock (_lock)
        {
            if (_readyQueue.Count == 0)
            {
                action = null;
                return false;
            }

            var item = _readyQueue.Values[0];
            _readyQueue.RemoveAt(0);
            action = item.Action;
            return true;
        }
    }

    /// <summary>
    /// Advances the current time to the next waiting task's due time.
    /// This moves all tasks that become due to the ready queue.
    /// </summary>
    /// <returns>True if time was advanced, false if no waiting tasks exist.</returns>
    public bool AdvanceToNextDueTime()
    {
        lock (_lock)
        {
            if (_waitingQueue.Count == 0)
                return false;

            var nextDueTime = _waitingQueue.Values[0].DueTimeTicks;

            if (nextDueTime <= _currentTimeTicks)
            {
                // Already at or past this time, just move tasks
                MoveWaitingToReady();
                return _readyQueue.Count > 0;
            }

            _currentTimeTicks = nextDueTime;
            MoveWaitingToReady();
            return true;
        }
    }

    /// <summary>
    /// Advances the current time by the specified amount.
    /// This moves all tasks that become due to the ready queue.
    /// </summary>
    /// <param name="deltaTicks">The amount to advance in ticks.</param>
    public void AdvanceTime(long deltaTicks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(deltaTicks);

        lock (_lock)
        {
            _currentTimeTicks += deltaTicks;
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
        }
    }

    /// <summary>
    /// Gets a snapshot of all scheduled items (for debugging/testing).
    /// </summary>
    public (IReadOnlyList<(Action Action, long DueTimeTicks)> Ready, IReadOnlyList<(Action Action, long DueTimeTicks)> Waiting) GetSnapshot()
    {
        lock (_lock)
        {
            var ready = _readyQueue.Values
                .Select(item => (item.Action, item.DueTimeTicks))
                .ToList();
            var waiting = _waitingQueue.Values
                .Select(item => (item.Action, item.DueTimeTicks))
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
            var item = _waitingQueue.Values[0];
            if (item.DueTimeTicks > _currentTimeTicks)
                break;

            _waitingQueue.RemoveAt(0);
            _readyQueue.Add(item.SequenceNumber, item);
        }
    }

    private readonly record struct ScheduledItem(Action Action, long DueTimeTicks, long SequenceNumber);
}
