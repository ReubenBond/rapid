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
    /// Gets the async enumerable for subscribing to cluster events.
    /// Each subscriber receives all events published after they start iterating.
    /// Uses the Orleans-style TaskCompletionSource chaining pattern for efficient broadcast.
    /// </summary>
    IAsyncEnumerable<ClusterEventNotification> EventStream { get; }

    /// <summary>
    /// Gracefully leaves the cluster.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    Task LeaveGracefullyAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the accessor for membership view information.
    /// This provides access to the current view and view change notifications.
    /// </summary>
    IMembershipViewAccessor ViewAccessor { get; }
}

/// <summary>
/// Implementation of IRapidCluster that delegates to the membership service and view accessor.
/// </summary>
internal sealed class RapidCluster(RapidClusterService clusterService, IMembershipViewAccessor viewAccessor) : IRapidCluster
{
    private static readonly IAsyncEnumerable<ClusterEventNotification> EmptyEventStream = CreateEmpty();

    public IReadOnlyList<Endpoint> GetMemberlist() => viewAccessor.CurrentView.Members;

    public int GetMembershipSize() => viewAccessor.CurrentView.Size;

    public Dictionary<Endpoint, Metadata> GetClusterMetadata() => clusterService.MembershipService?.GetMetadata() ?? [];

    public IAsyncEnumerable<ClusterEventNotification> EventStream
        => clusterService.MembershipService?.EventStream ?? EmptyEventStream;

    public async Task LeaveGracefullyAsync(CancellationToken cancellationToken = default)
    {
        if (clusterService.MembershipService != null)
        {
            await clusterService.MembershipService.LeaveAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    public IMembershipViewAccessor ViewAccessor => viewAccessor;

    private static async IAsyncEnumerable<ClusterEventNotification> CreateEmpty()
    {
        await Task.CompletedTask.ConfigureAwait(true);
        yield break;
    }
}
