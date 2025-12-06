/*
 * Copyright © 2016 - 2025 VMware, Inc. All Rights Reserved.
 */

using System.Runtime.InteropServices;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rapid.Messaging;
using Rapid.Monitoring;
using Rapid.Pb;

namespace Rapid;

/// <summary>
/// The public API for Rapid. Users create Cluster objects using either StartAsync()
/// or JoinAsync(), depending on whether starting a new cluster or joining an existing one.
/// </summary>
public sealed class Cluster : IDisposable
{
    private const int K = 10;
    private const int H = 9;
    private const int L = 4;
    private readonly IMessagingServer _rpcServer;
    private readonly MembershipService? _membershipService;
    private readonly SharedResources _sharedResources;
    private readonly Endpoint _listenAddress;
    private readonly ILogger<Cluster> _logger;
    private bool _hasShutdown;

    private Cluster(IMessagingServer rpcServer, MembershipService? membershipService,
        SharedResources sharedResources, Endpoint listenAddress,
        ILoggerFactory? loggerFactory = null)
    {
        _rpcServer = rpcServer;
        _membershipService = membershipService;
        _sharedResources = sharedResources;
        _listenAddress = listenAddress;
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<Cluster>();
    }

    /// <summary>
    /// Returns the list of endpoints currently in the membership set.
    /// </summary>
    public List<Endpoint> GetMemberlist()
    {
        if (_hasShutdown)
            throw new InvalidOperationException("Can't access the memberlist after having shut down");

        return _membershipService?.GetMembershipView() ?? [];
    }

    /// <summary>
    /// Returns the number of endpoints currently in the membership set.
    /// </summary>
    public int GetMembershipSize()
    {
        if (_hasShutdown)
            throw new InvalidOperationException("Can't access the memberlist after having shut down");

        return _membershipService?.GetMembershipSize() ?? 0;
    }

    /// <summary>
    /// Returns cluster metadata.
    /// </summary>
    public Dictionary<Endpoint, Metadata> GetClusterMetadata()
    {
        if (_hasShutdown)
            throw new InvalidOperationException("Can't access metadata after having shut down");

        return _membershipService?.GetMetadata() ?? [];
    }

    /// <summary>
    /// Register callbacks for cluster events.
    /// </summary>
    public void RegisterSubscription(ClusterEvents eventType, Action<ClusterStatusChange> callback)
    {
        _membershipService?.RegisterSubscription(eventType, callback);
    }

    /// <summary>
    /// Gracefully leaves the cluster.
    /// </summary>
    public async Task LeaveGracefullyAsync()
    {
        _logger.LogDebug("Leaving the membership group and shutting down");
        if (_membershipService != null)
        {
            await _membershipService.LeaveAsync();
        }
        Shutdown();
    }

    /// <summary>
    /// Shuts down the cluster.
    /// </summary>
    public void Shutdown()
    {
        _logger.LogDebug("Shutting down RpcServer and MembershipService");
        _membershipService?.Shutdown();
        _rpcServer.Shutdown();
        _sharedResources.Dispose();
        _hasShutdown = true;
    }

    public void Dispose()
    {
        if (!_hasShutdown)
            Shutdown();
    }

    /// <summary>
    /// Builder for creating Cluster instances.
    /// </summary>
    public sealed class ClusterBuilder(Endpoint listenAddress)
    {
        private readonly Endpoint _listenAddress = listenAddress;
        private IEdgeFailureDetectorFactory? _edgeFailureDetector;
        private Metadata _metadata = new();
        private Settings _settings = new();
        private readonly Dictionary<ClusterEvents, List<Action<ClusterStatusChange>>> _subscriptions = [];
        private IMessagingClient? _messagingClient;
        private IMessagingServer? _messagingServer;
        private ILoggerFactory? _loggerFactory;

        public ClusterBuilder(string hostname, int port)
            : this(Utils.HostFromParts(hostname, port))
        {
        }

        public ClusterBuilder SetMetadata(Dictionary<string, ByteString> metadata)
        {
            _metadata = new Metadata();
            foreach (var kvp in metadata)
            {
                _metadata.Metadata_.Add(kvp.Key, kvp.Value);
            }
            return this;
        }

        public ClusterBuilder SetEdgeFailureDetectorFactory(IEdgeFailureDetectorFactory factory)
        {
            _edgeFailureDetector = factory;
            return this;
        }

        public ClusterBuilder AddSubscription(ClusterEvents eventType, Action<ClusterStatusChange> callback)
        {
            ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(_subscriptions, eventType, out var exists);
            entry ??= [];
            entry.Add(callback);
            return this;
        }

        public ClusterBuilder UseSettings(Settings settings)
        {
            _settings = settings;
            return this;
        }

        public ClusterBuilder UseLoggerFactory(ILoggerFactory loggerFactory)
        {
            _loggerFactory = loggerFactory;
            return this;
        }

        public ClusterBuilder SetMessagingClientAndServer(IMessagingClient client, IMessagingServer server)
        {
            _messagingClient = client;
            _messagingServer = server;
            return this;
        }

