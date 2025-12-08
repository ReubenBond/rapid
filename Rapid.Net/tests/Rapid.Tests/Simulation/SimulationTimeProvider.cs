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
/// 
/// Time is tracked centrally by the <see cref="SimulationTaskQueue"/> - this provider
/// delegates all time queries and modifications to the task queue.
/// </summary>
internal sealed partial class SimulationTimeProvider : TimeProvider
{
    private readonly ILogger<SimulationTimeProvider> _logger;
    private readonly SimulationTaskQueue _taskQueue;

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
        Start = startDateTime ?? new DateTimeOffset(2000, 1, 1, 0, 0, 0, 0, TimeSpan.Zero);
        _logger = logger ?? NullLogger<SimulationTimeProvider>.Instance;

        if (startDateTime.HasValue)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(startDateTime.Value.Ticks, 0);
        }

        // Initialize the task queue's time to zero (start time is tracked separately)
        _taskQueue.CurrentTime = TimeSpan.Zero;
    }

    /// <summary>
    /// Gets the starting date and time for this provider.
    /// </summary>
    public DateTimeOffset Start { get; }

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => Start + _taskQueue.CurrentTime;

    /// <summary>
    /// Advances the date and time in the UTC time zone.
    /// </summary>
    /// <param name="value">The date and time in the UTC time zone.</param>
    /// <exception cref="ArgumentOutOfRangeException">The supplied time value is before the current time.</exception>
    /// <remarks>
    /// This method simply advances time. If the time is set forward beyond the
    /// trigger point of any outstanding timers, those timers will be moved to the ready queue.
    /// </remarks>
    public void SetUtcNow(DateTimeOffset value)
    {
        var currentTime = GetUtcNow();
        if (value < currentTime)
        {
            throw new ArgumentOutOfRangeException(nameof(value), $"Cannot go back in time. Current time is {currentTime}.");
        }

        _taskQueue.CurrentTime = value - Start;
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
        ArgumentOutOfRangeException.ThrowIfLessThan(delta, TimeSpan.Zero);

        var fromTime = GetUtcNow();
        var toTime = fromTime + delta;
        LogAdvance(delta,
            fromTime.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
            toTime.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture));
        _taskQueue.CurrentTime += delta;
    }

    /// <inheritdoc />
    public override long GetTimestamp() => (Start + _taskQueue.CurrentTime).Ticks;

    /// <inheritdoc />
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

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
            var nextDueTime = _taskQueue.NextWaitingDueTime;
            if (!nextDueTime.HasValue)
                return null;

            var currentTime = _taskQueue.CurrentTime;
            var duration = nextDueTime.Value - currentTime;
            return duration > TimeSpan.Zero ? duration : TimeSpan.Zero;
        }
    }

    /// <summary>
    /// Advances time to fire exactly the next pending timer.
    /// </summary>
    /// <returns>True if time was advanced, false if no timers were pending.</returns>
    public bool AdvanceToNextTimer()
    {
        var nextDueTime = _taskQueue.NextWaitingDueTime;
        if (!nextDueTime.HasValue)
            return false;

        if (nextDueTime.Value > _taskQueue.CurrentTime)
        {
            _taskQueue.CurrentTime = nextDueTime.Value;
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
        var (_, waiting) = _taskQueue.GetSnapshot();
        var result = new List<TimerInfo>();

        foreach (var item in waiting)
        {
            if (item is ScheduledTimerItem timerItem)
            {
                result.Add(new TimerInfo(Start + timerItem.DueTime, timerItem.Period));
            }
        }

        return [.. result.OrderBy(t => t.WakeupTime)];
    }

    /// <summary>
    /// Returns a string representation this provider's idea of current time.
    /// </summary>
    /// <returns>A string representing the provider's current time.</returns>
    public override string ToString() => GetUtcNow().ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        LogCreateTimer(dueTime, period);

        var timer = new SimulationTimer(_taskQueue, callback, state);
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
internal sealed class SimulationTimer(SimulationTaskQueue taskQueue, TimerCallback callback, object? state) : ITimer
{
    private const uint MaxSupportedTimeout = 0xfffffffe;

    private readonly TimerCallback? _callback = callback;
    private readonly object? _state = state;
    private SimulationTaskQueue? _taskQueue = taskQueue;
    private IDisposable? _scheduledTimer;

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
        if (taskQueue is null)
        {
            // timer has been disposed
            return false;
        }

        // Cancel any existing timer
        _scheduledTimer?.Dispose();
        _scheduledTimer = null;

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
        var currentTime = taskQueue.CurrentTime;
        var scheduledDueTime = currentTime + dueTime;
        var callback = _callback!;
        var state = _state;

        _scheduledTimer = taskQueue.ScheduleTimer(
            () => callback(state),
            scheduledDueTime,
            period);

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
        _scheduledTimer?.Dispose();
        _scheduledTimer = null;
        _taskQueue = null;
    }
}
