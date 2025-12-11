namespace Rapid.Tests.Simulation;

/// <summary>
/// Shared time source for all simulation components.
/// Provides a single source of truth for the current simulated time,
/// allowing multiple <see cref="SimulationTaskQueue"/> instances to share
/// a unified view of time while maintaining separate task queues.
/// </summary>
/// <remarks>
/// Creates a new simulation clock with the specified start date/time and initial time offset.
/// </remarks>
/// <param name="startDateTime">The starting date/time for the simulation.</param>
/// <param name="initialTime">The initial time offset. Default is <see cref="TimeSpan.Zero"/>.</param>
internal sealed class SimulationClock(DateTimeOffset startDateTime, TimeSpan initialTime = default)
{
    private readonly Lock _lock = new();
    private TimeSpan _currentTime = initialTime;

    /// <summary>
    /// Gets the starting date/time for the simulation.
    /// </summary>
    public DateTimeOffset StartDateTime { get; } = startDateTime;

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
    /// Gets the current simulated date/time (StartDateTime + CurrentTime).
    /// </summary>
    public DateTimeOffset UtcNow => StartDateTime + CurrentTime;

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
}
