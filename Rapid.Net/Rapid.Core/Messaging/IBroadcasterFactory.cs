using Rapid.Pb;

namespace Rapid.Messaging;

/// <summary>
/// Factory for creating IBroadcaster instances.
/// This exists to support DI patterns and avoid direct instantiation of broadcasters.
/// </summary>
public interface IBroadcasterFactory
{
    /// <summary>
    /// Creates a new broadcaster instance.
    /// </summary>
    /// <returns>A new IBroadcaster instance.</returns>
    IBroadcaster Create();
}

/// <summary>
/// Default factory implementation that creates UnicastToAllBroadcaster instances.
/// </summary>
internal sealed class UnicastToAllBroadcasterFactory : IBroadcasterFactory
{
    private readonly IMessagingClient _messagingClient;

    public UnicastToAllBroadcasterFactory(IMessagingClient messagingClient)
    {
        _messagingClient = messagingClient;
    }

    public IBroadcaster Create() => new UnicastToAllBroadcaster(_messagingClient);
}
