namespace Rapid.Tests.Simulation;

/// <summary>
/// A synchronization context that routes all continuations through a <see cref="DeterministicTaskScheduler"/>.
/// This ensures that async/await continuations are captured and executed deterministically.
/// </summary>
/// <remarks>
/// Creates a new deterministic synchronization context.
/// </remarks>
/// <param name="scheduler">The task scheduler to route continuations through.</param>
internal sealed class DeterministicSynchronizationContext(DeterministicTaskScheduler scheduler) : SynchronizationContext
{
    /// <summary>
    /// Gets the underlying task scheduler.
    /// </summary>
    public DeterministicTaskScheduler Scheduler { get; } = scheduler ?? throw new ArgumentNullException(nameof(scheduler));

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
    public override SynchronizationContext CreateCopy() => new DeterministicSynchronizationContext(Scheduler);

    /// <summary>
    /// Installs this synchronization context on the current thread.
    /// </summary>
    /// <returns>The previous synchronization context, which should be restored when done.</returns>
    public SynchronizationContext? Install()
    {
        var previous = Current;
        SetSynchronizationContext(this);
        return previous;
    }

    /// <summary>
    /// Restores the previous synchronization context.
    /// </summary>
    /// <param name="previous">The previous synchronization context to restore.</param>
    public static void Restore(SynchronizationContext? previous) => SetSynchronizationContext(previous);
}
