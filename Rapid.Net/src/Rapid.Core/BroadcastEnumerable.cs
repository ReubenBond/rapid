using System.Diagnostics.CodeAnalysis;

namespace Rapid;

/// <summary>
/// A broadcast async enumerable that allows multiple subscribers to receive the same items.
/// Based on Orleans' AsyncEnumerable pattern using TaskCompletionSource chaining.
/// Each subscriber gets their own enumerator that starts from the current position
/// and receives all subsequent items.
/// </summary>
/// <typeparam name="T">The type of items to broadcast.</typeparam>
internal sealed class BroadcastEnumerable<T> : IAsyncEnumerable<T>, IDisposable
{
    private static readonly object InitialValue = new();
    private static readonly object DisposedValue = new();

    private readonly Lock _publishLock = new();
    private Element _current;

    public BroadcastEnumerable()
    {
        _current = Element.CreateInitial();
    }

    /// <summary>
    /// Publishes an item to all current and future subscribers.
    /// </summary>
    /// <param name="item">The item to publish.</param>
    /// <returns>True if published successfully, false if disposed.</returns>
    public bool TryPublish(T item)
    {
        if (_current.IsDisposed) return false;

        lock (_publishLock)
        {
            if (_current.IsDisposed) return false;

            var newElement = new Element(item);
            var prev = _current;
            _current = newElement;
            prev.SetNext(newElement);

            return true;
        }
    }

    /// <summary>
    /// Publishes an item to all current and future subscribers.
    /// Throws if the broadcaster has been disposed.
    /// </summary>
    /// <param name="item">The item to publish.</param>
    public void Publish(T item)
    {
        if (!TryPublish(item))
        {
            ThrowDisposed();
        }
    }

    /// <summary>
    /// Disposes the broadcaster, signaling to all subscribers that no more items will be published.
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
    private static void ThrowDisposed() => throw new ObjectDisposedException(nameof(BroadcastEnumerable<T>));

    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        => new Enumerator(_current, cancellationToken);

    private sealed class Enumerator : IAsyncEnumerator<T>
    {
        private readonly CancellationToken _cancellationToken;
        private Element _current;

        public Enumerator(Element initial, CancellationToken cancellationToken)
        {
            _cancellationToken = cancellationToken;
            _current = initial;
        }

        public T Current => _current.Value;

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
        private readonly object? _value;

        public Element(T value)
            : this(value, new TaskCompletionSource<Element>(TaskCreationOptions.RunContinuationsAsynchronously))
        {
        }

        private Element(object? value, TaskCompletionSource<Element> next)
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
            tcs.SetException(new ObjectDisposedException(nameof(BroadcastEnumerable<T>)));
            return new Element(DisposedValue, tcs);
        }

        public bool IsValid => !IsInitial && !IsDisposed;
        public bool IsInitial => ReferenceEquals(_value, InitialValue);
        public bool IsDisposed => ReferenceEquals(_value, DisposedValue);

        public T Value
        {
            get
            {
                if (!IsValid) throw new InvalidOperationException("Element does not have a valid value.");
                return (T)_value!;
            }
        }

        public Task<Element> NextAsync() => _next.Task;

        public void SetNext(Element next) => _next.SetResult(next);
    }
}
