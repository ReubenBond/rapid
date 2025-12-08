namespace Rapid.Tests.Simulation;

/// <summary>
/// A synchronization context that routes all continuations through a <see cref="SimulationTaskQueue"/>.
/// </summary>
internal sealed class SimulationSynchronizationContext(SimulationTaskQueue taskQueue, TaskScheduler taskScheduler) : SynchronizationContext
{
    public override void Post(SendOrPostCallback d, object? state)
    {
        ArgumentNullException.ThrowIfNull(d);
        var task = new Task(() => d(state));
        task.Start(taskScheduler);
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        ArgumentNullException.ThrowIfNull(d);
        // For Send, we execute synchronously
        d(state);
    }

    public override SynchronizationContext CreateCopy() => new SimulationSynchronizationContext(taskQueue, taskScheduler);

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
}
