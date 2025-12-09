using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Rapid.Exceptions;
using Rapid.Messaging;
using Rapid.Pb;

namespace Rapid;

/// <summary>
/// Hosted service that manages the Rapid cluster lifecycle.
/// </summary>
internal sealed partial class RapidClusterService(
    IOptions<RapidOptions> options,
    IMessagingClient messagingClient,
    IMembershipServiceFactory membershipServiceFactory,
    SharedResources sharedResources,
    ILogger<RapidClusterService> logger) : BackgroundService, IAsyncDisposable
{
    private readonly RapidOptions _options = options.Value;
    private readonly ILogger<RapidClusterService> _logger = logger;
    private int _disposed;

    [LoggerMessage(Level = LogLevel.Information, Message = "Starting Rapid cluster service on {ListenAddress}")]
    private partial void LogStarting(string ListenAddress);

    [LoggerMessage(Level = LogLevel.Information, Message = "Rapid cluster service started successfully")]
    private partial void LogStarted();

    [LoggerMessage(Level = LogLevel.Information, Message = "Stopping Rapid cluster service")]
    private partial void LogStopping();

    [LoggerMessage(Level = LogLevel.Error, Message = "Error in Rapid cluster service")]
    private partial void LogError(Exception ex);

    public MembershipService? MembershipService { get; private set; }

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
                await StartClusterAsync(stoppingToken).ConfigureAwait(true);
            }
            else
            {
                // Join an existing cluster
                await JoinClusterAsync(stoppingToken).ConfigureAwait(true);
            }

            LogStarted();

            // Keep running until cancellation
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(true);
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
        var currentIdentifier = RapidUtils.NodeIdFromUuid(sharedResources.NewGuid());

        MembershipService = membershipServiceFactory.CreateForNewCluster(
            _options.ListenAddress,
            currentIdentifier,
            _options.Metadata,
            _options.Subscriptions);
    }

    private async Task JoinClusterAsync(CancellationToken cancellationToken)
    {

        var currentIdentifier = RapidUtils.NodeIdFromUuid(sharedResources.NewGuid());

        // Phase 1: Contact seed for observers
        var preJoinMessage = new PreJoinMessage
        {
            Sender = _options.ListenAddress,
            NodeId = currentIdentifier
        };

        var preJoinResponse = await messagingClient.SendMessageAsync(
            _options.SeedAddress!,
            RapidUtils.ToRapidRequest(preJoinMessage),
            cancellationToken).ConfigureAwait(true);
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

            return await messagingClient.SendMessageAsync(
                entry.Key,
                RapidUtils.ToRapidRequest(joinMessageForObserver),
                cancellationToken).WithDefaultOnException().ConfigureAwait(true);
        });

        var responses = await Task.WhenAll(tasks).ConfigureAwait(true);
        var successfulResponse = responses.FirstOrDefault(r => r?.JoinResponse?.StatusCode == JoinStatusCode.SafeToJoin)?.JoinResponse;

        if (successfulResponse == null)
        {
            throw new JoinException("Failed to get successful response from any observer");
        }

        // Initialize membership from response
        var metadataMap = new Dictionary<Endpoint, Metadata>();
        for (var i = 0; i < successfulResponse.MetadataKeys.Count && i < successfulResponse.MetadataValues.Count; i++)
        {
            var endpoint = successfulResponse.MetadataKeys[i];
            var metadata = successfulResponse.MetadataValues[i];
            metadataMap[endpoint] = metadata;
        }

        MembershipService = membershipServiceFactory.CreateForJoin(
            _options.ListenAddress,
            successfulResponse.ConfigurationId,
            successfulResponse.Identifiers,
            successfulResponse.Endpoints,
            metadataMap,
            _options.Subscriptions);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        LogStopping();

        // Wait for background tasks to complete gracefully
        try
        {
            sharedResources.StartShutdown();
            await sharedResources.WaitForBackgroundTasksAsync(TimeSpan.FromSeconds(5), cancellationToken)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Expected if forced shutdown
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(true);
    }

    public override void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return; // Already disposed
        }

        if (MembershipService != null)
        {
            await MembershipService.DisposeAsync().ConfigureAwait(false);
        }

        base.Dispose();
    }
}
