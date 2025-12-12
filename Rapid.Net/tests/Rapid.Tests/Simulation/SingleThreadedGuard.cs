using System.Diagnostics;

namespace Rapid.Tests.Simulation;

/// <summary>
/// A debug guard that detects accidental concurrent access in single-threaded code.
/// Unlike a real lock, this throws immediately if concurrent access is detected
/// rather than blocking. Allows reentrant access by the same thread.
/// Use this for simulation code that must be single-threaded.
/// </summary>
internal sealed class SingleThreadedGuard
{
    private int _ownerThreadId;
    private int _entryCount;
    private string? _ownerStackTrace;

    /// <summary>
    /// Enters the guarded section. Throws if another thread is already inside.
    /// Allows reentrant access by the same thread.
    /// </summary>
    /// <returns>A disposable scope that exits the guard when disposed.</returns>
    public Scope Enter()
    {
        var currentThreadId = Environment.CurrentManagedThreadId;
        var existingOwner = Interlocked.CompareExchange(ref _ownerThreadId, currentThreadId, 0);

        if (existingOwner != 0 && existingOwner != currentThreadId)
        {
            var ownerStack = _ownerStackTrace ?? "(unknown)";
            throw new InvalidOperationException(
                $"Concurrent access detected in single-threaded simulation code. " +
                $"Thread {currentThreadId} attempted to enter while thread {existingOwner} is inside. " +
                $"This indicates a bug - simulation code must not be accessed concurrently.\n" +
                $"Owner thread stack trace:\n{ownerStack}");
        }

        if (_entryCount == 0)
        {
            _ownerStackTrace = new StackTrace(fNeedFileInfo: true).ToString();
        }

        Interlocked.Increment(ref _entryCount);
        return new Scope(this);
    }

    private void Exit()
    {
        if (Interlocked.Decrement(ref _entryCount) == 0)
        {
            Interlocked.Exchange(ref _ownerThreadId, 0);
        }
    }

    public readonly struct Scope(SingleThreadedGuard guard) : IDisposable
    {
        public void Dispose() => guard.Exit();
    }
}