        /// <summary>
        /// Start a cluster without joining. Required to bootstrap a seed node.
        /// </summary>
        public async Task<Cluster> StartAsync()
        {
            var sharedResources = new SharedResources(_loggerFactory);
            var currentIdentifier = Utils.NodeIdFromUuid(Guid.NewGuid());

            // Create messaging infrastructure
            _messagingClient ??= new GrpcClient(_settings, _loggerFactory);
            _messagingServer ??= new GrpcServer(_listenAddress, sharedResources, _settings, _loggerFactory);

            // Create membership view with just this node
            var membershipView = new MembershipView(K, [currentIdentifier],
                                                    [_listenAddress]);

            // Create cut detector
            var cutDetector = new MultiNodeCutDetector(K, H, L);

            // Create failure detector factory if not provided
            _edgeFailureDetector ??= new PingPongFailureDetectorFactory(_listenAddress, _messagingClient, _loggerFactory);

            // Create metadata dictionary
            var metadataMap = new Dictionary<Endpoint, Metadata> { { _listenAddress, _metadata } };

            // Create membership service
            var membershipService = new MembershipService(_listenAddress, cutDetector, membershipView,
                                                         sharedResources, _settings, _messagingClient,
                                                         _edgeFailureDetector, metadataMap, _subscriptions,
                                                         _loggerFactory);

            // Wire up the server to handle messages
            ((GrpcServer)_messagingServer).SetMembershipService(membershipService);

            // Start server
            await _messagingServer.StartAsync();

            var cluster = new Cluster(_messagingServer, membershipService, sharedResources,
                                     _listenAddress, _loggerFactory);
            return cluster;
        }

        /// <summary>
        /// Joins an existing cluster using seedAddress to bootstrap.
        /// </summary>
        public async Task<Cluster> JoinAsync(Endpoint seedAddress)
        {
            var sharedResources = new SharedResources(_loggerFactory);
            var currentIdentifier = Utils.NodeIdFromUuid(Guid.NewGuid());

            // Create messaging infrastructure
            _messagingClient ??= new GrpcClient(_settings, _loggerFactory);
            _messagingServer ??= new GrpcServer(_listenAddress, sharedResources, _settings, _loggerFactory);

            // Start server first
            await _messagingServer.StartAsync();

            // Phase 1: Contact seed for observers
            var preJoinMessage = new PreJoinMessage
            {
                Sender = _listenAddress,
                NodeId = currentIdentifier
            };

            var preJoinResponse = await _messagingClient.SendMessageAsync(seedAddress,
                                                                         Utils.ToRapidRequest(preJoinMessage));
            var joinResponse = preJoinResponse.JoinResponse;

            if (joinResponse.StatusCode != JoinStatusCode.SafeToJoin &&
                joinResponse.StatusCode != JoinStatusCode.HostnameAlreadyInRing)
            {
                throw new JoinException($"Join failed with status: {joinResponse.StatusCode}");
            }

            var observers = joinResponse.Endpoints.ToList();
            if (observers.Count == 0)
            {
                throw new JoinException("No observers returned from seed");
            }

            // Phase 2: Contact observers
            // Determine ring numbers - batch together requests to the same node
            var ringNumbersPerObserver = new Dictionary<Endpoint, List<int>>();
            for (int ringNumber = 0; ringNumber < observers.Count; ringNumber++)
            {
                var observer = observers[ringNumber];
                if (!ringNumbersPerObserver.ContainsKey(observer))
                {
                    ringNumbersPerObserver[observer] = [];
                }
                ringNumbersPerObserver[observer].Add(ringNumber);
            }

            // Send join to all unique observers with their ring numbers
            var logger = _loggerFactory?.CreateLogger<ClusterBuilder>();
            var tasks = ringNumbersPerObserver.Select(async entry =>
            {
                var joinMessageForObserver = new JoinMessage
                {
                    Sender = _listenAddress,
                    NodeId = currentIdentifier,
                    Metadata = _metadata,
                    ConfigurationId = joinResponse.ConfigurationId
                };
                joinMessageForObserver.RingNumber.AddRange(entry.Value);

                try
                {
                    logger?.LogInformation("{Sender} is sending a join-p2 to {Observer} for config {ConfigId}",
                        Utils.Loggable(_listenAddress), Utils.Loggable(entry.Key), joinResponse.ConfigurationId);
                    return await _messagingClient.SendMessageAsync(entry.Key, Utils.ToRapidRequest(joinMessageForObserver));
                }
                catch
                {
                    return null;
                }
            });

            var responses = await Task.WhenAll(tasks);
            var successfulResponse = responses.FirstOrDefault(r => r?.JoinResponse?.StatusCode == JoinStatusCode.SafeToJoin)?.JoinResponse;

            if (successfulResponse == null)
            {
                throw new JoinException("Failed to get successful response from any observer");
            }

            // Initialize membership view from response
            var membershipView = new MembershipView(K, successfulResponse.Identifiers, successfulResponse.Endpoints);
            var cutDetector = new MultiNodeCutDetector(K, H, L);
            _edgeFailureDetector ??= new PingPongFailureDetectorFactory(_listenAddress, _messagingClient, _loggerFactory);

            // Build metadata map
            var metadataMap = new Dictionary<Endpoint, Metadata>();
            for (int i = 0; i < successfulResponse.MetadataKeys.Count && i < successfulResponse.MetadataValues.Count; i++)
            {
                var endpoint = successfulResponse.MetadataKeys[i];
                var metadata = successfulResponse.MetadataValues[i];
                metadataMap[endpoint] = metadata;
            }

            var membershipService = new MembershipService(_listenAddress, cutDetector, membershipView,
                                                         sharedResources, _settings, _messagingClient,
                                                         _edgeFailureDetector, metadataMap, _subscriptions,
                                                         _loggerFactory);

            ((GrpcServer)_messagingServer).SetMembershipService(membershipService);

            var cluster = new Cluster(_messagingServer, membershipService, sharedResources,
                                     _listenAddress, _loggerFactory);
            return cluster;
        }

        public Task<Cluster> JoinAsync(string hostname, int port)
        {
            return JoinAsync(Utils.HostFromParts(hostname, port));
        }
    }

    public class JoinException(string message) : Exception(message)
    {
        public JoinException()
        {
        }
    }
}
