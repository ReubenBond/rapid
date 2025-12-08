namespace Rapid.Tests.Simulation;

/// <summary>
/// A synchronization context that routes all continuations through a <see cref="SimulationTaskScheduler"/>.
/// This ensures that async/await continuations are captured and executed deterministically.
/// </summary>
/// <remarks>
/// Creates a new simulation synchronization context.
/// </remarks>
/// <param name="scheduler">The task scheduler to route continuations through.</param>
internal sealed class SimulationSynchronizationContext(SimulationTaskScheduler scheduler) : SynchronizationContext
{
    /// <summary>
    /// Gets the underlying task scheduler.
    /// </summary>
    public SimulationTaskScheduler Scheduler { get; } = scheduler ?? throw new ArgumentNullException(nameof(scheduler));

    /// <inheritdoc />
    public override void Post(SendOrPostCallback d, object? state)
    {
        ArgumentNullException.ThrowIfNull(d);

        // Queue the callback as a task on the deterministic scheduler
        var task = new Task(() => d(state));
        task.Start(Scheduler);
    }

    /// <inheritdoc />
    public override void Send(SendOrPostCallback d, object? state)
    {
        ArgumentNullException.ThrowIfNull(d);

        // For Send, we execute synchronously if we're on the same thread
        // Otherwise, we queue and wait
        d(state);
    }

    /// <inheritdoc />
    public override SynchronizationContext CreateCopy() => new SimulationSynchronizationContext(Scheduler);

    /// <summary>
    /// Installs this synchronization context on the current thread and returns a scope
    /// that restores the previous context when disposed.
    /// </summary>
    /// <returns>A disposable scope that restores the previous synchronization context when disposed.</returns>
    /// <example>
    /// <code>
    /// using var _ = syncContext.Install();
    /// // Code here runs with the simulation synchronization context
    /// // Previous context is automatically restored when scope ends
    /// </code>
    /// </example>
    public SynchronizationContextScope Install()
    {
        var previous = Current;
        SetSynchronizationContext(this);
        return new SynchronizationContextScope(previous);
    }
}

/// <summary>
/// A disposable scope that restores the previous synchronization context when disposed.
/// </summary>
/// <remarks>
/// This struct is returned by <see cref="SimulationSynchronizationContext.Install"/> and
/// should be used with a using statement to ensure the previous context is restored.
/// </remarks>
internal readonly struct SynchronizationContextScope : IDisposable
{
    private readonly SynchronizationContext? _previous;

    /// <summary>
    /// Creates a new scope that will restore the specified context when disposed.
    /// </summary>
    /// <param name="previous">The synchronization context to restore.</param>
    internal SynchronizationContextScope(SynchronizationContext? previous)
    {
        _previous = previous;
    }

    /// <summary>
    /// Restores the previous synchronization context.
    /// </summary>
    public void Dispose()
    {
        SynchronizationContext.SetSynchronizationContext(_previous);
    }
}
