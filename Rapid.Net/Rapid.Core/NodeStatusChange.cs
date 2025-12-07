using Rapid.Pb;

namespace Rapid;

/// <summary>
/// Represents a single node status change event.
/// </summary>
public sealed class NodeStatusChange
{
    public Endpoint Endpoint { get; }
    public EdgeStatus Status { get; }
    public Metadata Metadata { get; }

    internal NodeStatusChange(Endpoint endpoint, EdgeStatus status, Metadata metadata)
    {
        Endpoint = endpoint;
        Status = status;
        Metadata = metadata;
    }

    public override string ToString() => $"{Endpoint.Hostname.ToStringUtf8()}:{Endpoint.Port}:{Status}:{Metadata}";
}
