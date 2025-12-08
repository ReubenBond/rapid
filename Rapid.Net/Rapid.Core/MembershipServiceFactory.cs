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
    MembershipViewAccessor viewAccessor,
    IOptions<RapidProtocolOptions> protocolOptions,
    ILogger<MembershipService> logger) : IMembershipServiceFactory
{
    public MembershipService CreateForNewCluster(
        Endpoint localEndpoint,
        NodeId nodeId,
        Metadata metadata,
        Dictionary<ClusterEvents, List<Action<ClusterStatusChange>>> subscriptions)
    {
        var opts = protocolOptions.Value;
        var membershipView = new MembershipViewBuilder(opts.RingCount, [nodeId], [localEndpoint]).Build();
        var cutDetector = new MultiNodeCutDetector(opts.RingCount, opts.HighWaterMark, opts.LowWaterMark);
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
            viewAccessor,
            metadataMap,
            subscriptions,
            logger);
    }

    public MembershipService CreateForJoin(
        Endpoint localEndpoint,
        IEnumerable<NodeId> nodeIds,
        IEnumerable<Endpoint> endpoints,
        Dictionary<Endpoint, Metadata> metadataMap,
        Dictionary<ClusterEvents, List<Action<ClusterStatusChange>>> subscriptions)
    {
        // Convert to collections as MembershipViewBuilder requires ICollection
        var nodeIdList = nodeIds.ToList();
        var endpointList = endpoints.ToList();

        var opts = protocolOptions.Value;
        var membershipView = new MembershipViewBuilder(opts.RingCount, nodeIdList, endpointList).Build();
        var cutDetector = new MultiNodeCutDetector(opts.RingCount, opts.HighWaterMark, opts.LowWaterMark);
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
            viewAccessor,
            metadataMap,
            subscriptions,
            logger);
    }
}
