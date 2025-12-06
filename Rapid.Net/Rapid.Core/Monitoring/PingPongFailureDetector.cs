using Microsoft.Extensions.Logging;
using Rapid.Messaging;
using Rapid.Pb;

namespace Rapid.Monitoring;

/// <summary>
/// Simple ping-pong failure detector factory.
/// </summary>
public sealed partial class PingPongFailureDetectorFactory(Endpoint localEndpoint, IMessagingClient client,
    SharedResources sharedResources, ILoggerFactory? loggerFactory) : IEdgeFailureDetectorFactory
{
    private readonly Endpoint _localEndpoint = localEndpoint;
    private readonly IMessagingClient _client = client;
    private readonly SharedResources _sharedResources = sharedResources;
    private readonly ILoggerFactory? _loggerFactory = loggerFactory;

    public IEdgeFailureDetector CreateInstance(Endpoint subject, Action notifier)
    {
        return new PingPongFailureDetector(subject, _localEndpoint, _client, _sharedResources, notifier, _loggerFactory);
    }

    private sealed partial class PingPongFailureDetector(Endpoint subject, Endpoint observer, IMessagingClient client,
        SharedResources sharedResources, Action notifier, ILoggerFactory? loggerFactory) : IEdgeFailureDetector
    {
        private readonly Endpoint _subject = subject;
        private readonly Endpoint _observer = observer;
        private readonly IMessagingClient _client = client;
#pragma warning disable CA2213 // SharedResources is owned by DI container, not disposed by this class
        private readonly SharedResources _sharedResources = sharedResources;
#pragma warning restore CA2213
        private readonly Action _notifier = notifier;
        private readonly ILogger _logger = loggerFactory?.CreateLogger<PingPongFailureDetector>()
                ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PingPongFailureDetector>.Instance;
        private readonly CancellationTokenSource _cts = new();

        private readonly struct LoggableEndpoint(Endpoint endpoint)
        {
            private readonly Endpoint _endpoint = endpoint;
            public override readonly string ToString() => RapidUtils.Loggable(_endpoint);
        }

        [LoggerMessage(Level = LogLevel.Warning, Message = "Probe failed for {Subject}")]
        private partial void LogProbeFailed(LoggableEndpoint Subject);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Probe exception for {Subject}")]
        private partial void LogProbeException(Exception ex, LoggableEndpoint Subject);
        
        private Task? _probeTask;

        public void Start()
        {
            _probeTask = ProbeAsync();
        }

        private async Task ProbeAsync()
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    await _sharedResources.TimeProvider.Delay(TimeSpan.FromSeconds(1), _cts.Token).ConfigureAwait(false);
                    await ProbeOnceAsync().ConfigureAwait(false);
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
                var response = await _client.SendMessageAsync(_subject, request, _cts.Token).ConfigureAwait(false);

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

        public void StopMonitoring()
        {
            _cts.Cancel();
        }

        public void Dispose()
        {
            StopMonitoring();
            _cts.Dispose();
            _client.Dispose();
        }
    }
}
