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
/// This class provides a polling-based approach: <see cref="ConsumeCompleted"/> takes all synchronously
/// available items, allowing tests to drain all currently-buffered
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
    private Task<bool>? _pendingMoveNext;
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

    public IEnumerable<T> ConsumeCompleted()
    {
        while (TryConsume(out var item))
        {
            yield return item!;
        }
    }

    private bool TryConsume(out T? value)
    {
        if (_stopped)
        {
            value = default;
            return false;
        }

        // Start a new MoveNextAsync if we don't have one pending
        _pendingMoveNext ??= _enumerator.MoveNextAsync().AsTask();

        // Check if the move next has completed (synchronously available)
        if (_pendingMoveNext.IsCompleted)
        {
            var hasValue = _pendingMoveNext.Result;
            _pendingMoveNext = null;

            if (hasValue)
            {
                value = _enumerator.Current;
                return true;
            }

            _stopped = true;
        }

        value = default;
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        if (_stopped) return;
        _stopped = true;

        await _cts.CancelAsync().ConfigureAwait(true);
        
        // Observe any pending MoveNextAsync to prevent unobserved task exceptions.
        // The task may complete with cancellation or other exceptions after we cancel.
        if (_pendingMoveNext is not null)
        {
            try
            {
                await _pendingMoveNext.ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation token is triggered
            }
            catch (ObjectDisposedException)
            {
                // Expected when the underlying enumerable is disposed
            }
        }
        
        await _enumerator.DisposeAsync().ConfigureAwait(true);
        _cts.Dispose();
    }
}
