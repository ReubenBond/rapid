using Microsoft.Extensions.Logging;
using Rapid.Messaging;
using Rapid.Monitoring;
using Rapid.Pb;

namespace Rapid.Tests.Simulation;

/// <summary>
/// Factory for creating failure detectors in simulation mode.
/// Uses the same probe-based detection as PingPongFailureDetector but with simulation timing.
/// </summary>
internal sealed class SimulationFailureDetectorFactory(
    Endpoint localEndpoint,
    IMessagingClient client,
    SharedResources sharedResources,
    ILoggerFactory? loggerFactory) : IEdgeFailureDetectorFactory
{
    private readonly Endpoint _localEndpoint = localEndpoint;
    private readonly IMessagingClient _client = client;
    private readonly SharedResources _sharedResources = sharedResources;
    private readonly ILoggerFactory? _loggerFactory = loggerFactory;

    public IEdgeFailureDetector CreateInstance(Endpoint subject, Action notifier)
    {
        return new SimulationFailureDetector(
            subject,
            _localEndpoint,
            _client,
            _sharedResources,
            notifier,
            _loggerFactory);
    }
}

/// <summary>
/// Failure detector for simulation testing.
/// Uses the simulation's TimeProvider for timing control.
/// </summary>
internal sealed partial class SimulationFailureDetector : IEdgeFailureDetector
{
    private readonly Endpoint _subject;
    private readonly Endpoint _observer;
    private readonly IMessagingClient _client;
    private readonly SharedResources _sharedResources;
    private readonly Action _notifier;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private Task? _probeTask;

    private readonly struct LoggableEndpoint(Endpoint endpoint)
    {
        private readonly Endpoint _endpoint = endpoint;
        public override readonly string ToString() => RapidUtils.Loggable(_endpoint);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Simulation probe failed for {Subject}")]
    private partial void LogProbeFailed(LoggableEndpoint Subject);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Simulation probe exception for {Subject}")]
    private partial void LogProbeException(Exception ex, LoggableEndpoint Subject);

    public SimulationFailureDetector(
        Endpoint subject,
        Endpoint observer,
        IMessagingClient client,
        SharedResources sharedResources,
        Action notifier,
        ILoggerFactory? loggerFactory)
    {
        _subject = subject;
        _observer = observer;
        _client = client;
        _sharedResources = sharedResources;
        _notifier = notifier;
        _logger = loggerFactory?.CreateLogger<SimulationFailureDetector>()
            ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SimulationFailureDetector>.Instance;
    }

    public void Start() => _probeTask = ProbeAsync();

    private async Task ProbeAsync()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                // Use the TimeProvider from SharedResources for deterministic timing
                await Task.Delay(TimeSpan.FromSeconds(1), _sharedResources.TimeProvider, _cts.Token).ConfigureAwait(true);
                await ProbeOnceAsync().ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ProbeOnceAsync()
    {
#pragma warning disable CA1031
        try
        {
            var request = RapidUtils.ToRapidRequest(new ProbeMessage { Sender = _observer });
            var response = await _client.SendMessageAsync(_subject, request, _cts.Token).ConfigureAwait(true);

            if (response.ProbeResponse == null)
            {
                LogProbeFailed(new LoggableEndpoint(_subject));
                _notifier();
                StopMonitoring();
            }
        }
        catch (Exception ex)
        {
            LogProbeException(ex, new LoggableEndpoint(_subject));
            _notifier();
            StopMonitoring();
        }
#pragma warning restore CA1031
    }

    public void StopMonitoring() => _cts.Cancel();

    public void Dispose()
    {
        StopMonitoring();
        _cts.Dispose();
    }
}
