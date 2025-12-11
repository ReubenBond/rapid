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
    /// <param name="metadata">The node's metadata.</param>
    /// <returns>A new MembershipService instance.</returns>
    MembershipService CreateForNewCluster(
        Endpoint localEndpoint,
        Metadata metadata);

    /// <summary>
    /// Creates a MembershipService instance for joining an existing cluster.
    /// </summary>
    /// <param name="localEndpoint">The local endpoint for this node.</param>
    /// <param name="configurationId">The configuration ID from the cluster's JoinResponse.</param>
    /// <param name="nodeIds">The list of node identifiers in the cluster.</param>
    /// <param name="endpoints">The list of endpoints in the cluster.</param>
    /// <param name="metadataMap">Metadata for all nodes in the cluster.</param>
    /// <returns>A new MembershipService instance.</returns>
    MembershipService CreateForJoin(
        Endpoint localEndpoint,
        long configurationId,
        IEnumerable<NodeId> nodeIds,
        IEnumerable<Endpoint> endpoints,
        Dictionary<Endpoint, Metadata> metadataMap);
}
