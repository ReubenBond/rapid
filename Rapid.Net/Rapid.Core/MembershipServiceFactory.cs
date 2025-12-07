using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Rapid.Messaging;
using Rapid.Monitoring;
using Rapid.Pb;

namespace Rapid;

/// <summary>
/// Default implementation of IMembershipServiceFactory.
/// Uses DI to obtain all dependencies and creates MembershipService instances with runtime data.
/// </summary>
internal sealed class MembershipServiceFactory(
    IMessagingClient messagingClient,
    IEdgeFailureDetectorFactory edgeFailureDetectorFactory,
    IBroadcasterFactory broadcasterFactory,
    IFastPaxosFactory fastPaxosFactory,
    SharedResources sharedResources,
    IOptions<RapidProtocolOptions> protocolOptions,
    ILoggerFactory loggerFactory) : IMembershipServiceFactory
{

    // Constants for cluster configuration
    private const int RingCount = 10;  // Number of rings
    private const int HighWaterMark = 9;   // High watermark
    private const int LowWaterMark = 4;   // Low watermark

    public MembershipService CreateForNewCluster(
        Endpoint localEndpoint,
        NodeId nodeId,
        Metadata metadata,
        Dictionary<ClusterEvents, List<Action<ClusterStatusChange>>> subscriptions)
    {
        var membershipView = new MutableMembershipView(RingCount, [nodeId], [localEndpoint]);
        var cutDetector = new MultiNodeCutDetector(RingCount, HighWaterMark, LowWaterMark);
        var metadataMap = new Dictionary<Endpoint, Metadata> { { localEndpoint, metadata } };
        var broadcaster = broadcasterFactory.Create();

        return new MembershipService(
            localEndpoint,
            cutDetector,
            membershipView,
            sharedResources,
            protocolOptions,
            messagingClient,
            broadcaster,
            edgeFailureDetectorFactory,
            fastPaxosFactory,
            metadataMap,
            subscriptions,
            loggerFactory);
    }

    public MembershipService CreateForJoin(
        Endpoint localEndpoint,
        IEnumerable<NodeId> nodeIds,
        IEnumerable<Endpoint> endpoints,
        Dictionary<Endpoint, Metadata> metadataMap,
        Dictionary<ClusterEvents, List<Action<ClusterStatusChange>>> subscriptions)
    {
        // Convert to collections as MutableMembershipView requires ICollection
        var nodeIdList = nodeIds.ToList();
        var endpointList = endpoints.ToList();

        var membershipView = new MutableMembershipView(RingCount, nodeIdList, endpointList);
        var cutDetector = new MultiNodeCutDetector(RingCount, HighWaterMark, LowWaterMark);
        var broadcaster = broadcasterFactory.Create();

        return new MembershipService(
            localEndpoint,
            cutDetector,
            membershipView,
            sharedResources,
            protocolOptions,
            messagingClient,
            broadcaster,
            edgeFailureDetectorFactory,
            fastPaxosFactory,
            metadataMap,
            subscriptions,
            loggerFactory);
    }
}
