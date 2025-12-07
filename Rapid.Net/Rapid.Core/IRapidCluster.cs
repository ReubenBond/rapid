using Rapid.Pb;

namespace Rapid;

/// <summary>
/// Provides access to the Rapid cluster membership information.
/// Inject this interface to interact with the cluster.
/// </summary>
public interface IRapidCluster
{
    /// <summary>
    /// Returns the list of endpoints currently in the membership set.
    /// </summary>
    IReadOnlyList<Endpoint> GetMemberlist();

    /// <summary>
    /// Returns the number of endpoints currently in the membership set.
    /// </summary>
    int GetMembershipSize();

    /// <summary>
    /// Returns cluster metadata.
    /// </summary>
    Dictionary<Endpoint, Metadata> GetClusterMetadata();

    /// <summary>
    /// Register callbacks for cluster events.
    /// </summary>
    void RegisterSubscription(ClusterEvents eventType, Action<ClusterStatusChange> callback);

    /// <summary>
    /// Gracefully leaves the cluster.
    /// </summary>
    Task LeaveGracefullyAsync();
}

/// <summary>
/// Implementation of IRapidCluster that delegates to the membership service.
/// </summary>
internal sealed class RapidCluster(RapidClusterService clusterService) : IRapidCluster
{
    public IReadOnlyList<Endpoint> GetMemberlist() => clusterService.MembershipService?.GetMembershipView() ?? [];

    public int GetMembershipSize() => clusterService.MembershipService?.GetMembershipSize() ?? 0;

    public Dictionary<Endpoint, Metadata> GetClusterMetadata() => clusterService.MembershipService?.GetMetadata() ?? [];

    public void RegisterSubscription(ClusterEvents eventType, Action<ClusterStatusChange> callback) => clusterService.MembershipService?.RegisterSubscription(eventType, callback);

    public async Task LeaveGracefullyAsync()
    {
        if (clusterService.MembershipService != null)
        {
            await clusterService.MembershipService.LeaveAsync().ConfigureAwait(false);
        }
    }
}
