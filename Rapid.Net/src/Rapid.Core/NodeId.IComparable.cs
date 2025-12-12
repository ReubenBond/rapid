namespace Rapid.Pb;

/// <summary>
/// Implements IComparable for NodeId to enable consistent ordering across processes.
/// Compares by High bits first, then Low bits.
/// </summary>
public partial class NodeId : IComparable<NodeId>
{
    public int CompareTo(NodeId? other)
    {
        if (other is null) return 1;

        // Compare high bits first
        var highComparison = High.CompareTo(other.High);
        if (highComparison != 0) return highComparison;

        // Then compare low bits
        return Low.CompareTo(other.Low);
    }

    public static bool operator ==(NodeId? left, NodeId? right)
    {
        if (left is null) return right is null;
        return left.Equals(right);
    }

    public static bool operator !=(NodeId? left, NodeId? right) => !(left == right);

    public static bool operator <(NodeId? left, NodeId? right)
    {
        if (left is null) return right is not null;
        return left.CompareTo(right) < 0;
    }

    public static bool operator <=(NodeId? left, NodeId? right)
    {
        if (left is null) return true;
        return left.CompareTo(right) <= 0;
    }

    public static bool operator >(NodeId? left, NodeId? right)
    {
        if (left is null) return false;
        return left.CompareTo(right) > 0;
    }

    public static bool operator >=(NodeId? left, NodeId? right)
    {
        if (left is null) return right is null;
        return left.CompareTo(right) >= 0;
    }
}
