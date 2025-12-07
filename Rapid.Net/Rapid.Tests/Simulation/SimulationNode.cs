using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Rapid.Messaging;
using Rapid.Monitoring;
using Rapid.Pb;

namespace Rapid.Tests.Simulation;

/// <summary>
/// Represents a simulated node in a Rapid cluster.
/// Uses in-memory transport instead of gRPC and does not require a WebApplication.
/// </summary>
internal sealed class SimulationNode : IDisposable
{
    private readonly SimulationEnvironment _environment;
    private readonly SharedResources _sharedResources;
    private readonly SimulationFailureDetectorFactory _failureDetectorFactory;
    private readonly IFastPaxosFactory _fastPaxosFactory;
    private readonly MembershipViewAccessor _viewAccessor;
    private readonly IOptions<RapidProtocolOptions> _protocolOptions;
    private readonly ILogger<SimulationNode> _logger;
    private MembershipService? _membershipService;
    private bool _disposed;

    /// <summary>
    /// Gets the endpoint address of this node.
    /// </summary>
    public Endpoint Address { get; }

    /// <summary>
    /// Gets the deterministic random instance for this node.
    /// </summary>
    public DeterministicRandom Random { get; }

    /// <summary>
    /// Gets the current membership view of this node.
    /// </summary>
    public MembershipView CurrentView => _viewAccessor.CurrentView;

    /// <summary>
    /// Gets the membership view accessor for subscribing to view changes.
    /// </summary>
    public IMembershipViewAccessor ViewAccessor => _viewAccessor;

    /// <summary>
    /// Gets whether this node is initialized and part of a cluster.
    /// </summary>
    public bool IsInitialized => _membershipService != null;

    /// <summary>
    /// Gets the membership size of this node's view.
    /// </summary>
    public int MembershipSize => _viewAccessor.CurrentView.Size;

    /// <summary>
    /// Gets the messaging client for testing purposes.
    /// </summary>
    internal InMemoryMessagingClient MessagingClient { get; }

