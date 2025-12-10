namespace Rapid.Tests.Simulation;

/// <summary>
/// A pull-based poller for async enumerables in simulation tests.
/// <para>
/// The simulation runs on a custom scheduler where async operations complete synchronously
/// when the simulation advances. This means standard async enumerable consumption patterns
/// (like <c>await foreach</c> or LINQ's <c>CountAsync</c>/<c>ToListAsync</c>) would block
/// indefinitely waiting for a stream that never completes on its own.
/// </para>
/// <para>
/// This class provides a polling-based approach: <see cref="Poll"/> checks if an item
/// is synchronously available without blocking, allowing tests to drain all currently-buffered
/// items after simulation steps complete.
/// </para>
/// </summary>
/// <typeparam name="T">The type of items in the async enumerable.</typeparam>
/// <remarks>
/// Usage pattern:
/// <code>
/// await using var poller = new AsyncEnumerablePoller&lt;ClusterEventNotification&gt;(node.EventStream);
/// 
/// // Run simulation...
/// harness.WaitForConvergence(expectedSize: 3);
/// 
/// // Drain all items
/// while (poller.Poll() is { } notification)
/// {
///     // Process notification
/// }
/// </code>
/// </remarks>
public sealed class AsyncEnumerablePoller<T> : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly IAsyncEnumerator<T> _enumerator;
    private ValueTask<bool>? _pendingMoveNext;
    private bool _stopped;

    /// <summary>
    /// Creates a new poller for the given async enumerable stream.
    /// </summary>
    /// <param name="stream">The async enumerable stream to poll.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> is null.</exception>
    public AsyncEnumerablePoller(IAsyncEnumerable<T> stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _enumerator = stream.GetAsyncEnumerator(_cts.Token);
    }

    /// <summary>
    /// Polls for the next item. Returns <c>default</c> if no item is ready yet or the stream has ended.
    /// <para>
    /// This method is non-blocking - it checks if a pending <c>MoveNextAsync</c> has completed
    /// synchronously and returns the current item if available.
    /// </para>
    /// </summary>
    /// <returns>The next item if one is immediately available; otherwise <c>default</c>.</returns>
    public T? Poll()
    {
        if (_stopped) return default;

        // Start a new MoveNextAsync if we don't have one pending
        _pendingMoveNext ??= _enumerator.MoveNextAsync();

        // Check if the move next has completed (synchronously available)
        if (_pendingMoveNext.Value.IsCompleted)
        {
            var hasValue = _pendingMoveNext.Value.Result;
            _pendingMoveNext = null;

            if (hasValue)
            {
                return _enumerator.Current;
            }

            _stopped = true;
        }

        return default;
    }

    /// <summary>
    /// Returns <c>true</c> if the poller has been stopped or the stream has ended.
    /// </summary>
    public bool IsStopped => _stopped;

    /// <summary>
    /// Disposes the poller, canceling any pending enumeration and releasing resources.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_stopped) return;
        _stopped = true;

        await _cts.CancelAsync().ConfigureAwait(false);
        await _enumerator.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
