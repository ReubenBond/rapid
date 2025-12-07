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
internal sealed class MembershipServiceFactory : IMembershipServiceFactory
{
    private readonly IMessagingClient _messagingClient;
    private readonly IEdgeFailureDetectorFactory _edgeFailureDetectorFactory;
    private readonly IBroadcasterFactory _broadcasterFactory;
    private readonly SharedResources _sharedResources;
    private readonly IOptions<RapidProtocolOptions> _protocolOptions;
    private readonly ILoggerFactory _loggerFactory;

    // Constants for cluster configuration
    private const int K = 10;  // Number of rings
    private const int H = 9;   // High watermark
    private const int L = 4;   // Low watermark

    public MembershipServiceFactory(
        IMessagingClient messagingClient,
        IEdgeFailureDetectorFactory edgeFailureDetectorFactory,
        IBroadcasterFactory broadcasterFactory,
        SharedResources sharedResources,
        IOptions<RapidProtocolOptions> protocolOptions,
        ILoggerFactory loggerFactory)
    {
        _messagingClient = messagingClient;
        _edgeFailureDetectorFactory = edgeFailureDetectorFactory;
        _broadcasterFactory = broadcasterFactory;
        _sharedResources = sharedResources;
        _protocolOptions = protocolOptions;
        _loggerFactory = loggerFactory;
    }

    public MembershipService CreateForNewCluster(
        Endpoint localEndpoint,
        NodeId nodeId,
        Metadata metadata,
        Dictionary<ClusterEvents, List<Action<ClusterStatusChange>>> subscriptions)
    {
#pragma warning disable CA2000 // Dispose objects before losing scope - MembershipView ownership transferred to MembershipService
        var membershipView = new MembershipView(K, [nodeId], [localEndpoint]);
#pragma warning restore CA2000
        var cutDetector = new MultiNodeCutDetector(K, H, L);
        var metadataMap = new Dictionary<Endpoint, Metadata> { { localEndpoint, metadata } };
        var broadcaster = _broadcasterFactory.Create();

        return new MembershipService(
            localEndpoint,
            cutDetector,
            membershipView,
            _sharedResources,
            _protocolOptions,
            _messagingClient,
            broadcaster,
            _edgeFailureDetectorFactory,
            metadataMap,
            subscriptions,
            _loggerFactory);
    }

    public MembershipService CreateForJoin(
        Endpoint localEndpoint,
        IEnumerable<NodeId> nodeIds,
        IEnumerable<Endpoint> endpoints,
        Dictionary<Endpoint, Metadata> metadataMap,
        Dictionary<ClusterEvents, List<Action<ClusterStatusChange>>> subscriptions)
    {
#pragma warning disable CA2000 // Dispose objects before losing scope - MembershipView ownership transferred to MembershipService
        var membershipView = new MembershipView(K, nodeIds, endpoints);
#pragma warning restore CA2000
        var cutDetector = new MultiNodeCutDetector(K, H, L);
        var broadcaster = _broadcasterFactory.Create();

        return new MembershipService(
            localEndpoint,
            cutDetector,
            membershipView,
            _sharedResources,
            _protocolOptions,
            _messagingClient,
            broadcaster,
            _edgeFailureDetectorFactory,
            metadataMap,
            subscriptions,
            _loggerFactory);
    }
}
