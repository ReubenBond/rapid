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
    private readonly Action<List<Endpoint>> _onDecidedWrapped;
    private readonly IBroadcaster _broadcaster;
    private readonly Dictionary<List<Endpoint>, int> _votesPerProposal = new(ListEndpointComparer.Instance);
    private readonly HashSet<Endpoint> _votesReceived = [];
    private readonly Paxos _paxos;
    private readonly Lock _paxosLock = new();
    private bool _decided;
    private CancellationTokenSource? _scheduledClassicRoundCts;
    private readonly RapidProtocolOptions _options;
    private readonly SharedResources _sharedResources;

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

    public FastPaxos(
        Endpoint myAddr,
        long configurationId,
        int membershipSize,
        IMessagingClient client,
        IBroadcaster broadcaster,
        Action<List<Endpoint>> onDecide,
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
        // rounds. This determines how many classic rounds we want to start per second on average.
        _jitterRate = 1 / (double)membershipSize;

        _onDecidedWrapped = hosts =>
        {
            if (_decided) return;
            _decided = true;
            _scheduledClassicRoundCts?.Cancel();
            onDecide(hosts);
        };

        _paxos = new Paxos(myAddr, configurationId, membershipSize, client, broadcaster,
                          _onDecidedWrapped, loggerFactory);
    }

    /// <summary>
    /// Propose a value for a fast round with a delay to trigger the recovery protocol.
    /// </summary>
    /// <param name="proposal">The membership change proposal towards a configuration change.</param>
    /// <param name="recoveryDelay">Delay before starting classic Paxos round</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task ProposeAsync(List<Endpoint> proposal, TimeSpan recoveryDelay, CancellationToken cancellationToken = default)
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
        await _broadcaster.BroadcastAsync(proposalMessage).ConfigureAwait(false);

        LogSchedulingClassicRound(recoveryDelay);
        _scheduledClassicRoundCts = new CancellationTokenSource();
        var classicRoundTask = ScheduleClassicRoundAsync(recoveryDelay, cancellationToken);
        _sharedResources.TrackBackgroundTask(classicRoundTask);
    }

    private async Task ScheduleClassicRoundAsync(TimeSpan recoveryDelay, CancellationToken cancellationToken)
    {
        await Task.Delay(recoveryDelay, _sharedResources.TimeProvider, _scheduledClassicRoundCts!.Token).ConfigureAwait(false);
        await StartClassicPaxosRoundAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Propose a value for a fast round.
    /// </summary>
    /// <param name="proposal">The membership change proposal towards a configuration change.</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public Task ProposeAsync(List<Endpoint> proposal, CancellationToken cancellationToken = default) => ProposeAsync(proposal, GetRandomDelay(), cancellationToken);

    /// <summary>
    /// Invoked by the membership service when it receives a proposal for a fast round.
    /// </summary>
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

        if (_decided)
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
                _onDecidedWrapped(proposalList);
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
    public async Task<RapidResponse> HandleMessagesAsync(RapidRequest request, CancellationToken cancellationToken = default)
    {
        switch (request.ContentCase)
        {
            case RapidRequest.ContentOneofCase.FastRoundPhase2BMessage:
                HandleFastRoundProposal(request.FastRoundPhase2BMessage);
                break;
            case RapidRequest.ContentOneofCase.Phase1AMessage:
                await _paxos.HandlePhase1aMessageAsync(request.Phase1AMessage, cancellationToken).ConfigureAwait(false);
                break;
            case RapidRequest.ContentOneofCase.Phase1BMessage:
                await _paxos.HandlePhase1bMessageAsync(request.Phase1BMessage, cancellationToken).ConfigureAwait(false);
                break;
            case RapidRequest.ContentOneofCase.Phase2AMessage:
                await _paxos.HandlePhase2aMessageAsync(request.Phase2AMessage, cancellationToken).ConfigureAwait(false);
                break;
            case RapidRequest.ContentOneofCase.Phase2BMessage:
                await _paxos.HandlePhase2bMessageAsync(request.Phase2BMessage, cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new ArgumentException($"Unexpected message case: {request.ContentCase}");
        }
        return RapidUtils.ToRapidResponse(new ConsensusResponse());
    }

    /// <summary>
    /// Trigger Paxos phase1a.
    /// </summary>
    public Task StartClassicPaxosRoundAsync(CancellationToken cancellationToken = default)
    {
        if (_decided)
        {
            return Task.CompletedTask;
        }

        Task result;
        lock (_paxosLock)
        {
            result = _paxos.StartPhase1aAsync(2, cancellationToken);
        }
        return result;
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
