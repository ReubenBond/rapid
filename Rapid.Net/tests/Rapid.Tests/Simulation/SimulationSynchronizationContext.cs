namespace Rapid.Tests.Simulation;
/// <summary>
/// A synchronization context that routes all continuations through a <see cref="SimulationTaskQueue"/>.
/// </summary>
internal sealed class SimulationSynchronizationContext(SimulationTaskQueue taskQueue) : SynchronizationContext
{
    public override void Post(SendOrPostCallback d, object? state)
    {
        ArgumentNullException.ThrowIfNull(d);
        taskQueue.Enqueue(new ScheduledSyncContextItem(d, state));
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        ArgumentNullException.ThrowIfNull(d);
        // For Send, we execute synchronously
        d(state);
    }

    public override SynchronizationContext CreateCopy() => new SimulationSynchronizationContext(taskQueue);

    /// <summary>
    /// Installs this synchronization context on the current thread and returns a scope
    /// that restores the previous context when disposed.
    /// </summary>
    /// <returns>A disposable scope that restores the previous synchronization context when disposed.</returns>
    public SynchronizationContextScope Install()
    {
        var previous = Current;
        SetSynchronizationContext(this);
        return new SynchronizationContextScope(previous);
    }

    private sealed class ScheduledSyncContextItem(SendOrPostCallback callback, object? state) : ScheduledItem
    {
        protected internal override void Invoke() => callback(state);
    }
}
