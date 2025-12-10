namespace Rapid.Tests.Simulation;

/// <summary>
/// A pull-based event consumer for simulation tests.
/// <para>
/// The simulation runs on a custom scheduler where async operations complete synchronously
/// when the simulation advances. This means standard async enumerable consumption patterns
/// (like <c>await foreach</c> or LINQ's <c>CountAsync</c>/<c>ToListAsync</c>) would block
/// indefinitely waiting for a stream that never completes on its own.
/// </para>
/// <para>
/// This class provides a polling-based approach: <see cref="TryGetNext"/> checks if an event
/// is synchronously available without blocking, allowing tests to drain all currently-buffered
/// events after simulation steps complete.
/// </para>
/// </summary>
/// <remarks>
/// Usage pattern:
/// <code>
/// var consumer = new SimulationEventConsumer(node.EventStream);
/// 
/// // Run simulation...
/// harness.WaitForConvergence(expectedSize: 3);
/// 
/// // Drain all events
/// while (consumer.TryGetNext() is { } notification)
/// {
///     // Process notification
/// }
/// </code>
/// </remarks>
public sealed class SimulationEventConsumer
{
    private readonly IAsyncEnumerator<ClusterEventNotification> _enumerator;
    private ValueTask<bool>? _pendingMoveNext;
    private bool _stopped;

    /// <summary>
    /// Creates a new event consumer for the given event stream.
    /// </summary>
    /// <param name="stream">The event stream to consume.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> is null.</exception>
    public SimulationEventConsumer(IAsyncEnumerable<ClusterEventNotification> stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _enumerator = stream.GetAsyncEnumerator();
    }

    /// <summary>
    /// Polls for the next event. Returns <c>null</c> if no event is ready yet or the stream has ended.
    /// <para>
    /// This method is non-blocking - it checks if a pending <c>MoveNextAsync</c> has completed
    /// synchronously and returns the current item if available.
    /// </para>
    /// </summary>
    /// <returns>The next notification if one is immediately available; otherwise <c>null</c>.</returns>
    public ClusterEventNotification? TryGetNext()
    {
        if (_stopped) return null;

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

        return null;
    }

    /// <summary>
    /// Stops the consumer. After calling this, <see cref="TryGetNext"/> will always return <c>null</c>.
    /// </summary>
    public void Stop() => _stopped = true;

    /// <summary>
    /// Returns <c>true</c> if the consumer has been stopped or the stream has ended.
    /// </summary>
    public bool IsStopped => _stopped;
}
