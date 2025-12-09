using Rapid.Pb;

namespace Rapid;

/// <summary>
/// Represents a cluster membership status change.
/// </summary>
public sealed class ClusterStatusChange(
    ConfigurationId configurationId,
    IReadOnlyList<Endpoint> membership,
    IReadOnlyList<NodeStatusChange> delta)
{
    public ConfigurationId ConfigurationId { get; } = configurationId;
    public IReadOnlyList<Endpoint> Membership { get; } = membership;
    public IReadOnlyList<NodeStatusChange> Delta { get; } = delta;

    public override string ToString()
    {
        return $"ClusterStatusChange{{configurationId={ConfigurationId}, " +
               $"membership={string.Join(",", Membership)}, delta={string.Join(",", Delta)}}}";
    }
}
