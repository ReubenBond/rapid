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
internal sealed class RapidCluster : IRapidCluster
{
    private readonly RapidClusterService _clusterService;

    public RapidCluster(RapidClusterService clusterService)
    {
        _clusterService = clusterService;
    }

    public IReadOnlyList<Endpoint> GetMemberlist() => _clusterService.MembershipService?.GetMembershipView() ?? [];

    public int GetMembershipSize() => _clusterService.MembershipService?.GetMembershipSize() ?? 0;

    public Dictionary<Endpoint, Metadata> GetClusterMetadata() => _clusterService.MembershipService?.GetMetadata() ?? [];

    public void RegisterSubscription(ClusterEvents eventType, Action<ClusterStatusChange> callback) => _clusterService.MembershipService?.RegisterSubscription(eventType, callback);

    public async Task LeaveGracefullyAsync()
    {
        if (_clusterService.MembershipService != null)
        {
            await _clusterService.MembershipService.LeaveAsync().ConfigureAwait(false);
        }
    }
}