    private SimulationNode(
        SimulationEnvironment environment,
        Endpoint address,
        RapidProtocolOptions? protocolOptions,
        ILoggerFactory? loggerFactory)
    {
        _environment = environment;
        Address = address;
        Random = environment.CreateDerivedRandom();

        var logger = loggerFactory ?? environment.LoggerFactory;
        _logger = logger?.CreateLogger<SimulationNode>()
            ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SimulationNode>.Instance;

        // Create protocol options
        var options = protocolOptions ?? new RapidProtocolOptions();
        _protocolOptions = Options.Create(options);

        // Create shared resources with the simulation's time provider
        _sharedResources = new SharedResources(logger, environment.TimeProvider);

        // Create in-memory messaging client
        MessagingClient = new InMemoryMessagingClient(environment, address);

        // Create view accessor
        _viewAccessor = new MembershipViewAccessor();

        // Create failure detector factory
        _failureDetectorFactory = new SimulationFailureDetectorFactory(
            address,
            MessagingClient,
            _sharedResources,
            logger);

        // Create fast paxos factory
        _fastPaxosFactory = new FastPaxosFactory(
            MessagingClient,
            _protocolOptions,
            _sharedResources,
            logger ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

        // Register with the simulation environment
        environment.RegisterNode(this);
    }

    /// <summary>
    /// Creates a new simulation node.
    /// </summary>
    public static SimulationNode Create(
        SimulationEnvironment environment,
        string hostname,
        int port,
        RapidProtocolOptions? protocolOptions = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var address = RapidUtils.HostFromParts(hostname, port);
        return new SimulationNode(environment, address, protocolOptions, loggerFactory);
    }

    /// <summary>
    /// Creates a new simulation node with a numeric identifier.
    /// </summary>
    public static SimulationNode Create(
        SimulationEnvironment environment,
        int nodeId,
        RapidProtocolOptions? protocolOptions = null,
        ILoggerFactory? loggerFactory = null) => Create(environment, "node", nodeId, protocolOptions, loggerFactory);

    /// <summary>
    /// Starts this node as a new single-node cluster (seed node).
    /// </summary>
    public void StartCluster(Metadata? metadata = null)
    {
        if (_membershipService != null)
        {
            throw new InvalidOperationException("Node is already initialized");
        }

        var nodeId = RapidUtils.NodeIdFromUuid(CreateDeterministicGuid());
        var actualMetadata = metadata ?? new Metadata();

        var opts = _protocolOptions.Value;
        var membershipView = new MembershipViewBuilder(opts.RingCount, [nodeId], [Address]).Build();
        var cutDetector = new MultiNodeCutDetector(opts.RingCount, opts.HighWaterMark, opts.LowWaterMark);
        var metadataMap = new Dictionary<Endpoint, Metadata> { { Address, actualMetadata } };
        var broadcaster = new UnicastToAllBroadcaster(MessagingClient);
        Dictionary<ClusterEvents, List<Action<ClusterStatusChange>>> subscriptions = [];

        _membershipService = new MembershipService(
            Address,
            cutDetector,
            membershipView,
            _sharedResources,
            _protocolOptions,
            MessagingClient,
            broadcaster,
            _failureDetectorFactory,
            _fastPaxosFactory,
            _viewAccessor,
            metadataMap,
            subscriptions,
            _environment.LoggerFactory);
    }

    /// <summary>
    /// Joins this node to an existing cluster through the specified seed node.
    /// </summary>
    public async Task JoinClusterAsync(SimulationNode seedNode, Metadata? metadata = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(seedNode);

        if (_membershipService != null)
        {
            throw new InvalidOperationException("Node is already initialized");
        }

        var nodeId = RapidUtils.NodeIdFromUuid(CreateDeterministicGuid());
        var actualMetadata = metadata ?? new Metadata();

        // Phase 1: Contact seed for observers
        var preJoinMessage = new PreJoinMessage
        {
            Sender = Address,
            NodeId = nodeId
        };

        var preJoinResponse = await MessagingClient.SendMessageAsync(
            seedNode.Address,
            RapidUtils.ToRapidRequest(preJoinMessage),
            cancellationToken).ConfigureAwait(true);

        var joinResponse = preJoinResponse.JoinResponse;

        if (joinResponse.StatusCode != JoinStatusCode.SafeToJoin &&
            joinResponse.StatusCode != JoinStatusCode.HostnameAlreadyInRing)
        {
            throw new InvalidOperationException($"Join failed with status: {joinResponse.StatusCode}");
        }

        var observers = joinResponse.Endpoints.ToList();
        if (observers.Count == 0)
        {
            throw new InvalidOperationException("No observers returned from seed");
        }

        // Phase 2: Contact observers
        var ringNumbersPerObserver = new Dictionary<Endpoint, List<int>>();
        for (var ringNumber = 0; ringNumber < observers.Count; ringNumber++)
        {
            var observer = observers[ringNumber];
            if (!ringNumbersPerObserver.ContainsKey(observer))
            {
                ringNumbersPerObserver[observer] = [];
            }
            ringNumbersPerObserver[observer].Add(ringNumber);
        }

        var tasks = ringNumbersPerObserver.Select(async entry =>
        {
            var joinMessageForObserver = new JoinMessage
            {
                Sender = Address,
                NodeId = nodeId,
                Metadata = actualMetadata,
                ConfigurationId = joinResponse.ConfigurationId
            };
            joinMessageForObserver.RingNumber.AddRange(entry.Value);

            return await MessagingClient.SendMessageAsync(
                entry.Key,
                RapidUtils.ToRapidRequest(joinMessageForObserver),
                cancellationToken).ConfigureAwait(true);
        });

        var responses = await Task.WhenAll(tasks).ConfigureAwait(true);
        var successfulResponse = responses.FirstOrDefault(r => r?.JoinResponse?.StatusCode == JoinStatusCode.SafeToJoin)?.JoinResponse;

        if (successfulResponse == null)
        {
            throw new InvalidOperationException("Failed to get successful response from any observer");
        }

        // Initialize membership from response
        var metadataMap = new Dictionary<Endpoint, Metadata>();
        for (var i = 0; i < successfulResponse.MetadataKeys.Count && i < successfulResponse.MetadataValues.Count; i++)
        {
            var endpoint = successfulResponse.MetadataKeys[i];
            var m = successfulResponse.MetadataValues[i];
            metadataMap[endpoint] = m;
        }

        var opts = _protocolOptions.Value;
        var membershipView = new MembershipViewBuilder(
            opts.RingCount,
            successfulResponse.Identifiers.ToList(),
            successfulResponse.Endpoints.ToList()).Build();
        var cutDetector = new MultiNodeCutDetector(opts.RingCount, opts.HighWaterMark, opts.LowWaterMark);
        var broadcaster = new UnicastToAllBroadcaster(MessagingClient);
        Dictionary<ClusterEvents, List<Action<ClusterStatusChange>>> subscriptions = [];

        _membershipService = new MembershipService(
            Address,
            cutDetector,
            membershipView,
            _sharedResources,
            _protocolOptions,
            MessagingClient,
            broadcaster,
            _failureDetectorFactory,
            _fastPaxosFactory,
            _viewAccessor,
            metadataMap,
            subscriptions,
            _environment.LoggerFactory);
    }

    /// <summary>
    /// Handles an incoming request from another node.
    /// </summary>
    internal Task<RapidResponse> HandleRequestAsync(RapidRequest request, CancellationToken cancellationToken)
    {
        if (_membershipService == null)
        {
            throw new InvalidOperationException("Node is not initialized");
        }

        return _membershipService.HandleMessageAsync(request, cancellationToken);
    }

    /// <summary>
    /// Registers a subscription for cluster events.
    /// </summary>
    public void RegisterSubscription(ClusterEvents eventType, Action<ClusterStatusChange> callback) => _membershipService?.RegisterSubscription(eventType, callback);

    /// <summary>
    /// Gracefully leaves the cluster.
    /// </summary>
    public async Task LeaveAsync()
    {
        if (_membershipService != null)
        {
            await _membershipService.LeaveAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Shuts down the node.
    /// </summary>
    public void Shutdown() => _membershipService?.Shutdown();

    /// <summary>
    /// Creates a deterministic GUID using the node's random instance.
    /// </summary>
    private Guid CreateDeterministicGuid()
    {
        var bytes = new byte[16];
        Random.NextBytes(bytes);
        return new Guid(bytes);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _membershipService?.Shutdown();
        _membershipService?.Dispose();
        _sharedResources.Dispose();
        MessagingClient.Dispose();
        _environment.UnregisterNode(this);
    }
}
