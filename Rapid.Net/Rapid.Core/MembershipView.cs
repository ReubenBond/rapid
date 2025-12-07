using System.IO.Hashing;
using Rapid.Pb;

namespace Rapid;

/// <summary>
/// An immutable snapshot of the cluster membership at a point in time.
/// Instances are obtained through <see cref="IRapidCluster.GetCurrentView"/> or by subscribing
/// to view changes via <see cref="IRapidCluster.SubscribeToViewChangesAsync"/>.
/// </summary>
public sealed class MembershipView
{
    /// <summary>
    /// Initializes a new immutable MembershipView instance.
    /// </summary>
    /// <param name="k">Number of monitoring rings.</param>
    /// <param name="configurationId">The configuration identifier for this view.</param>
    /// <param name="members">The list of member endpoints.</param>
    /// <param name="nodeIds">The list of node identifiers seen.</param>
    internal MembershipView(int k, long configurationId, IReadOnlyList<Endpoint> members, IReadOnlyList<NodeId> nodeIds)
    {
        K = k;
        ConfigurationId = configurationId;
        Members = members;
        NodeIds = nodeIds;
    }

    /// <summary>
    /// Gets the number of monitoring rings (K value).
    /// </summary>
    public int K { get; }

    /// <summary>
    /// Gets the configuration identifier for this view.
    /// </summary>
    public long ConfigurationId { get; }

    /// <summary>
    /// Gets the list of member endpoints in the cluster.
    /// </summary>
    public IReadOnlyList<Endpoint> Members { get; }

    /// <summary>
    /// Gets the number of members in the cluster.
    /// </summary>
    public int Size => Members.Count;

    /// <summary>
    /// Gets the list of node identifiers that have been seen (including those that have left).
    /// </summary>
    public IReadOnlyList<NodeId> NodeIds { get; }

    /// <summary>
    /// Gets the configuration for this view, which can be used to bootstrap a new MutableMembershipView.
    /// </summary>
    public MembershipViewConfiguration Configuration => new(NodeIds, Members);

    /// <summary>
    /// Checks if a specific endpoint is a member of this view.
    /// </summary>
    /// <param name="endpoint">The endpoint to check.</param>
    /// <returns>True if the endpoint is a member, false otherwise.</returns>
    public bool IsMember(Endpoint endpoint) => Members.Contains(endpoint);
}

/// <summary>
/// The MembershipViewConfiguration object contains a list of nodes in the membership view as well as a list of UUIDs.
/// An instance of this object created from one MembershipView object contains the necessary information
/// to bootstrap an identical MutableMembershipView object.
/// </summary>
public sealed class MembershipViewConfiguration
{
    public MembershipViewConfiguration(IEnumerable<NodeId> nodeIds, IEnumerable<Endpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);
        ArgumentNullException.ThrowIfNull(endpoints);
        NodeIds = [.. nodeIds];
        Endpoints = [.. endpoints];
    }

    public IReadOnlyList<NodeId> NodeIds { get; }
    public IReadOnlyList<Endpoint> Endpoints { get; }

    /// <summary>
    /// Gets the configuration ID for the list of endpoints and identifiers.
    /// </summary>
    /// <returns>A configuration identifier.</returns>
    public long GetConfigurationId() => GetConfigurationId(NodeIds, Endpoints);

    public static long GetConfigurationId(IEnumerable<NodeId> identifiers, IEnumerable<Endpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(identifiers);
        ArgumentNullException.ThrowIfNull(endpoints);
        
        long hash = 1;
        foreach (var id in identifiers)
        {
            hash = hash * 37 + (long)XxHash64.HashToUInt64(BitConverter.GetBytes(id.High));
            hash = hash * 37 + (long)XxHash64.HashToUInt64(BitConverter.GetBytes(id.Low));
        }
        foreach (var endpoint in endpoints)
        {
            hash = hash * 37 + (long)XxHash64.HashToUInt64(endpoint.Hostname.Span);
            hash = hash * 37 + (long)XxHash64.HashToUInt64(BitConverter.GetBytes(endpoint.Port));
        }
        return hash;
    }
}
