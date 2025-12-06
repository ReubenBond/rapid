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
public sealed class PingPongFailureDetectorFactory(Endpoint localEndpoint, IMessagingClient client,
    ILoggerFactory? loggerFactory) : IEdgeFailureDetectorFactory
{
    private readonly Endpoint _localEndpoint = localEndpoint;
    private readonly IMessagingClient _client = client;
    private readonly ILoggerFactory? _loggerFactory = loggerFactory;

    public IEdgeFailureDetector CreateInstance(Endpoint subject, Action notifier)
    {
        return new PingPongFailureDetector(subject, _localEndpoint, _client, notifier, _loggerFactory);
    }

    private class PingPongFailureDetector(Endpoint subject, Endpoint observer, IMessagingClient client,
        Action notifier, ILoggerFactory? loggerFactory) : IEdgeFailureDetector
    {
        private readonly Endpoint _subject = subject;
        private readonly Endpoint _observer = observer;
        private readonly IMessagingClient _client = client;
        private readonly Action _notifier = notifier;
        private readonly ILogger _logger = loggerFactory?.CreateLogger<PingPongFailureDetector>()
                ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PingPongFailureDetector>.Instance;
        private readonly CancellationTokenSource _cts = new();
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
                try
                {
                    var request = Utils.ToRapidRequest(new ProbeMessage { Sender = _observer });
                    var response = await _client.SendMessageAsync(_subject, request, _cts.Token);
                    
                    if (response.ProbeResponse == null)
                    {
                        _logger.LogWarning("Probe failed for {Subject}", Utils.Loggable(_subject));
                        _notifier();
                        break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Probe exception for {Subject}", Utils.Loggable(_subject));
                    _notifier();
                    break;
                }
            }
        }

        public void Stop()
        {
            _cts.Cancel();
        }

        public void Dispose()
        {
            Stop();
            _timer.Dispose();
            _cts.Dispose();
        }
    }
}
