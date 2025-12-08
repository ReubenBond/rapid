using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Rapid.Tests.Simulation;

/// <summary>
/// A time provider for simulation testing that integrates with <see cref="SimulationTaskQueue"/>
/// for deterministic timer execution.
/// 
/// When a <see cref="SimulationTaskQueue"/> is provided, timer callbacks are scheduled through
/// the queue instead of being executed immediately. This enables fully deterministic simulation
/// testing where task execution order is controlled.
/// 
/// When the scheduler is empty and time needs to advance, the time provider can automatically
/// advance to the next scheduled timer.
/// </summary>
internal sealed partial class SimulationTimeProvider : TimeProvider
{
    internal readonly HashSet<SimulationWaiter> Waiters = [];
    private readonly ILogger<SimulationTimeProvider> _logger;
    private readonly SimulationTaskQueue? _taskQueue;
    private DateTimeOffset _now;
    private TimeZoneInfo _localTimeZone = TimeZoneInfo.Utc;
    private volatile int _wakeWaitersGate;

    [LoggerMessage(Level = LogLevel.Trace, Message = "Advance({Duration}) from {FromTime} to {ToTime}, waiters={WaiterCount}")]
    private partial void LogAdvance(TimeSpan duration, string fromTime, string toTime, int waiterCount);

    [LoggerMessage(Level = LogLevel.Trace, Message = "WakeWaiters: firing waiter, wakeupTime={WakeupTime}")]
    private partial void LogFireWaiter(string wakeupTime);

    [LoggerMessage(Level = LogLevel.Trace, Message = "CreateTimer: dueTime={DueTime}, period={Period}")]
    private partial void LogCreateTimer(TimeSpan dueTime, TimeSpan period);

    [LoggerMessage(Level = LogLevel.Trace, Message = "AddWaiter: wakeupTime={WakeupTime}")]
    private partial void LogAddWaiter(string wakeupTime);

    /// <summary>
    /// Initializes a new instance of the <see cref="SimulationTimeProvider"/> class.
    /// </summary>
    /// <remarks>
    /// This creates a provider whose time is initially set to midnight January 1st 2000.
    /// The provider is set to not automatically advance time each time it is read.
    /// </remarks>
    public SimulationTimeProvider()
    {
        _logger = NullLogger<SimulationTimeProvider>.Instance;
        Start = _now = new(2000, 1, 1, 0, 0, 0, 0, TimeSpan.Zero);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SimulationTimeProvider"/> class.
    /// </summary>
    /// <param name="startDateTime">The initial time and date reported by the provider.</param>
    /// <param name="logger">Optional logger for diagnostic output.</param>
    /// <remarks>
    /// The provider is set to not automatically advance time each time it is read.
    /// </remarks>
    public SimulationTimeProvider(DateTimeOffset startDateTime, ILogger<SimulationTimeProvider>? logger = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(startDateTime.Ticks, 0);

        Start = _now = startDateTime;
        _logger = logger ?? NullLogger<SimulationTimeProvider>.Instance;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SimulationTimeProvider"/> class with a shared task queue.
    /// </summary>
    /// <param name="taskQueue">The shared task queue for scheduling timer callbacks.</param>
    /// <param name="startDateTime">The initial time and date reported by the provider.</param>
    /// <param name="logger">Optional logger for diagnostic output.</param>
    public SimulationTimeProvider(SimulationTaskQueue taskQueue, DateTimeOffset? startDateTime = null, ILogger<SimulationTimeProvider>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(taskQueue);

        _taskQueue = taskQueue;
        Start = _now = startDateTime ?? new DateTimeOffset(2000, 1, 1, 0, 0, 0, 0, TimeSpan.Zero);
        _logger = logger ?? NullLogger<SimulationTimeProvider>.Instance;

        // Sync the task queue's time with our time
        _taskQueue.CurrentTimeTicks = _now.Ticks;
    }

    /// <summary>
    /// Gets the starting date and time for this provider.
    /// </summary>
    public DateTimeOffset Start { get; }

    /// <summary>
    /// Gets the shared task queue, if one was provided.
    /// </summary>
    public SimulationTaskQueue? TaskQueue => _taskQueue;

    /// <summary>
    /// Gets or sets the amount of time by which time advances whenever the clock is read.
    /// </summary>
    /// <remarks>
    /// This defaults to <see cref="TimeSpan.Zero"/>.
    /// </remarks>
    public TimeSpan AutoAdvanceAmount
    {
        get => field;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value.Ticks, 0);
            field = value;
        }
    }

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow()
    {
        DateTimeOffset result;

        lock (Waiters)
        {
            result = _now;
            _now += AutoAdvanceAmount;
            SyncTaskQueueTime();
        }

        WakeWaiters();
        return result;
    }

