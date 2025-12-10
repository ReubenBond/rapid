using System.Diagnostics.CodeAnalysis;

namespace Rapid;

/// <summary>
/// A broadcast async enumerable that allows multiple subscribers to receive the same events.
/// Based on Orleans' AsyncEnumerable pattern using TaskCompletionSource chaining.
/// Each subscriber gets their own enumerator that starts from the current position
/// and receives all subsequent events.
/// </summary>
internal sealed class ClusterEventBroadcaster : IAsyncEnumerable<ClusterEventNotification>, IDisposable
{
    private static readonly object InitialValue = new();
    private static readonly object DisposedValue = new();

    private readonly Lock _publishLock = new();
    private Element _current;

    public ClusterEventBroadcaster()
    {
        _current = Element.CreateInitial();
    }

    /// <summary>
    /// Publishes an event to all current and future subscribers.
    /// </summary>
    /// <param name="notification">The notification to publish.</param>
    /// <returns>True if published successfully, false if disposed.</returns>
    public bool TryPublish(ClusterEventNotification notification)
    {
        if (_current.IsDisposed) return false;

        lock (_publishLock)
        {
            if (_current.IsDisposed) return false;

            var newElement = new Element(notification);
            var prev = _current;
            _current = newElement;
            prev.SetNext(newElement);

            return true;
        }
    }

    /// <summary>
    /// Publishes an event to all current and future subscribers.
    /// Throws if the broadcaster has been disposed.
    /// </summary>
    /// <param name="notification">The notification to publish.</param>
    public void Publish(ClusterEventNotification notification)
    {
        if (!TryPublish(notification))
        {
            ThrowDisposed();
        }
    }

    /// <summary>
    /// Disposes the broadcaster, signaling to all subscribers that no more events will be published.
    /// </summary>
    public void Dispose()
    {
        if (_current.IsDisposed) return;

        lock (_publishLock)
        {
            if (_current.IsDisposed) return;

            var disposed = Element.CreateDisposed();
            var prev = _current;
            _current = disposed;
            prev.SetNext(disposed);
        }
    }

    [DoesNotReturn]
    private static void ThrowDisposed() => throw new ObjectDisposedException(nameof(ClusterEventBroadcaster));

    public IAsyncEnumerator<ClusterEventNotification> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        => new Enumerator(_current, cancellationToken);

    private sealed class Enumerator : IAsyncEnumerator<ClusterEventNotification>
    {
        private readonly CancellationToken _cancellationToken;
        private Element _current;

        public Enumerator(Element initial, CancellationToken cancellationToken)
        {
            _cancellationToken = cancellationToken;
            _current = initial;
        }

        public ClusterEventNotification Current => _current.Value;

        public async ValueTask<bool> MoveNextAsync()
        {
            if (_current.IsDisposed)
            {
                return false;
            }

            try
            {
                _current = await _current.NextAsync().WaitAsync(_cancellationToken).ConfigureAwait(false);
                return _current.IsValid;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Element
    {
        private readonly TaskCompletionSource<Element> _next;
        private readonly object _value;

        public Element(ClusterEventNotification value)
            : this(value, new TaskCompletionSource<Element>(TaskCreationOptions.RunContinuationsAsynchronously))
        {
        }

        private Element(object value, TaskCompletionSource<Element> next)
        {
            _value = value;
            _next = next;
        }

        public static Element CreateInitial() => new(
            InitialValue,
            new TaskCompletionSource<Element>(TaskCreationOptions.RunContinuationsAsynchronously));

        public static Element CreateDisposed()
        {
            var tcs = new TaskCompletionSource<Element>(TaskCreationOptions.RunContinuationsAsynchronously);
            tcs.SetException(new ObjectDisposedException(nameof(ClusterEventBroadcaster)));
            return new Element(DisposedValue, tcs);
        }

        public bool IsValid => !IsInitial && !IsDisposed;
        public bool IsInitial => ReferenceEquals(_value, InitialValue);
        public bool IsDisposed => ReferenceEquals(_value, DisposedValue);

        public ClusterEventNotification Value
        {
            get
            {
                if (!IsValid) throw new InvalidOperationException("Element does not have a valid value.");
                return (ClusterEventNotification)_value;
            }
        }

        public Task<Element> NextAsync() => _next.Task;

        public void SetNext(Element next) => _next.SetResult(next);
    }
}
