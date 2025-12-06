using Rapid.Pb;

namespace Rapid;

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

