using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Rapid;

/// <summary>
/// Default implementation of <see cref="IMembershipViewAccessor"/>.
/// Receives view updates from MembershipService and provides them to consumers.
/// </summary>
internal sealed class MembershipViewAccessor : IMembershipViewAccessor
{
    private readonly Channel<MembershipView> _viewChangeChannel;
    private readonly Lock _lock = new();

    /// <summary>
    /// Initializes a new MembershipViewAccessor.
    /// </summary>
    public MembershipViewAccessor()
    {
        _viewChangeChannel = Channel.CreateUnbounded<MembershipView>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = true
        });
    }

    /// <inheritdoc/>
    public MembershipView CurrentView { get; private set; } = MembershipView.Empty;

    /// <inheritdoc/>
    public async IAsyncEnumerable<MembershipView> ListenForViewUpdatesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var view in _viewChangeChannel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(true))
        {
            yield return view;
        }
    }

    /// <summary>
    /// Publishes a new view to all listeners. Called by MembershipService when consensus is reached.
    /// </summary>
    /// <param name="view">The new membership view.</param>
    internal void PublishView(MembershipView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        lock (_lock)
        {
            CurrentView = view;
        }

        _viewChangeChannel.Writer.TryWrite(view);
    }

    /// <summary>
    /// Completes the view change channel. Called during shutdown.
    /// </summary>
    internal void Complete()
    {
        _viewChangeChannel.Writer.TryComplete();
    }
}
