using Rapid.Pb;

namespace Rapid;

/// <summary>
/// Comparer for individual Endpoint instances that delegates to Endpoint.CompareTo.
/// Used with SortedSet to ensure consistent ordering across nodes.
/// </summary>
internal sealed class EndpointComparer : IComparer<Endpoint>
{
    public static readonly EndpointComparer Instance = new();

    private EndpointComparer() { }

    public int Compare(Endpoint? x, Endpoint? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        return x.CompareTo(y);
    }
}

/// <summary>
/// Comparer for NodeId instances. Delegates to NodeId.CompareTo.
/// Used with SortedSet to ensure consistent ordering across nodes.
/// </summary>
internal sealed class NodeIdComparer : IComparer<NodeId>
{
    public static readonly NodeIdComparer Instance = new();

    private NodeIdComparer() { }

    public int Compare(NodeId? x, NodeId? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        return x.CompareTo(y);
    }
}

internal sealed class ListEndpointComparer : IEqualityComparer<List<Endpoint>>
{
    public static readonly ListEndpointComparer Instance = new();

    private ListEndpointComparer() { }

    public bool Equals(List<Endpoint>? x, List<Endpoint>? y)
    {
        if (x == null && y == null) return true;
        if (x == null || y == null) return false;
        return x.SequenceEqual(y);
    }

    public int GetHashCode(List<Endpoint> obj)
    {
        var hash = new HashCode();
        foreach (var endpoint in obj)
        {
            hash.Add(endpoint.GetHashCode());
        }
        return hash.ToHashCode();
    }
}

