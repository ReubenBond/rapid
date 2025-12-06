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
public sealed class PingPongFailureDetectorFactory : IEdgeFailureDetectorFactory
{
    private readonly Endpoint _localEndpoint;
    private readonly IMessagingClient _client;
    private readonly ILoggerFactory? _loggerFactory;

    public PingPongFailureDetectorFactory(Endpoint localEndpoint, IMessagingClient client, 
        ILoggerFactory? loggerFactory)
    {
        _localEndpoint = localEndpoint;
        _client = client;
        _loggerFactory = loggerFactory;
    }

    public IEdgeFailureDetector CreateInstance(Endpoint subject, Action notifier)
    {
        return new PingPongFailureDetector(subject, _localEndpoint, _client, notifier, _loggerFactory);
    }

    private class PingPongFailureDetector : IEdgeFailureDetector
    {
        private readonly Endpoint _subject;
        private readonly Endpoint _observer;
        private readonly IMessagingClient _client;
        private readonly Action _notifier;
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _cts = new();
        private readonly PeriodicTimer _timer;
        private Task? _probeTask;

        public PingPongFailureDetector(Endpoint subject, Endpoint observer, IMessagingClient client,
            Action notifier, ILoggerFactory? loggerFactory)
        {
            _subject = subject;
            _observer = observer;
            _client = client;
            _notifier = notifier;
            _logger = loggerFactory?.CreateLogger<PingPongFailureDetector>() 
                ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PingPongFailureDetector>.Instance;
            _timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000));
        }

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
