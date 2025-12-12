namespace Rapid.Tests.Simulation.Infrastructure;

/// <summary>
/// A disposable scope that restores the previous synchronization context when disposed.
/// </summary>
internal readonly struct SynchronizationContextScope : IDisposable
{
    private readonly SynchronizationContext? _previous;
    private readonly bool _shouldRestore;

    /// <summary>
    /// Gets an empty scope that does nothing when disposed.
    /// </summary>
    public static SynchronizationContextScope Empty => default;

    /// <summary>
    /// Creates a new scope that will restore the specified context when disposed.
    /// </summary>
    /// <param name="previous">The synchronization context to restore.</param>
    internal SynchronizationContextScope(SynchronizationContext? previous)
    {
        _previous = previous;
        _shouldRestore = true;
    }

    /// <summary>
    /// Restores the previous synchronization context if this scope should restore.
    /// </summary>
    public void Dispose()
    {
        if (_shouldRestore)
        {
            SynchronizationContext.SetSynchronizationContext(_previous);
        }
    }
}
