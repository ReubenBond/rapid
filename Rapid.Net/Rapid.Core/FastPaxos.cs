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

    [LoggerMessage(Level = LogLevel.Debug, Message = "FastPaxos initialized: myAddr={MyAddr}, configId={ConfigId}, membershipSize={MembershipSize}")]
    private partial void LogFastPaxosInitialized(LoggableEndpoint MyAddr, long ConfigId, long MembershipSize);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Propose: broadcasting fast round proposal={Proposal}, recoveryDelay={RecoveryDelay}")]
    private partial void LogPropose(LoggableEndpoints Proposal, TimeSpan RecoveryDelay);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Propose: registered fast round vote for proposal")]
    private partial void LogProposeRegisteredVote();

    [LoggerMessage(Level = LogLevel.Debug, Message = "ScheduleClassicRoundAsync: starting classic Paxos round 2 after delay")]
    private partial void LogStartingClassicRound();

    [LoggerMessage(Level = LogLevel.Debug, Message = "ScheduleClassicRoundAsync: skipped, already decided or cancelled")]
    private partial void LogClassicRoundSkipped();

    [LoggerMessage(Level = LogLevel.Debug, Message = "HandleFastRoundProposal: received from {Sender}, endpoints={Endpoints}, configId={ConfigId}")]
    private partial void LogHandleFastRoundProposalReceived(LoggableEndpoint Sender, LoggableEndpoints Endpoints, long ConfigId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "HandleFastRoundProposal: duplicate vote from {Sender}, ignoring")]
    private partial void LogDuplicateFastRoundVote(LoggableEndpoint Sender);

    [LoggerMessage(Level = LogLevel.Debug, Message = "HandleFastRoundProposal: already decided, ignoring")]
    private partial void LogFastRoundAlreadyDecided();

    [LoggerMessage(Level = LogLevel.Debug, Message = "HandleFastRoundProposal: vote count for proposal={Count}, total votes received={TotalVotes}, threshold={Threshold}, f={F}")]
    private partial void LogFastRoundVoteCount(int Count, int TotalVotes, long Threshold, int F);

    [LoggerMessage(Level = LogLevel.Debug, Message = "HandleFastRoundProposal: fast round succeeded, cancelling classic round")]
    private partial void LogFastRoundSucceeded();

    [LoggerMessage(Level = LogLevel.Debug, Message = "HandleMessages: received {MessageType}")]
    private partial void LogHandleMessages(RapidRequest.ContentOneofCase MessageType);

    [LoggerMessage(Level = LogLevel.Debug, Message = "HandleMessages: unexpected message case {MessageCase}")]
    private partial void LogUnexpectedMessageCase(RapidRequest.ContentOneofCase MessageCase);

    [LoggerMessage(Level = LogLevel.Debug, Message = "GetRandomDelay: computed jitter={Jitter}ms, total delay={TotalDelay}")]
    private partial void LogRandomDelay(long Jitter, TimeSpan TotalDelay);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Dispose: cleaning up FastPaxos resources")]
    private partial void LogDispose();

    private readonly struct LoggableEndpoint(Endpoint endpoint)
    {
        private readonly Endpoint _endpoint = endpoint;
        public override readonly string ToString() => RapidUtils.Loggable(_endpoint);
    }

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

        LogFastPaxosInitialized(new LoggableEndpoint(myAddr), configurationId, membershipSize);
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
        LogPropose(new LoggableEndpoints(proposal), recoveryDelay);

        lock (_paxosLock)
        {
            _paxos.RegisterFastRoundVote(proposal);
            LogProposeRegisteredVote();
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
        var classicRoundTask = ScheduleClassicRoundAsync(recoveryDelay, _sharedResources.ShuttingDown);
        _sharedResources.TrackBackgroundTask(classicRoundTask);
    }

    /// <summary>
    /// Trigger Paxos phase1a.
    /// </summary>
    private async Task ScheduleClassicRoundAsync(TimeSpan recoveryDelay, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _scheduledClassicRoundCts.Token);
        await Task.Delay(recoveryDelay, _sharedResources.TimeProvider, cts.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (_onDecidedTcs.Task.IsCompleted || cts.IsCancellationRequested)
        {
            LogClassicRoundSkipped();
            return;
        }

        LogStartingClassicRound();

        // Start classic Paxos round with round number 2
        lock (_paxosLock)
        {
            _paxos.StartPhase1a(2, cts.Token);
        }
    }

    /// <summary>
    /// Invoked by the membership service when it receives a proposal for a fast round.
    /// </summary>
    /// <param name="proposalMessage">the membership change proposal towards a configuration change.</param>
    private void HandleFastRoundProposal(FastRoundPhase2bMessage proposalMessage)
    {
        LogHandleFastRoundProposalReceived(new LoggableEndpoint(proposalMessage.Sender), new LoggableEndpoints(proposalMessage.Endpoints), proposalMessage.ConfigurationId);

        if (proposalMessage.ConfigurationId != _configurationId)
        {
            LogConfigurationMismatch(_configurationId);
            return;
        }

        if (_votesReceived.Contains(proposalMessage.Sender))
        {
            LogDuplicateFastRoundVote(new LoggableEndpoint(proposalMessage.Sender));
            return;
        }

        if (_onDecidedTcs.Task.IsCompleted)
        {
            LogFastRoundAlreadyDecided();
            return;
        }

        _votesReceived.Add(proposalMessage.Sender);

        var proposalList = new List<Endpoint>(proposalMessage.Endpoints);
        ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(_votesPerProposal, proposalList, out var exists);
        ++entry;

        var count = entry;
        var f = (int)Math.Floor((_membershipSize - 1) / 4.0); // Fast Paxos resiliency.
        var threshold = _membershipSize - f;

        LogFastRoundVoteCount(count, _votesReceived.Count, threshold, f);

        if (_votesReceived.Count >= _membershipSize - f)
        {
            if (count >= _membershipSize - f)
            {
                LogDecidedViewChange(new LoggableEndpoints(proposalList));

                // We have a successful proposal. Consume it.
                if (_onDecidedTcs.TrySetResult(proposalList))
                {
                    LogFastRoundSucceeded();
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
        LogHandleMessages(request.ContentCase);

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
                LogUnexpectedMessageCase(request.ContentCase);
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
        var totalDelay = TimeSpan.FromMicroseconds(jitter + (long)_options.ConsensusFallbackTimeoutBaseDelay.TotalMilliseconds);
        LogRandomDelay(jitter, totalDelay);
        return totalDelay;
    }

    public void Dispose()
    {
        LogDispose();
        _scheduledClassicRoundCts?.Dispose();
    }
}