    /// <summary>
    /// Advances the date and time in the UTC time zone.
    /// </summary>
    /// <param name="value">The date and time in the UTC time zone.</param>
    /// <exception cref="ArgumentOutOfRangeException">The supplied time value is before the current time.</exception>
    /// <remarks>
    /// This method simply advances time. If the time is set forward beyond the
    /// trigger point of any outstanding timers, those timers will immediately trigger
    /// (or be scheduled to the task queue if one is configured).
    /// This is unlike the <see cref="AdjustTime" /> method, which has no impact
    /// on timers.
    /// </remarks>
    public void SetUtcNow(DateTimeOffset value)
    {
        lock (Waiters)
        {
            if (value < _now)
            {
                throw new ArgumentOutOfRangeException(nameof(value), $"Cannot go back in time. Current time is {_now}.");
            }

            _now = value;
            SyncTaskQueueTime();
        }

        WakeWaiters();
    }

    /// <summary>
    /// Advances time by a specific amount.
    /// </summary>
    /// <param name="delta">The amount of time to advance the clock by.</param>
    /// <remarks>
    /// Advancing time affects the timers created from this provider, and all other operations that are directly or
    /// indirectly using this provider as a time source.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The time value is less than <see cref="TimeSpan.Zero"/>.</exception>
    public void Advance(TimeSpan delta)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delta.Ticks, 0);

        lock (Waiters)
        {
            LogAdvance(delta, _now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture), 
                (_now + delta).ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture), Waiters.Count);
            _now += delta;
            SyncTaskQueueTime();
        }

        WakeWaiters();
    }

    /// <summary>
    /// Sets the date and time in the UTC time zone.
    /// </summary>
    /// <param name="value">The date and time in the UTC time zone.</param>
    /// <remarks>
    /// This method updates the current time, and has no impact on outstanding
    /// timers. This is similar to what happens in a real system when the system's
    /// time is changed.
    /// </remarks>
    public void AdjustTime(DateTimeOffset value)
    {
        lock (Waiters)
        {
            var delta = value - _now;
            _now = value;

            // adjust the wake times so they're relative to the new time value
            foreach (var w in Waiters)
            {
                w.WakeupTime += delta.Ticks;
            }

            SyncTaskQueueTime();
        }
    }

    /// <inheritdoc />
    public override long GetTimestamp() => GetUtcNow().Ticks;

    /// <inheritdoc />
    public override TimeZoneInfo LocalTimeZone => _localTimeZone;

    /// <summary>
    /// Sets the local time zone.
    /// </summary>
    /// <param name="localTimeZone">The local time zone.</param>
    public void SetLocalTimeZone(TimeZoneInfo localTimeZone)
    {
        ArgumentNullException.ThrowIfNull(localTimeZone);
        _localTimeZone = localTimeZone;
    }

    /// <summary>
    /// Gets the amount by which the value from <see cref="GetTimestamp"/> increments per second.
    /// </summary>
    /// <remarks>
    /// This is fixed to the value of <see cref="TimeSpan.TicksPerSecond"/>.
    /// </remarks>
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <summary>
    /// Gets the number of pending waiters/timers.
    /// </summary>
    public int PendingTimerCount
    {
        get
        {
            lock (Waiters)
            {
                return Waiters.Count;
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
            lock (Waiters)
            {
                return Waiters.Count > 0;
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
            lock (Waiters)
            {
                if (Waiters.Count == 0)
                    return null;

                var minWakeup = Waiters.Min(w => w.WakeupTime);
                var duration = TimeSpan.FromTicks(minWakeup - _now.Ticks);
                return duration > TimeSpan.Zero ? duration : TimeSpan.Zero;
            }
        }
    }

    /// <summary>
    /// Gets the due time of the next timer in ticks, or null if no timers are pending.
    /// </summary>
    public long? NextTimerDueTicks
    {
        get
        {
            lock (Waiters)
            {
                if (Waiters.Count == 0)
                    return null;

                return Waiters.Min(w => w.WakeupTime);
            }
        }
    }

    /// <summary>
    /// Advances time to fire exactly the next pending timer.
    /// </summary>
    /// <returns>True if a timer was fired (or scheduled), false if no timers were pending.</returns>
    public bool AdvanceToNextTimer()
    {
        lock (Waiters)
        {
            if (Waiters.Count == 0)
                return false;

            var minWakeup = Waiters.Min(w => w.WakeupTime);
            if (minWakeup > _now.Ticks)
            {
                _now = new DateTimeOffset(minWakeup, TimeSpan.Zero);
                SyncTaskQueueTime();
            }
        }

        WakeWaiters();
        return true;
    }

    /// <summary>
    /// Gets information about all pending timers.
    /// </summary>
    public IReadOnlyList<TimerInfo> GetPendingTimers()
    {
        lock (Waiters)
        {
            return Waiters
                .OrderBy(w => w.WakeupTime)
                .ThenBy(w => w.ScheduledOn)
                .Select(w => new TimerInfo(
                    new DateTimeOffset(w.WakeupTime, TimeSpan.Zero),
                    TimeSpan.FromTicks(w.Period)))
                .ToList();
        }
    }

    /// <summary>
    /// Returns a string representation this provider's idea of current time.
    /// </summary>
    /// <returns>A string representing the provider's current time.</returns>
    public override string ToString() => _now.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        LogCreateTimer(dueTime, period);

        var timer = new SimulationTimer(this, callback, state);
        _ = timer.Change(dueTime, period);
        return timer;
    }

    internal void RemoveWaiter(SimulationWaiter waiter)
    {
        lock (Waiters)
        {
            _ = Waiters.Remove(waiter);
        }
    }

    internal void AddWaiter(SimulationWaiter waiter, long dueTime)
    {
        lock (Waiters)
        {
            waiter.ScheduledOn = _now.Ticks;
            waiter.WakeupTime = _now.Ticks + dueTime;
            _ = Waiters.Add(waiter);
            LogAddWaiter(new DateTimeOffset(waiter.WakeupTime, TimeSpan.Zero).ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture));
        }

        WakeWaiters();
    }

    /// <summary>
    /// Event raised when the gate is about to be released in WakeWaiters.
    /// Used for testing thread synchronization scenarios.
    /// </summary>
    internal event EventHandler? GateOpening;

    /// <summary>
    /// Synchronizes the task queue's time with the time provider's time.
    /// Must be called while holding the Waiters lock.
    /// </summary>
    private void SyncTaskQueueTime()
    {
        if (_taskQueue != null)
        {
            _taskQueue.CurrentTimeTicks = _now.Ticks;
        }
    }

    private void WakeWaiters()
    {
        if (Interlocked.CompareExchange(ref _wakeWaitersGate, 1, 0) == 1)
        {
            // some other thread is already in here, so let it take care of things
            return;
        }

        while (true)
        {
            SimulationWaiter? candidate = null;
            lock (Waiters)
            {
                // find an expired waiter
                foreach (var waiter in Waiters)
                {
                    if (waiter.WakeupTime > _now.Ticks)
                    {
                        // not expired yet
                    }
                    else if (candidate is null)
                    {
                        // our first candidate
                        candidate = waiter;
                    }
                    else if (waiter.WakeupTime < candidate.WakeupTime)
                    {
                        // found a waiter with an earlier wake time, it's our new candidate
                        candidate = waiter;
                    }
                    else if (waiter.WakeupTime > candidate.WakeupTime)
                    {
                        // the waiter has a later wake time, so keep the current candidate
                    }
                    else if (waiter.ScheduledOn < candidate.ScheduledOn)
                    {
                        // the new waiter has the same wake time as the candidate, pick whichever was scheduled earliest to maintain order
                        candidate = waiter;
                    }
                }

                if (candidate == null)
                {
                    // didn't find a candidate to wake, we're done
                    GateOpening?.Invoke(this, EventArgs.Empty);
                    _wakeWaitersGate = 0;
                    return;
                }
            }

            LogFireWaiter(new DateTimeOffset(candidate.WakeupTime, TimeSpan.Zero).ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture));

            // If we have a task queue, schedule the callback instead of executing it directly
            if (_taskQueue != null)
            {
                var waiterToFire = candidate;
                var wakeupTime = candidate.WakeupTime;
                var period = candidate.Period;

                // Schedule the callback on the task queue
                _taskQueue.EnqueueAt(() =>
                {
                    waiterToFire.InvokeCallback();

                    // Handle periodic timers by rescheduling
                    if (period > 0)
                    {
                        lock (Waiters)
                        {
                            if (Waiters.Contains(waiterToFire))
                            {
                                waiterToFire.ScheduledOn = _now.Ticks;
                                waiterToFire.WakeupTime = _now.Ticks + period;
                            }
                        }
                    }
                }, wakeupTime);

                // Remove from waiters if not periodic, or update for next period
                if (period > 0)
                {
                    lock (Waiters)
                    {
                        candidate.ScheduledOn = _now.Ticks;
                        candidate.WakeupTime += period;
                    }
                }
                else
                {
                    RemoveWaiter(candidate);
                }
            }
            else
            {
                // No task queue - execute directly (original behavior)
                var oldTicks = _now.Ticks;

                // invoke the callback
                candidate.InvokeCallback();

                var newTicks = _now.Ticks;

                // see if we need to reschedule the waiter
                if (candidate.Period > 0)
                {
                    // update the waiter's state
                    candidate.ScheduledOn = newTicks;

                    if (oldTicks != newTicks)
                    {
                        // time changed while in the callback, readjust the wake time accordingly
                        candidate.WakeupTime = newTicks + candidate.Period;
                    }
                    else
                    {
                        // move on to the next period
                        candidate.WakeupTime += candidate.Period;
                    }
                }
                else
                {
                    // this waiter is never running again, so remove from the set.
                    RemoveWaiter(candidate);
                }
            }
        }
    }

    /// <summary>
    /// Information about a pending timer.
    /// </summary>
    internal sealed record TimerInfo(DateTimeOffset WakeupTime, TimeSpan Period);
}

