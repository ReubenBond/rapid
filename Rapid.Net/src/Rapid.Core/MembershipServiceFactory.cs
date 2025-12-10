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
    ICutDetectorFactory cutDetectorFactory,
    SharedResources sharedResources,
    MembershipViewAccessor viewAccessor,
    IOptions<RapidProtocolOptions> protocolOptions,
    ILogger<MembershipService> logger) : IMembershipServiceFactory
{
    public MembershipService CreateForNewCluster(
        Endpoint localEndpoint,
        NodeId nodeId,
        Metadata metadata)
    {
        var opts = protocolOptions.Value;
        
        // For a new cluster starting with 1 node, use configured K for rings
        // but cut detector needs effective values based on cluster size
        var membershipView = new MembershipViewBuilder(opts.ObserversPerSubject, [nodeId], [localEndpoint]).Build();
        
        // For a single-node cluster, the cut detector will be recreated when nodes join
        var cutDetector = cutDetectorFactory.Create(membershipView);
        
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
            cutDetectorFactory,
            viewAccessor,
            metadataMap,
            logger);
    }

    public MembershipService CreateForJoin(
        Endpoint localEndpoint,
        long configurationId,
        IEnumerable<NodeId> nodeIds,
        IEnumerable<Endpoint> endpoints,
        Dictionary<Endpoint, Metadata> metadataMap)
    {
        // Convert to collections as MembershipViewBuilder requires ICollection
        var nodeIdList = nodeIds.ToList();
        var endpointList = endpoints.ToList();
        var clusterSize = endpointList.Count;

        var opts = protocolOptions.Value;
        var membershipView = new MembershipViewBuilder(opts.ObserversPerSubject, nodeIdList, endpointList)
            .BuildWithConfigurationId(new ConfigurationId(configurationId));
        
        // Use cut detector factory to create detector based on actual cluster size
        var cutDetector = cutDetectorFactory.Create(membershipView);
        
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
            cutDetectorFactory,
            viewAccessor,
            metadataMap,
            logger);
    }
}
