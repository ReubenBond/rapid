namespace Rapid.Tests.Simulation;

/// <summary>
/// Shared time source for all simulation components.
/// Provides a single source of truth for the current simulated time,
/// allowing multiple <see cref="SimulationTaskQueue"/> instances to share
/// a unified view of time while maintaining separate task queues.
/// </summary>
internal sealed class SimulationClock
{
    private readonly Lock _lock = new();
    private TimeSpan _currentTime;

    /// <summary>
    /// Creates a new simulation clock with the specified initial time.
    /// </summary>
    /// <param name="initialTime">The initial time offset. Default is <see cref="TimeSpan.Zero"/>.</param>
    public SimulationClock(TimeSpan initialTime = default)
    {
        _currentTime = initialTime;
    }

    /// <summary>
    /// Gets the current simulated time as an offset from the start.
    /// </summary>
    public TimeSpan CurrentTime
    {
        get
        {
            lock (_lock)
            {
                return _currentTime;
            }
        }
    }

    /// <summary>
    /// Advances the current time by the specified amount.
    /// </summary>
    /// <param name="delta">The amount to advance. Must be non-negative.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="delta"/> is negative.</exception>
    public void Advance(TimeSpan delta)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delta, TimeSpan.Zero);
        lock (_lock)
        {
            _currentTime += delta;
        }
    }

    /// <summary>
    /// Sets the current time to the specified value.
    /// </summary>
    /// <param name="time">The new time. Must not be before the current time.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="time"/> is before the current time.</exception>
    public void SetTime(TimeSpan time)
    {
        lock (_lock)
        {
            if (time < _currentTime)
            {
                throw new ArgumentOutOfRangeException(nameof(time), 
                    $"Cannot go back in time. Current time is {_currentTime}, attempted to set to {time}.");
            }
            _currentTime = time;
        }
    }
}
