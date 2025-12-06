/*
 * Copyright © 2016 - 2025 VMware, Inc. All Rights Reserved.
 */

using Microsoft.Extensions.Logging;
using Rapid.Messaging;
using Rapid.Pb;

namespace Rapid.Monitoring;

/// <summary>
/// Simple ping-pong failure detector factory.
/// </summary>
public sealed partial class PingPongFailureDetectorFactory(Endpoint localEndpoint, IMessagingClient client,
    ILoggerFactory? loggerFactory) : IEdgeFailureDetectorFactory
{
    private readonly Endpoint _localEndpoint = localEndpoint;
    private readonly IMessagingClient _client = client;
    private readonly ILoggerFactory? _loggerFactory = loggerFactory;

    public IEdgeFailureDetector CreateInstance(Endpoint subject, Action notifier)
    {
        return new PingPongFailureDetector(subject, _localEndpoint, _client, notifier, _loggerFactory);
    }

    private sealed partial class PingPongFailureDetector(Endpoint subject, Endpoint observer, IMessagingClient client,
        Action notifier, ILoggerFactory? loggerFactory) : IEdgeFailureDetector
    {
        private readonly Endpoint _subject = subject;
        private readonly Endpoint _observer = observer;
        private readonly IMessagingClient _client = client;
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
        private readonly PeriodicTimer _timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000));
        private Task? _probeTask;

        public void Start()
        {
            _probeTask = ProbeAsync();
        }

        private async Task ProbeAsync()
        {
            while (await _timer.WaitForNextTickAsync(_cts.Token))
            {
#pragma warning disable CA1031
                try
                {
                    var request = RapidUtils.ToRapidRequest(new ProbeMessage { Sender = _observer });
                    var response = await _client.SendMessageAsync(_subject, request, _cts.Token);

                    if (response.ProbeResponse == null)
                    {
                        LogProbeFailed(new LoggableEndpoint(_subject));
                        _notifier();
                        break;
                    }
                }
                catch (Exception ex)
                {
                    LogProbeException(ex, new LoggableEndpoint(_subject));
                    _notifier();
                    break;
                }
#pragma warning restore CA1031
            }
        }

        public void StopMonitoring()
        {
            _cts.Cancel();
        }

        public void Dispose()
        {
            StopMonitoring();
            _timer.Dispose();
            _cts.Dispose();
            _client.Dispose();
        }
    }
}
