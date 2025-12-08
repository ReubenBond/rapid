using System.Collections.Concurrent;

namespace Rapid.Tests.Simulation;

/// <summary>
/// A time provider for simulation testing that tracks all pending timer operations.
/// This allows precise control over time advancement and enables testing of race conditions.
/// 
/// Key features:
/// - Tracks all pending delays/timers with their target completion times
/// - Allows advancing to the next pending timer exactly
/// - Supports choosing specific timers to fire for race condition testing
/// - Provides visibility into all waiting operations
/// </summary>
internal sealed class SimulationTimeProvider : TimeProvider
{
    private readonly Lock _lock = new();
    private readonly SortedSet<PendingTimer> _pendingTimers = new(PendingTimerComparer.Instance);
    private readonly ConcurrentDictionary<long, PendingTimer> _timersById = new();
    private long _nextTimerId;
    private DateTimeOffset _utcNow;
    private long _timestampTicks;

    /// <summary>
    /// Creates a new simulation time provider starting at the specified time.
    /// </summary>
    /// <param name="startTime">The initial time. Defaults to current UTC time.</param>
    public SimulationTimeProvider(DateTimeOffset? startTime = null)
    {
        _utcNow = startTime ?? DateTimeOffset.UtcNow;
        _timestampTicks = 0;
    }

    /// <summary>
    /// Gets the current UTC time in the simulation.
    /// </summary>
    public override DateTimeOffset GetUtcNow() => _utcNow;

    /// <summary>
    /// Gets the timestamp frequency (ticks per second).
    /// </summary>
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <summary>
    /// Gets the current timestamp.
    /// </summary>
    public override long GetTimestamp() => _timestampTicks;

    /// <summary>
    /// Gets the number of pending timers.
    /// </summary>
    public int PendingTimerCount
    {
        get
        {
            lock (_lock)
            {
                return _pendingTimers.Count;
            }
        }
    }

    /// <summary>
    /// Gets whether there are any pending timers.
    /// </summary>
    public bool HasPendingTimers
    {
        get
        {
            lock (_lock)
            {
                return _pendingTimers.Count > 0;
            }
        }
    }

    /// <summary>
    /// Gets the time until the next pending timer fires, or null if no timers are pending.
    /// </summary>
    public TimeSpan? TimeUntilNextTimer
    {
        get
        {
            lock (_lock)
            {
                if (_pendingTimers.Count == 0)
                    return null;
                
                var next = _pendingTimers.Min!;
                var duration = next.DueTime - _utcNow;
                return duration > TimeSpan.Zero ? duration : TimeSpan.Zero;
            }
        }
    }

    /// <summary>
    /// Gets information about all pending timers, ordered by due time.
    /// </summary>
    public IReadOnlyList<TimerInfo> GetPendingTimers()
    {
        lock (_lock)
        {
            return _pendingTimers
                .Select(t => new TimerInfo(t.Id, t.DueTime, t.DueTime - _utcNow, t.Description))
                .ToList();
        }
    }

