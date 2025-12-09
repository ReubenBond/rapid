using Rapid.Pb;

namespace Rapid;

/// <summary>
/// Factory for creating MembershipService instances.
/// This exists because MembershipService needs runtime data (MembershipView, configuration, etc.)
/// that is only available after cluster join/bootstrap.
/// </summary>
internal interface IMembershipServiceFactory
{
    /// <summary>
    /// Creates a MembershipService instance for starting a new cluster.
    /// </summary>
    /// <param name="localEndpoint">The local endpoint for this node.</param>
    /// <param name="nodeId">The unique node identifier.</param>
    /// <param name="metadata">The node's metadata.</param>
    /// <param name="subscriptions">Event subscriptions for cluster events.</param>
    /// <returns>A new MembershipService instance.</returns>
    MembershipService CreateForNewCluster(
        Endpoint localEndpoint,
        NodeId nodeId,
        Metadata metadata,
        Dictionary<ClusterEvents, List<Action<ClusterStatusChange>>> subscriptions);

    /// <summary>
    /// Creates a MembershipService instance for joining an existing cluster.
    /// </summary>
    /// <param name="localEndpoint">The local endpoint for this node.</param>
    /// <param name="configurationId">The configuration ID from the cluster's JoinResponse.</param>
    /// <param name="nodeIds">The list of node identifiers in the cluster.</param>
    /// <param name="endpoints">The list of endpoints in the cluster.</param>
    /// <param name="metadataMap">Metadata for all nodes in the cluster.</param>
    /// <param name="subscriptions">Event subscriptions for cluster events.</param>
    /// <returns>A new MembershipService instance.</returns>
    MembershipService CreateForJoin(
        Endpoint localEndpoint,
        long configurationId,
        IEnumerable<NodeId> nodeIds,
        IEnumerable<Endpoint> endpoints,
        Dictionary<Endpoint, Metadata> metadataMap,
        Dictionary<ClusterEvents, List<Action<ClusterStatusChange>>> subscriptions);
}
