using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Rapid.Messaging;
using Rapid.Pb;

namespace Rapid;

/// <summary>
/// Hosted service that manages the Rapid cluster lifecycle.
/// </summary>
internal sealed partial class RapidClusterService : BackgroundService
{
    private readonly RapidOptions _options;
    private readonly IMessagingClient _messagingClient;
    private readonly IMembershipServiceFactory _membershipServiceFactory;
    private readonly ILogger<RapidClusterService> _logger;
    private readonly SharedResources _sharedResources;
    private MembershipService? _membershipService;

    [LoggerMessage(Level = LogLevel.Information, Message = "Starting Rapid cluster service on {ListenAddress}")]
    private partial void LogStarting(string ListenAddress);

    [LoggerMessage(Level = LogLevel.Information, Message = "Rapid cluster service started successfully")]
    private partial void LogStarted();

    [LoggerMessage(Level = LogLevel.Information, Message = "Stopping Rapid cluster service")]
    private partial void LogStopping();

    [LoggerMessage(Level = LogLevel.Error, Message = "Error in Rapid cluster service")]
    private partial void LogError(Exception ex);

    public RapidClusterService(
        IOptions<RapidOptions> options,
        IMessagingClient messagingClient,
        IMembershipServiceFactory membershipServiceFactory,
        SharedResources sharedResources,
        ILoggerFactory loggerFactory)
    {
        _options = options.Value;
        _messagingClient = messagingClient;
        _membershipServiceFactory = membershipServiceFactory;
        _sharedResources = sharedResources;
        _logger = loggerFactory.CreateLogger<RapidClusterService>();
    }

    public MembershipService? MembershipService => _membershipService;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var loggableAddress = RapidUtils.Loggable(_options.ListenAddress);
            LogStarting(loggableAddress);

            // Create the cluster based on configuration
            if (_options.SeedAddress == null || _options.ListenAddress.Equals(_options.SeedAddress))
            {
                // Start a new cluster
                await StartClusterAsync(stoppingToken).ConfigureAwait(false);
            }
            else
            {
                // Join an existing cluster
                await JoinClusterAsync(stoppingToken).ConfigureAwait(false);
            }

            LogStarted();

            // Keep running until cancellation
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }
        catch (Exception ex)
        {
            LogError(ex);
            throw;
        }
    }

    private async Task StartClusterAsync(CancellationToken cancellationToken)
    {
        var currentIdentifier = RapidUtils.NodeIdFromUuid(Guid.NewGuid());

        _membershipService = _membershipServiceFactory.CreateForNewCluster(
            _options.ListenAddress,
            currentIdentifier,
            _options.Metadata,
            _options.Subscriptions);
    }

    private async Task JoinClusterAsync(CancellationToken cancellationToken)
    {

        var currentIdentifier = RapidUtils.NodeIdFromUuid(Guid.NewGuid());

        // Phase 1: Contact seed for observers
        var preJoinMessage = new PreJoinMessage
        {
            Sender = _options.ListenAddress,
            NodeId = currentIdentifier
        };

        var preJoinResponse = await _messagingClient.SendMessageAsync(
            _options.SeedAddress!,
            RapidUtils.ToRapidRequest(preJoinMessage),
            cancellationToken).ConfigureAwait(false);
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
                Sender = _options.ListenAddress,
                NodeId = currentIdentifier,
                Metadata = _options.Metadata,
                ConfigurationId = joinResponse.ConfigurationId
            };
            joinMessageForObserver.RingNumber.AddRange(entry.Value);

            return await _messagingClient.SendMessageAsync(
                entry.Key,
                RapidUtils.ToRapidRequest(joinMessageForObserver),
                cancellationToken).WithDefaultOnException().ConfigureAwait(false);
        });

        var responses = await Task.WhenAll(tasks).ConfigureAwait(false);
        var successfulResponse = responses.FirstOrDefault(r => r?.JoinResponse?.StatusCode == JoinStatusCode.SafeToJoin)?.JoinResponse;

        if (successfulResponse == null)
        {
            throw new JoinException("Failed to get successful response from any observer");
        }

        // Initialize membership view from response
#pragma warning disable CA2000 // Dispose objects before losing scope - MembershipView ownership transferred to MembershipService
        var membershipView = new MembershipView(K, successfulResponse.Identifiers, successfulResponse.Endpoints);
#pragma warning restore CA2000
        var cutDetector = new MultiNodeCutDetector(K, H, L);

        var metadataMap = new Dictionary<Endpoint, Metadata>();
        for (var i = 0; i < successfulResponse.MetadataKeys.Count && i < successfulResponse.MetadataValues.Count; i++)
        {
            var endpoint = successfulResponse.MetadataKeys[i];
            var metadata = successfulResponse.MetadataValues[i];
            metadataMap[endpoint] = metadata;
        }

        _membershipService = new MembershipService(
            _options.ListenAddress,
            cutDetector,
            membershipView,
            _sharedResources,
            _protocolOptions,
            _messagingClient,
            _edgeFailureDetectorFactory,
            metadataMap,
            _options.Subscriptions,
            _loggerFactory);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        LogStopping();
        _membershipService?.Shutdown();

        // Wait for background tasks to complete gracefully
        try
        {
            await _sharedResources.WaitForBackgroundTasksAsync(TimeSpan.FromSeconds(5), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected if forced shutdown
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public override void Dispose()
    {
        _membershipService?.Dispose();
        _sharedResources.Dispose();
        base.Dispose();
    }
}