    /// <summary>
    /// Advances time by the specified duration, firing any timers that become due.
    /// </summary>
    /// <param name="duration">The duration to advance.</param>
    /// <returns>The number of timers that were fired.</returns>
    public int Advance(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration), "Duration cannot be negative");

        var targetTime = _utcNow + duration;
        return AdvanceTo(targetTime);
    }

    /// <summary>
    /// Advances time to the specified target time, firing any timers that become due.
    /// </summary>
    /// <param name="targetTime">The target time to advance to.</param>
    /// <returns>The number of timers that were fired.</returns>
    public int AdvanceTo(DateTimeOffset targetTime)
    {
        if (targetTime < _utcNow)
            throw new ArgumentOutOfRangeException(nameof(targetTime), "Cannot go back in time");

        var firedCount = 0;
        List<PendingTimer> timersToFire;

        lock (_lock)
        {
            // Collect all timers that should fire
            timersToFire = _pendingTimers
                .Where(t => t.DueTime <= targetTime)
                .ToList();

            foreach (var timer in timersToFire)
            {
                _pendingTimers.Remove(timer);
                _timersById.TryRemove(timer.Id, out _);
            }

            // Update time
            _utcNow = targetTime;
            _timestampTicks = targetTime.Ticks;
        }

        // Fire timers outside the lock to prevent deadlocks
        foreach (var timer in timersToFire.OrderBy(t => t.DueTime).ThenBy(t => t.Id))
        {
            timer.Fire();
            firedCount++;
        }

        return firedCount;
    }

    /// <summary>
    /// Advances time to fire exactly the next pending timer.
    /// </summary>
    /// <returns>True if a timer was fired, false if no timers were pending.</returns>
    public bool AdvanceToNextTimer()
    {
        PendingTimer? nextTimer;

        lock (_lock)
        {
            if (_pendingTimers.Count == 0)
                return false;

            nextTimer = _pendingTimers.Min;
        }

        if (nextTimer != null)
        {
            AdvanceTo(nextTimer.DueTime);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Advances time to fire all currently pending timers.
    /// </summary>
    /// <returns>The number of timers fired.</returns>
    public int AdvanceToAllPendingTimers()
    {
        var totalFired = 0;
        while (AdvanceToNextTimer())
        {
            totalFired++;
        }
        return totalFired;
    }

    /// <summary>
    /// Fires a specific timer by ID without advancing time.
    /// Useful for testing race conditions by choosing which operation completes first.
    /// </summary>
    /// <param name="timerId">The ID of the timer to fire.</param>
    /// <returns>True if the timer was found and fired, false otherwise.</returns>
    public bool FireTimer(long timerId)
    {
        PendingTimer? timer;

        lock (_lock)
        {
            if (!_timersById.TryRemove(timerId, out timer))
                return false;
            
            _pendingTimers.Remove(timer);
        }

        timer.Fire();
        return true;
    }

    /// <summary>
    /// Cancels a specific timer by ID.
    /// </summary>
    /// <param name="timerId">The ID of the timer to cancel.</param>
    /// <returns>True if the timer was found and cancelled, false otherwise.</returns>
    public bool CancelTimer(long timerId)
    {
        lock (_lock)
        {
            if (!_timersById.TryRemove(timerId, out var timer))
                return false;
            
            _pendingTimers.Remove(timer);
            timer.Cancel();
            return true;
        }
    }

    /// <summary>
    /// Creates a timer that will fire after the specified delay.
    /// </summary>
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var timer = new SimulationTimer(this, callback, state, dueTime, period);
        
        if (dueTime != Timeout.InfiniteTimeSpan && dueTime >= TimeSpan.Zero)
        {
            ScheduleTimer(timer, dueTime, null);
        }

        return timer;
    }

    /// <summary>
    /// Registers a delay operation. Called internally by Task.Delay when using this provider.
    /// </summary>
#pragma warning disable CA1068 // CancellationToken parameters should come last - matches Task.Delay signature pattern
    internal long RegisterDelay(TimeSpan delay, CancellationToken cancellationToken, TaskCompletionSource tcs, string? description = null)
#pragma warning restore CA1068
    {
        var id = Interlocked.Increment(ref _nextTimerId);
        var dueTime = _utcNow + delay;
        
        var pendingTimer = new PendingTimer(
            id,
            dueTime,
            () => tcs.TrySetResult(),
            () => tcs.TrySetCanceled(cancellationToken),
            description ?? $"Delay({delay})");

        lock (_lock)
        {
            _pendingTimers.Add(pendingTimer);
            _timersById[id] = pendingTimer;
        }

        // Handle cancellation
        if (cancellationToken.CanBeCanceled)
        {
            cancellationToken.Register(() => CancelTimer(id));
        }

        return id;
    }

    internal void ScheduleTimer(SimulationTimer timer, TimeSpan dueTime, string? description)
    {
        var id = Interlocked.Increment(ref _nextTimerId);
        var absoluteDueTime = _utcNow + dueTime;
        
        var pendingTimer = new PendingTimer(
            id,
            absoluteDueTime,
            () => timer.FireCallback(),
            () => { }, // Timers don't have cancellation callbacks
            description ?? $"Timer(due={dueTime})");

        lock (_lock)
        {
            _pendingTimers.Add(pendingTimer);
            _timersById[id] = pendingTimer;
        }

        timer.CurrentPendingId = id;
    }

    internal void UnscheduleTimer(long pendingId)
    {
        lock (_lock)
        {
            if (_timersById.TryRemove(pendingId, out var timer))
            {
                _pendingTimers.Remove(timer);
            }
        }
    }

    /// <summary>
    /// Information about a pending timer.
    /// </summary>
    /// <param name="Id">The unique ID of the timer.</param>
    /// <param name="DueTime">The absolute time when the timer will fire.</param>
    /// <param name="TimeRemaining">The time remaining until the timer fires.</param>
    /// <param name="Description">A description of what this timer is for.</param>
    internal readonly record struct TimerInfo(long Id, DateTimeOffset DueTime, TimeSpan TimeRemaining, string Description);

    private sealed class PendingTimer(
        long id,
        DateTimeOffset dueTime,
        Action fireAction,
        Action cancelAction,
        string description)
    {
        public long Id { get; } = id;
        public DateTimeOffset DueTime { get; } = dueTime;
        public string Description { get; } = description;

        public void Fire() => fireAction();
        public void Cancel() => cancelAction();
    }

    private sealed class PendingTimerComparer : IComparer<PendingTimer>
    {
        public static readonly PendingTimerComparer Instance = new();

        public int Compare(PendingTimer? x, PendingTimer? y)
        {
            if (x is null && y is null) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            var timeCompare = x.DueTime.CompareTo(y.DueTime);
            if (timeCompare != 0) return timeCompare;

            // Use ID as tiebreaker for deterministic ordering
            return x.Id.CompareTo(y.Id);
        }
    }
}

/// <summary>
/// A timer implementation for the simulation time provider.
/// </summary>
internal sealed class SimulationTimer : ITimer
{
    private readonly SimulationTimeProvider _timeProvider;
    private readonly TimerCallback _callback;
    private readonly object? _state;
    private TimeSpan _period;
    private bool _disposed;

    internal long CurrentPendingId { get; set; }

    public SimulationTimer(
        SimulationTimeProvider timeProvider,
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        _timeProvider = timeProvider;
        _callback = callback;
        _state = state;
        _period = period;
    }

    internal void FireCallback()
    {
        if (_disposed) return;

        _callback(_state);

        // Reschedule if periodic
        if (_period > TimeSpan.Zero && _period != Timeout.InfiniteTimeSpan)
        {
            _timeProvider.ScheduleTimer(this, _period, $"Timer(period={_period})");
        }
    }

    public bool Change(TimeSpan dueTime, TimeSpan period)
    {
        if (_disposed) return false;

        // Cancel current scheduled callback
        if (CurrentPendingId > 0)
        {
            _timeProvider.UnscheduleTimer(CurrentPendingId);
            CurrentPendingId = 0;
        }

        _period = period;

        // Schedule new callback if not infinite
        if (dueTime != Timeout.InfiniteTimeSpan && dueTime >= TimeSpan.Zero)
        {
            _timeProvider.ScheduleTimer(this, dueTime, $"Timer(due={dueTime}, period={period})");
        }

        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (CurrentPendingId > 0)
        {
            _timeProvider.UnscheduleTimer(CurrentPendingId);
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
