using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Rapid.Tests.Simulation;

/// <summary>
/// A time provider for simulation testing that integrates with <see cref="SimulationTaskQueue"/>
/// for deterministic timer execution.
/// 
/// Timer callbacks are scheduled through the queue instead of being executed immediately.
/// This enables fully deterministic simulation testing where task execution order is controlled.
/// </summary>
internal sealed partial class SimulationTimeProvider : TimeProvider
{
    private readonly ILogger<SimulationTimeProvider> _logger;
    private readonly SimulationTaskQueue _taskQueue;
    private readonly Lock _lock = new();
    private DateTimeOffset _now;
    private TimeZoneInfo _localTimeZone = TimeZoneInfo.Utc;

    [LoggerMessage(Level = LogLevel.Trace, Message = "Advance({Duration}) from {FromTime} to {ToTime}")]
    private partial void LogAdvance(TimeSpan duration, string fromTime, string toTime);

    [LoggerMessage(Level = LogLevel.Trace, Message = "CreateTimer: dueTime={DueTime}, period={Period}")]
    private partial void LogCreateTimer(TimeSpan dueTime, TimeSpan period);

    /// <summary>
    /// Initializes a new instance of the <see cref="SimulationTimeProvider"/> class with a task queue.
    /// </summary>
    /// <param name="taskQueue">The task queue for scheduling timer callbacks.</param>
    /// <param name="startDateTime">The initial time and date reported by the provider. Defaults to midnight January 1st 2000.</param>
    /// <param name="logger">Optional logger for diagnostic output.</param>
    public SimulationTimeProvider(SimulationTaskQueue taskQueue, DateTimeOffset? startDateTime = null, ILogger<SimulationTimeProvider>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(taskQueue);

        _taskQueue = taskQueue;
        Start = _now = startDateTime ?? new DateTimeOffset(2000, 1, 1, 0, 0, 0, 0, TimeSpan.Zero);
        _logger = logger ?? NullLogger<SimulationTimeProvider>.Instance;

        if (startDateTime.HasValue)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(startDateTime.Value.Ticks, 0);
        }

