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
    /// Gets an enumerator that supports synchronous polling for simulation testing.
    /// The enumerator provides a <see cref="PollableEnumerator.TryGetNext"/> method
    /// that only advances when the next item is immediately available.
    /// </summary>
    public PollableEnumerator GetPollableEnumerator(CancellationToken cancellationToken = default)
        => new(_current, cancellationToken);

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

    /// <summary>
    /// An enumerator that supports synchronous polling for simulation testing.
    /// Unlike <see cref="IAsyncEnumerator{T}"/>, this enumerator never awaits asynchronously.
    /// It only advances when the next item is immediately available.
    /// </summary>
    public sealed class PollableEnumerator : IDisposable
    {
        private readonly CancellationToken _cancellationToken;
        private Element _current;
        private bool _stopped;

        internal PollableEnumerator(Element initial, CancellationToken cancellationToken)
        {
            _cancellationToken = cancellationToken;
            _current = initial;
        }

        /// <summary>
        /// Gets the current item. Only valid after <see cref="TryGetNext"/> returns true.
        /// </summary>
        public T Current => _current.Value;

        /// <summary>
        /// Returns true if the enumerator has been stopped (disposed or stream ended).
        /// </summary>
        public bool IsStopped => _stopped;

        /// <summary>
        /// Attempts to get the next item if one is immediately available.
        /// This method never blocks or awaits - it only returns true if the next
        /// element can be retrieved synchronously.
        /// </summary>
        /// <param name="item">The next item if available; otherwise default.</param>
        /// <returns>True if an item was retrieved; false if no item is ready or the stream has ended.</returns>
        public bool TryGetNext([MaybeNullWhen(false)] out T item)
        {
            item = default;

            if (_stopped || _cancellationToken.IsCancellationRequested)
            {
                _stopped = true;
                return false;
            }

            if (_current.IsDisposed)
            {
                _stopped = true;
                return false;
            }

            // Check if next element is available WITHOUT starting an async operation
            var nextTask = _current.NextAsync();
            if (!nextTask.IsCompletedSuccessfully)
            {
                // Next element not ready yet - return false without starting async await
                return false;
            }

            // Next element is ready - advance synchronously
#pragma warning disable CA1849 // Safe to access .Result since we've verified IsCompletedSuccessfully
            _current = nextTask.Result;
#pragma warning restore CA1849

            if (!_current.IsValid)
            {
                _stopped = true;
                return false;
            }

            item = _current.Value;
            return true;
        }

        /// <summary>
        /// Stops the enumerator.
        /// </summary>
        public void Dispose()
        {
            _stopped = true;
        }
    }

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

        public ValueTask<bool> MoveNextAsync()
        {
            if (_current.IsDisposed)
            {
                return ValueTask.FromResult(false);
            }

            var nextTask = _current.NextAsync();
            
            // Fast path: if next element is already available, complete synchronously.
            // This is critical for simulation tests where async continuations may not
            // be pumped by the custom scheduler.
            if (nextTask.IsCompletedSuccessfully)
            {
                // CA1849: Safe to access .Result since we've verified IsCompletedSuccessfully
#pragma warning disable CA1849
                _current = nextTask.Result;
#pragma warning restore CA1849
                return ValueTask.FromResult(_current.IsValid);
            }

            // Slow path: await asynchronously
            return MoveNextAsyncCore(nextTask);
        }

        private async ValueTask<bool> MoveNextAsyncCore(Task<Element> nextTask)
        {
            try
            {
                _current = await nextTask.WaitAsync(_cancellationToken).ConfigureAwait(false);
                return _current.IsValid;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    internal sealed class Element
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
