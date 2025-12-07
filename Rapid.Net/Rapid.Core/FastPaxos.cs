using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Rapid.Messaging;
using Rapid.Pb;

namespace Rapid;

/// <summary>
/// Single-decree consensus. We always start with a Fast round.
/// </summary>
internal sealed partial class FastPaxos : IDisposable
{
    private readonly ILogger<FastPaxos> _logger;
    private readonly double _jitterRate;
    private readonly Endpoint _myAddr;
    private readonly long _configurationId;
    private readonly long _membershipSize;
    private readonly IBroadcaster _broadcaster;
    private readonly Dictionary<List<Endpoint>, int> _votesPerProposal = new(ListEndpointComparer.Instance);
    private readonly HashSet<Endpoint> _votesReceived = [];
    private readonly Paxos _paxos;
    private readonly Lock _paxosLock = new();
    private readonly RapidProtocolOptions _options;
    private readonly SharedResources _sharedResources;
    private readonly CancellationTokenSource _scheduledClassicRoundCts = new();

    private readonly struct LoggableEndpoints(IEnumerable<Endpoint> endpoints)
    {
        private readonly IEnumerable<Endpoint> _endpoints = endpoints;
        public override readonly string ToString() => string.Join(", ", _endpoints.Select(RapidUtils.Loggable));
    }

    [LoggerMessage(Level = LogLevel.Trace, Message = "Configuration ID mismatch for proposal: current_config:{CurrentConfig}")]
    private partial void LogConfigurationMismatch(long CurrentConfig);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Decided on a view change: {Proposal}")]
    private partial void LogDecidedViewChange(LoggableEndpoints Proposal);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Fast round may not succeed for proposal")]
    private partial void LogFastRoundMayNotSucceed();

    [LoggerMessage(Level = LogLevel.Trace, Message = "Scheduling classic round with delay: {Delay}")]
    private partial void LogSchedulingClassicRound(TimeSpan Delay);

    private readonly TaskCompletionSource<List<Endpoint>> _onDecidedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<List<Endpoint>> Decided => _onDecidedTcs.Task;

    public FastPaxos(
        Endpoint myAddr,
        long configurationId,
        int membershipSize,
        IMessagingClient client,
        IBroadcaster broadcaster,
        IOptions<RapidProtocolOptions> options,
        SharedResources sharedResources,
        ILoggerFactory? loggerFactory = null)
    {
        _myAddr = myAddr;
        _configurationId = configurationId;
        _membershipSize = membershipSize;
        _broadcaster = broadcaster;
        _options = options.Value;
        _sharedResources = sharedResources;
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<FastPaxos>();

        // The rate of a random expovariate variable, used to determine a jitter over a base delay to start classic
        // rounds. This determines how many classic rounds we want to start per second on average. Does not
        // affect correctness of the protocol, but having too many nodes starting rounds will increase messaging load,
        // especially for very large clusters.
        _jitterRate = 1 / (double)membershipSize;

        _paxos = new Paxos(myAddr, configurationId, membershipSize, client, broadcaster,
                          _onDecidedTcs, loggerFactory);
    }

    /// <summary>
    /// Propose a value for a fast round.
    /// </summary>
    /// <param name="proposal">the membership change proposal towards a configuration change.</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public void Propose(List<Endpoint> proposal, CancellationToken cancellationToken = default) => Propose(proposal, GetRandomDelay(), cancellationToken);

    /// <summary>
    /// Propose a value for a fast round with a delay to trigger the recovery protocol.
    /// </summary>
    /// <param name="proposal">the membership change proposal towards a configuration change.</param>
    /// <param name="recoveryDelay">Delay before starting classic Paxos round</param>
    /// <param name="cancellationToken">Cancellation token</param>
    private void Propose(List<Endpoint> proposal, TimeSpan recoveryDelay, CancellationToken cancellationToken = default)
    {
        lock (_paxosLock)
        {
            _paxos.RegisterFastRoundVote(proposal);
        }

        var consensusMessage = new FastRoundPhase2bMessage
        {
            ConfigurationId = _configurationId,
            Sender = _myAddr
        };
        consensusMessage.Endpoints.AddRange(proposal);

        var proposalMessage = RapidUtils.ToRapidRequest(consensusMessage);
        _broadcaster.Broadcast(proposalMessage, cancellationToken);

        LogSchedulingClassicRound(recoveryDelay);
        var classicRoundTask = ScheduleClassicRoundAsync(recoveryDelay);
        _sharedResources.TrackBackgroundTask(classicRoundTask);
    }