        // Initialize the task queue's time to match our time
        _taskQueue.CurrentTimeTicks = _now.Ticks;
    }

    /// <summary>
    /// Gets the starting date and time for this provider.
    /// </summary>
    public DateTimeOffset Start { get; }

    /// <summary>
    /// Gets the task queue.
    /// </summary>
    public SimulationTaskQueue TaskQueue => _taskQueue;

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

        lock (_lock)
        {
            result = _now;
            _now += AutoAdvanceAmount;
            _taskQueue.CurrentTimeTicks = _now.Ticks;
        }

        return result;
    }

    /// <summary>
    /// Advances the date and time in the UTC time zone.
    /// </summary>
    /// <param name="value">The date and time in the UTC time zone.</param>
    /// <exception cref="ArgumentOutOfRangeException">The supplied time value is before the current time.</exception>
    /// <remarks>
    /// This method simply advances time. If the time is set forward beyond the
    /// trigger point of any outstanding timers, those timers will be moved to the ready queue.
    /// This is unlike the <see cref="AdjustTime" /> method, which has no impact on timers.
    /// </remarks>
    public void SetUtcNow(DateTimeOffset value)
    {
        lock (_lock)
        {
            if (value < _now)
            {
                throw new ArgumentOutOfRangeException(nameof(value), $"Cannot go back in time. Current time is {_now}.");
            }

            _now = value;
            _taskQueue.CurrentTimeTicks = _now.Ticks;
        }
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

        lock (_lock)
        {
            LogAdvance(delta, _now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
                (_now + delta).ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture));
            _now += delta;
            _taskQueue.CurrentTimeTicks = _now.Ticks;
        }
    }

    /// <summary>
    /// Sets the date and time in the UTC time zone.
    /// </summary>
    /// <param name="value">The date and time in the UTC time zone.</param>
    /// <remarks>
    /// This method updates the current time and adjusts the wake times of
    /// outstanding timers accordingly. This is similar to what happens in a real 
    /// system when the system's time is changed - timers still fire after the same
    /// relative duration from when they were scheduled.
    /// </remarks>
    public void AdjustTime(DateTimeOffset value)
    {
        lock (_lock)
        {
            var delta = value.Ticks - _now.Ticks;
            _now = value;
            // Adjust waiting timer due times by the same delta so they fire at the
            // same relative time from now as they would have before the adjustment
            _taskQueue.AdjustWaitingDueTimes(delta);
            // Update the task queue's notion of current time
            _taskQueue.CurrentTimeTicks = _now.Ticks;
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
    /// Gets the time until the next pending timer fires, or null if no timers are pending.
    /// </summary>
    public TimeSpan? TimeUntilNextTimer
    {
        get
        {
            var nextDueTime = _taskQueue.NextWaitingDueTimeTicks;
            if (!nextDueTime.HasValue)
                return null;

            lock (_lock)
            {
                var duration = TimeSpan.FromTicks(nextDueTime.Value - _now.Ticks);
                return duration > TimeSpan.Zero ? duration : TimeSpan.Zero;
            }
        }
    }

    /// <summary>
    /// Advances time to fire exactly the next pending timer.
    /// </summary>
    /// <returns>True if time was advanced, false if no timers were pending.</returns>
    public bool AdvanceToNextTimer()
    {
        var nextDueTime = _taskQueue.NextWaitingDueTimeTicks;
        if (!nextDueTime.HasValue)
            return false;

        lock (_lock)
        {
            if (nextDueTime.Value > _now.Ticks)
            {
                _now = new DateTimeOffset(nextDueTime.Value, TimeSpan.Zero);
                _taskQueue.CurrentTimeTicks = _now.Ticks;
            }
        }

        return true;
    }

    /// <summary>
    /// Gets information about all pending timers.
    /// Note: This returns information based on the waiting queue, which may not
    /// include timers that are already in the ready queue.
    /// </summary>
    public IReadOnlyList<TimerInfo> GetPendingTimers()
    {
        var snapshot = _taskQueue.GetSnapshot();
        var result = new List<TimerInfo>();

        foreach (var (_, dueTime) in snapshot.Waiting)
        {
            result.Add(new TimerInfo(
                new DateTimeOffset(dueTime, TimeSpan.Zero),
                TimeSpan.Zero)); // Period info not available from snapshot
        }

        return result.OrderBy(t => t.WakeupTime).ToList();
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

        var timer = new SimulationTimer(this, _taskQueue, callback, state);
        _ = timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>
    /// Information about a pending timer.
    /// </summary>
    internal sealed record TimerInfo(DateTimeOffset WakeupTime, TimeSpan Period);
}

/// <summary>
/// A timer implementation for the simulation time provider.
/// This implements the timer abstractions using SimulationTaskQueue for scheduling.
/// </summary>
internal sealed class SimulationTimer : ITimer
{
    private const uint MaxSupportedTimeout = 0xfffffffe;

    private SimulationTimeProvider? _timeProvider;
    private SimulationTaskQueue? _taskQueue;
    private TimerCallback? _callback;
    private object? _state;
    private long _timerId = -1;

    public SimulationTimer(SimulationTimeProvider timeProvider, SimulationTaskQueue taskQueue, TimerCallback callback, object? state)
    {
        _timeProvider = timeProvider;
        _taskQueue = taskQueue;
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

        var taskQueue = _taskQueue;
        var timeProvider = _timeProvider;
        if (taskQueue is null || timeProvider is null)
        {
            // timer has been disposed
            return false;
        }

        // Cancel any existing timer
        if (_timerId >= 0)
        {
            taskQueue.CancelTimer(_timerId);
            _timerId = -1;
        }

        if (dueTimeMs < 0)
        {
            // Infinite due time means the timer is disabled
            return true;
        }

        if (periodMs < 0 || periodMs == Timeout.Infinite)
        {
            // Normalize period
            period = TimeSpan.Zero;
        }

        // Schedule the new timer
        var currentTime = timeProvider.GetUtcNow().Ticks;
        var dueTimeTicks = currentTime + dueTime.Ticks;
        var callback = _callback!;
        var state = _state;

        _timerId = taskQueue.ScheduleTimer(
            () => callback(state),
            dueTimeTicks,
            period.Ticks);

        return true;
    }

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
        if (_timerId >= 0)
        {
            _taskQueue?.CancelTimer(_timerId);
            _timerId = -1;
        }

        _timeProvider = null;
        _taskQueue = null;
        _callback = null;
        _state = null;
    }
}
