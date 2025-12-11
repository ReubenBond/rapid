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
    IOptions<RapidProtocolOptions> protocolOptions,
    IMessagingClient messagingClient,
    IMembershipServiceFactory membershipServiceFactory,
    SharedResources sharedResources,
    ILogger<RapidClusterService> logger) : BackgroundService, IAsyncDisposable
{
    private readonly RapidOptions _options = options.Value;
    private readonly RapidProtocolOptions _protocolOptions = protocolOptions.Value;
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Join attempt {Attempt} failed: {Message}. Retrying in {DelayMs}ms")]
    private partial void LogJoinRetry(int Attempt, string Message, double DelayMs);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to join cluster after {Attempts} attempts")]
    private partial void LogJoinFailed(int Attempts);

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
                StartCluster();
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

    private void StartCluster()
    {
        MembershipService = membershipServiceFactory.CreateForNewCluster(
            _options.ListenAddress,
            _options.Metadata);
    }

    private async Task JoinClusterAsync(CancellationToken cancellationToken)
    {
        var maxRetries = _protocolOptions.GrpcDefaultRetries;
        var retryDelay = _protocolOptions.JoinRetryBaseDelay;
        JoinResponse? successfulResponse = null;

        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                successfulResponse = await TryJoinClusterAsync(cancellationToken).ConfigureAwait(true);
                if (successfulResponse != null)
                {
                    break;
                }
            }
            catch (Exception ex) when (attempt < maxRetries && IsRetryableJoinError(ex))
            {
                LogJoinRetry(attempt + 1, ex.Message, retryDelay.TotalMilliseconds);
                await Task.Delay(retryDelay, sharedResources.TimeProvider, cancellationToken).ConfigureAwait(true);
                retryDelay = TimeSpan.FromTicks((long)(retryDelay.Ticks * _protocolOptions.JoinRetryBackoffMultiplier));

                // Cap at maximum delay
                if (retryDelay > _protocolOptions.JoinRetryMaxDelay)
                {
                    retryDelay = _protocolOptions.JoinRetryMaxDelay;
                }
            }
        }

        if (successfulResponse == null)
        {
            LogJoinFailed(maxRetries + 1);
            throw new JoinException($"Failed to join cluster after {maxRetries + 1} attempts");
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
            metadataMap);
    }

    /// <summary>
    /// Attempts a single join operation. Generates a new NodeId and handles UUID collisions internally.
    /// </summary>
    private async Task<JoinResponse?> TryJoinClusterAsync(CancellationToken cancellationToken)
    {
        var currentIdentifier = RapidUtils.NodeIdFromUuid(sharedResources.NewGuid());

        // Phase 1: Contact seed for observers (with retry on UUID collision)
        JoinResponse joinResponse;
        while (true)
        {
            var preJoinMessage = new PreJoinMessage
            {
                Sender = _options.ListenAddress,
                NodeId = currentIdentifier
            };

            var preJoinResponse = await messagingClient.SendMessageAsync(
                _options.SeedAddress!,
                RapidUtils.ToRapidRequest(preJoinMessage),
                cancellationToken).ConfigureAwait(true);
            joinResponse = preJoinResponse.JoinResponse;

            if (joinResponse.StatusCode == JoinStatusCode.UuidAlreadyInRing)
            {
                // UUID collision - generate a new identifier and retry (matches Java behavior)
                currentIdentifier = RapidUtils.NodeIdFromUuid(sharedResources.NewGuid());
                continue;
            }

            break;
        }

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
            if (!ringNumbersPerObserver.TryGetValue(observer, out var value))
            {
                ringNumbersPerObserver[observer] = value = [];
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
            // Check if we got a ConfigChanged response - this means we should retry
            var configChangedResponse = responses.FirstOrDefault(r => r?.JoinResponse?.StatusCode == JoinStatusCode.ConfigChanged);
            if (configChangedResponse != null)
            {
                throw new JoinException("Configuration changed during join, retry needed");
            }

            throw new JoinException("Failed to get successful response from any observer");
        }

        return successfulResponse;
    }

    /// <summary>
    /// Determines if a join error is retryable (transient network issues vs permanent errors).
    /// </summary>
    private static bool IsRetryableJoinError(Exception ex)
    {
        // Timeouts and certain JoinExceptions are retryable
        if (ex is TimeoutException)
        {
            return true;
        }

        if (ex is JoinException)
        {
            var message = ex.Message;
            return message.Contains("Configuration changed", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("Failed to get successful response", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("Network partition", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("cannot reach", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("dropped", StringComparison.OrdinalIgnoreCase);
        }

        return false;
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
            await MembershipService.DisposeAsync().ConfigureAwait(true);
        }

        base.Dispose();
    }
}
