using Google.Protobuf;
using Rapid.Messaging;
using Rapid.Pb;

namespace Rapid;

/// <summary>
/// Configuration options for Rapid cluster.
/// </summary>
public sealed class RapidOptions
{
    /// <summary>
    /// The endpoint this node listens on.
    /// </summary>
    public Endpoint ListenAddress { get; set; } = null!;

    /// <summary>
    /// The seed node endpoint to join. If null or equal to ListenAddress, starts a new cluster.
    /// </summary>
    public Endpoint? SeedAddress { get; set; }

    /// <summary>
    /// Metadata for this node.
    /// </summary>
    public Metadata Metadata { get; set; } = new();

    /// <summary>
    /// Event subscriptions.
    /// </summary>
    internal Dictionary<ClusterEvents, List<Action<ClusterStatusChange>>> Subscriptions { get; } = new();

    /// <summary>
    /// Sets metadata from a dictionary.
    /// </summary>
    public void SetMetadata(Dictionary<string, ByteString> metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        Metadata = new Metadata();
        foreach (var kvp in metadata)
        {
            Metadata.Metadata_.Add(kvp.Key, kvp.Value);
        }
    }

    /// <summary>
    /// Adds an event subscription.
    /// </summary>
    public void AddSubscription(ClusterEvents eventType, Action<ClusterStatusChange> callback)
    {
        if (!Subscriptions.ContainsKey(eventType))
        {
            Subscriptions[eventType] = [];
        }
        Subscriptions[eventType].Add(callback);
    }
}