/// <summary>
/// Represents a pending timer/delay operation.
/// We keep all timer state here in order to prevent Timer instances from being self-referential,
/// which would block them being collected when someone forgets to call Dispose on the timer.
/// </summary>
internal sealed class SimulationWaiter
{
    private readonly TimerCallback _callback;
    private readonly object? _state;

    public long ScheduledOn { get; set; } = -1;
    public long WakeupTime { get; set; } = -1;
    public long Period { get; }

    public SimulationWaiter(TimerCallback callback, object? state, long period)
    {
        _callback = callback;
        _state = state;
        Period = period;
    }

    public void InvokeCallback() => _callback(_state);
}

/// <summary>
/// A timer implementation for the simulation time provider.
/// This implements the timer abstractions and is a thin wrapper around a waiter object.
/// The main role of this type is to create the waiter, add it to the waiter list, and ensure it gets
/// removed from the waiter list when disposed or collected.
/// </summary>
internal sealed class SimulationTimer : ITimer
{
    private const uint MaxSupportedTimeout = 0xfffffffe;

    private SimulationWaiter? _waiter;
    private SimulationTimeProvider? _timeProvider;
    private TimerCallback? _callback;
    private object? _state;

    public SimulationTimer(SimulationTimeProvider timeProvider, TimerCallback callback, object? state)
    {
        _timeProvider = timeProvider;
        _callback = callback;
        _state = state;
    }