    /// <summary>
    /// Trigger Paxos phase1a.
    /// </summary>
    private async Task ScheduleClassicRoundAsync(TimeSpan recoveryDelay)
    {
        await Task.Delay(recoveryDelay, _sharedResources.TimeProvider, _scheduledClassicRoundCts.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (_onDecidedTcs.Task.IsCompleted || _scheduledClassicRoundCts.IsCancellationRequested)
        {
            return;
        }

        // Start classic Paxos round with round number 2
        lock (_paxosLock)
        {
            _paxos.StartPhase1a(2, _scheduledClassicRoundCts.Token);
        }
    }

    /// <summary>
    /// Invoked by the membership service when it receives a proposal for a fast round.
    /// </summary>
    /// <param name="proposalMessage">the membership change proposal towards a configuration change.</param>
    private void HandleFastRoundProposal(FastRoundPhase2bMessage proposalMessage)
    {
        if (proposalMessage.ConfigurationId != _configurationId)
        {
            LogConfigurationMismatch(_configurationId);
            return;
        }

        if (_votesReceived.Contains(proposalMessage.Sender))
        {
            return;
        }

        if (_onDecidedTcs.Task.IsCompleted)
        {
            return;
        }

        _votesReceived.Add(proposalMessage.Sender);

        var proposalList = new List<Endpoint>(proposalMessage.Endpoints);
        ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(_votesPerProposal, proposalList, out var exists);
        ++entry;

        var count = entry;
        var f = (int)Math.Floor((_membershipSize - 1) / 4.0); // Fast Paxos resiliency.

        if (_votesReceived.Count >= _membershipSize - f)
        {
            if (count >= _membershipSize - f)
            {
                LogDecidedViewChange(new LoggableEndpoints(proposalList));

                // We have a successful proposal. Consume it.
                if (_onDecidedTcs.TrySetResult(proposalList))
                {
                    // Cancel the classic round.
                    _scheduledClassicRoundCts?.Cancel();
                }
            }
            else
            {
                // fallback protocol here
                LogFastRoundMayNotSucceed();
            }
        }
    }

    /// <summary>
    /// Invoked by the membership service when it receives a consensus proposal.
    /// </summary>
    /// <param name="request">the membership change proposal towards a configuration change.</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Response message</returns>
    public RapidResponse HandleMessages(RapidRequest request, CancellationToken cancellationToken = default)
    {
        switch (request.ContentCase)
        {
            case RapidRequest.ContentOneofCase.FastRoundPhase2BMessage:
                HandleFastRoundProposal(request.FastRoundPhase2BMessage);
                break;
            case RapidRequest.ContentOneofCase.Phase1AMessage:
                _paxos.HandlePhase1aMessage(request.Phase1AMessage, cancellationToken);
                break;
            case RapidRequest.ContentOneofCase.Phase1BMessage:
                _paxos.HandlePhase1bMessage(request.Phase1BMessage, cancellationToken);
                break;
            case RapidRequest.ContentOneofCase.Phase2AMessage:
                _paxos.HandlePhase2aMessage(request.Phase2AMessage, cancellationToken);
                break;
            case RapidRequest.ContentOneofCase.Phase2BMessage:
                _paxos.HandlePhase2bMessage(request.Phase2BMessage);
                break;
            default:
                throw new ArgumentException($"Unexpected message case: {request.ContentCase}");
        }

        return RapidUtils.ToRapidResponse(new ConsensusResponse());
    }

    /// <summary>
    /// Random expovariate variable plus a base delay.
    /// </summary>
    private TimeSpan GetRandomDelay()
    {
#pragma warning disable CA5394 // Do not use insecure randomness. Justification: this is not security-sensitive code.
        var jitter = (long)(-1000 * Math.Log(1 - Random.Shared.NextDouble()) / _jitterRate);
#pragma warning restore CA5394 // Do not use insecure randomness
        return TimeSpan.FromMicroseconds(jitter + (long)_options.ConsensusFallbackTimeoutBaseDelay.TotalMilliseconds);
    }

    public void Dispose() => _scheduledClassicRoundCts?.Dispose();
}
