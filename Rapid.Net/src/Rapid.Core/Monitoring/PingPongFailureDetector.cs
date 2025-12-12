using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Rapid.Messaging;
using Rapid.Pb;

namespace Rapid.Monitoring;

/// <summary>
/// Simple ping-pong failure detector factory.
/// </summary>
public sealed partial class PingPongFailureDetectorFactory(
    Endpoint localEndpoint,
    IMessagingClient client,
    SharedResources sharedResources,
    IOptions<RapidProtocolOptions> protocolOptions,
    ILogger<PingPongFailureDetector> logger) : IEdgeFailureDetectorFactory
{
    private readonly Endpoint _localEndpoint = localEndpoint;
    private readonly IMessagingClient _client = client;
    private readonly SharedResources _sharedResources = sharedResources;
    private readonly RapidProtocolOptions _protocolOptions = protocolOptions.Value;
    private readonly ILogger<PingPongFailureDetector> _logger = logger;

    /// <summary>
    /// Gets or sets the callback invoked when a probe response indicates
    /// this node has been kicked from the cluster (not in remote's membership view).
    /// The parameter is the remote configuration ID.
    /// </summary>
    public Action<long>? OnKickedDetected { get; set; }

    public IEdgeFailureDetector CreateInstance(Endpoint subject, Action notifier) =>
        new PingPongFailureDetector(
            subject,
            _localEndpoint,
            _client,
            _sharedResources,
            notifier,
            _protocolOptions.FailureDetectorConsecutiveFailures,
            OnKickedDetected,
            _logger);
}

/// <summary>
/// Simple ping-pong failure detector that probes a subject endpoint.
/// Requires multiple consecutive probe failures before declaring a node down.
/// </summary>
public sealed partial class PingPongFailureDetector : IEdgeFailureDetector
{
    private readonly Endpoint _subject;
    private readonly Endpoint _observer;
#pragma warning disable CA2213 // SharedResources is owned by DI container, not disposed by this class
    private readonly IMessagingClient _client;
    private readonly SharedResources _sharedResources;
#pragma warning restore CA2213
    private readonly Action _notifier;
    private readonly int _consecutiveFailuresThreshold;
    private readonly Action<long>? _onKickedDetected;
    private readonly ILogger<PingPongFailureDetector> _logger;
    private readonly CancellationTokenSource _cts = new();
    private int _disposed;
    private Task? _probeTask;
    private int _consecutiveFailures;

    /// <summary>
    /// Creates a new ping-pong failure detector.
    /// </summary>
    /// <param name="subject">The endpoint to monitor.</param>
    /// <param name="observer">The local endpoint (observer).</param>
    /// <param name="client">The messaging client for sending probes.</param>
    /// <param name="sharedResources">Shared resources including TimeProvider.</param>
    /// <param name="notifier">Action to invoke when the subject is detected as failed.</param>
    /// <param name="consecutiveFailuresThreshold">Number of consecutive failures required before declaring node down.</param>
    /// <param name="onKickedDetected">Optional callback when kicked from cluster is detected.</param>
    /// <param name="logger">Optional logger.</param>
    public PingPongFailureDetector(
        Endpoint subject,
        Endpoint observer,
        IMessagingClient client,
        SharedResources sharedResources,
        Action notifier,
        int consecutiveFailuresThreshold = 3,
        Action<long>? onKickedDetected = null,
        ILogger<PingPongFailureDetector>? logger = null)
    {
        _subject = subject;
        _observer = observer;
        _client = client;
        _sharedResources = sharedResources;
        _notifier = notifier;
        _consecutiveFailuresThreshold = consecutiveFailuresThreshold;
        _onKickedDetected = onKickedDetected;
        _logger = logger ?? NullLogger<PingPongFailureDetector>.Instance;
    }

    private readonly struct LoggableEndpoint(Endpoint endpoint)
    {
        private readonly Endpoint _endpoint = endpoint;
        public override readonly string ToString() => RapidUtils.Loggable(_endpoint);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Probe failed for {Subject} (consecutive failures: {ConsecutiveFailures}/{Threshold})")]
    private partial void LogProbeFailed(LoggableEndpoint Subject, int ConsecutiveFailures, int Threshold);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Probe exception for {Subject} (consecutive failures: {ConsecutiveFailures}/{Threshold})")]
    private partial void LogProbeException(Exception ex, LoggableEndpoint Subject, int ConsecutiveFailures, int Threshold);

    [LoggerMessage(Level = LogLevel.Information, Message = "Node {Subject} declared down after {ConsecutiveFailures} consecutive probe failures")]
    private partial void LogNodeDeclaredDown(LoggableEndpoint Subject, int ConsecutiveFailures);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Probe succeeded for {Subject}, resetting consecutive failure count")]
    private partial void LogProbeSucceeded(LoggableEndpoint Subject);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Kicked from cluster detected: not in {Subject}'s membership view (remote config: {RemoteConfigId})")]
    private partial void LogKickedDetected(LoggableEndpoint Subject, long RemoteConfigId);

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed == 1, this);
        _probeTask = ProbeAsync();
    }

    private async Task ProbeAsync()
    {
        while (_disposed == 0)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), _sharedResources.TimeProvider, _cts.Token).ConfigureAwait(true);
            await ProbeOnceAsync().ConfigureAwait(true);
        }
    }

    private async Task ProbeOnceAsync()
    {
        // Check disposed state without throwing - just return if disposed
        if (_disposed == 1)
        {
            return;
        }
#pragma warning disable CA1031
        try
        {
            var request = RapidUtils.ToRapidRequest(new ProbeMessage { Sender = _observer });
            var response = await _client.SendMessageAsync(_subject, request, _cts.Token).ConfigureAwait(true);

            if (response.ProbeResponse == null)
            {
                _consecutiveFailures++;
                LogProbeFailed(new LoggableEndpoint(_subject), _consecutiveFailures, _consecutiveFailuresThreshold);
                CheckAndNotifyFailure();
            }
            else
            {
                if (_consecutiveFailures > 0)
                {
                    LogProbeSucceeded(new LoggableEndpoint(_subject));
                }
                _consecutiveFailures = 0;

                // Check if we've been kicked from the cluster
                CheckForKicked(response.ProbeResponse);
            }
        }
        catch (Exception ex)
        {
            _consecutiveFailures++;
            LogProbeException(ex, new LoggableEndpoint(_subject), _consecutiveFailures, _consecutiveFailuresThreshold);
            CheckAndNotifyFailure();
        }
#pragma warning restore CA1031
    }

    private void CheckForKicked(ProbeResponse probeResponse)
    {
        if (_onKickedDetected == null)
        {
            return;
        }

        // If the remote node says we're not in their membership, we've been kicked
        if (!probeResponse.SenderInMembership)
        {
            LogKickedDetected(new LoggableEndpoint(_subject), probeResponse.ConfigurationId);
            _onKickedDetected(probeResponse.ConfigurationId);
        }
    }

    private void CheckAndNotifyFailure()
    {
        if (_consecutiveFailures >= _consecutiveFailuresThreshold)
        {
            LogNodeDeclaredDown(new LoggableEndpoint(_subject), _consecutiveFailures);
            _notifier();
            Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return; // Already disposed
        }

        // Cancel the token before disposing to stop the probe loop gracefully
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // CTS was already disposed, which is fine
        }

        _probeTask?.Ignore();
        _cts.Dispose();
    }
}
