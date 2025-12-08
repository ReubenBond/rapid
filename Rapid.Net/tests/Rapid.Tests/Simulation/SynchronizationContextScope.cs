namespace Rapid.Tests.Simulation;

/// <summary>
/// A disposable scope that restores the previous synchronization context when disposed.
/// </summary>
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
    public void Dispose() => SynchronizationContext.SetSynchronizationContext(_previous);
}
