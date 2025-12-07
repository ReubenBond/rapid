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

    /// <summary>
    /// Gets the current immutable membership view.
    /// </summary>
    /// <returns>The current immutable MembershipView snapshot.</returns>
    MembershipView GetCurrentView();

    /// <summary>
    /// Subscribes to view changes, returning an async enumerable of subsequently decided views.
    /// The enumerable will yield a new MembershipView each time consensus is reached on a view change.
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the subscription.</param>
    /// <returns>An async enumerable of MembershipView instances.</returns>
    IAsyncEnumerable<MembershipView> SubscribeToViewChangesAsync(CancellationToken cancellationToken = default);
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

    public MembershipView GetCurrentView() => clusterService.MembershipService?.GetCurrentView() 
        ?? throw new InvalidOperationException("Membership service is not initialized");

    public async IAsyncEnumerable<MembershipView> SubscribeToViewChangesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (clusterService.MembershipService == null)
        {
            yield break;
        }

        await foreach (var view in clusterService.MembershipService.SubscribeToViewChangesAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return view;
        }
    }
}