    public bool Change(TimeSpan dueTime, TimeSpan period)
    {
        var dueTimeMs = (long)dueTime.TotalMilliseconds;
        var periodMs = (long)period.TotalMilliseconds;

        // -1 means infinite (valid), otherwise must be non-negative and within MaxSupportedTimeout
        if (dueTimeMs < -1)
            throw new ArgumentOutOfRangeException(nameof(dueTime));
        if (dueTimeMs != -1 && (ulong)dueTimeMs > MaxSupportedTimeout)
            throw new ArgumentOutOfRangeException(nameof(dueTime));
        if (periodMs < -1)
            throw new ArgumentOutOfRangeException(nameof(period));
        if (periodMs != -1 && (ulong)periodMs > MaxSupportedTimeout)
            throw new ArgumentOutOfRangeException(nameof(period));

        var timeProvider = _timeProvider;
        if (timeProvider is null)
        {
            // timer has been disposed
            return false;
        }

        var waiter = _waiter;
        if (waiter is not null)
        {
            // remove any previous waiter
            timeProvider.RemoveWaiter(waiter);
            _waiter = null;
        }

        if (dueTimeMs < 0)
        {
            // this waiter will never wake up, so just bail
            return true;
        }

        if (periodMs < 0 || periodMs == Timeout.Infinite)
        {
            // normalize
            period = TimeSpan.Zero;
        }

        _waiter = waiter = new SimulationWaiter(_callback!, _state, period.Ticks);
        timeProvider.AddWaiter(waiter, dueTime.Ticks);
        return true;
    }

    // In case the timer is not disposed, this will remove the Waiter instance from the provider.
    ~SimulationTimer() => Dispose(false);

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    public ValueTask DisposeAsync()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    private void Dispose(bool _)
    {
        var waiter = _waiter;
        if (waiter is not null)
        {
            _timeProvider?.RemoveWaiter(waiter);
            _waiter = null;
        }

        _timeProvider = null;
        _callback = null;
        _state = null;
    }
}
