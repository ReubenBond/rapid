using System.Globalization;

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
internal sealed class SimulationTimeProvider : TimeProvider
{
    private readonly SimulationTaskQueue _taskQueue;
    private readonly DateTimeOffset _start;

    /// <summary>
    /// Initializes a new instance of the <see cref="SimulationTimeProvider"/> class with a task queue.
    /// </summary>
    /// <param name="taskQueue">The task queue for scheduling timer callbacks.</param>
    /// <param name="startDateTime">The initial time and date reported by the provider. Defaults to midnight January 1st 2000.</param>
    public SimulationTimeProvider(SimulationTaskQueue taskQueue, DateTimeOffset? startDateTime = null)
    {
        ArgumentNullException.ThrowIfNull(taskQueue);

        _taskQueue = taskQueue;
        _start = startDateTime ?? new DateTimeOffset(2000, 1, 1, 0, 0, 0, 0, TimeSpan.Zero);

        if (startDateTime.HasValue)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(startDateTime.Value.Ticks, 0);
        }
    }

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => _start + _taskQueue.CurrentTime;

    /// <inheritdoc />
    public override long GetTimestamp() => (_start + _taskQueue.CurrentTime).Ticks;

    /// <inheritdoc />
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    /// <inheritdoc />
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <inheritdoc />
    public override string ToString() => GetUtcNow().ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var timer = new SimulationTimer(_taskQueue, callback, state);
        _ = timer.Change(dueTime, period);
        return timer;
    }
}
