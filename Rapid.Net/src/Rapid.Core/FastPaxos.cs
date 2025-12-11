using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

using Rapid.Messaging;
using Rapid.Pb;

namespace Rapid;

/// <summary>
/// Handles the fast round (round 1) of Fast Paxos consensus.
/// 
/// This class is responsible for:
/// - Broadcasting fast round proposals (Phase 2b messages)
/// - Collecting fast round votes and deciding if threshold is met
/// - Returning a result indicating success, vote split, or delivery failure
/// 
/// This class does NOT handle classic Paxos rounds. The ConsensusCoordinator
/// is responsible for creating Paxos instances for classic rounds (2, 3, ...).
/// </summary>
internal sealed partial class FastPaxos
{
    private readonly ILogger<FastPaxos> _logger;
    private readonly Endpoint _myAddr;
    private readonly long _configurationId;
    private readonly long _membershipSize;
    private readonly IBroadcaster _broadcaster;
    private readonly Dictionary<List<Endpoint>, int> _votesPerProposal = new(ListEndpointComparer.Instance);
    private readonly HashSet<Endpoint> _votesReceived = [];

    private readonly struct LoggableEndpoints(IEnumerable<Endpoint> endpoints)
    {
        private readonly IEnumerable<Endpoint> _endpoints = endpoints;
        public override readonly string ToString() => string.Join(", ", _endpoints.Select(RapidUtils.Loggable));
    }

    private readonly struct LoggableEndpoint(Endpoint endpoint)
    {
        private readonly Endpoint _endpoint = endpoint;
        public override readonly string ToString() => RapidUtils.Loggable(_endpoint);
    }

    [LoggerMessage(Level = LogLevel.Trace, Message = "Configuration ID mismatch for proposal: current_config:{CurrentConfig}")]
    private partial void LogConfigurationMismatch(long CurrentConfig);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Decided on a view change: {Proposal}")]
    private partial void LogDecidedViewChange(LoggableEndpoints Proposal);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Fast round may not succeed for proposal")]
    private partial void LogFastRoundMayNotSucceed();

    [LoggerMessage(Level = LogLevel.Debug, Message = "FastPaxos initialized: myAddr={MyAddr}, configId={ConfigId}, membershipSize={MembershipSize}")]
    private partial void LogFastPaxosInitialized(LoggableEndpoint MyAddr, long ConfigId, long MembershipSize);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Propose: broadcasting fast round proposal={Proposal}")]
    private partial void LogPropose(LoggableEndpoints Proposal);

    [LoggerMessage(Level = LogLevel.Debug, Message = "HandleFastRoundProposal: received from {Sender}, endpoints={Endpoints}, configId={ConfigId}")]
    private partial void LogHandleFastRoundProposalReceived(LoggableEndpoint Sender, LoggableEndpoints Endpoints, long ConfigId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "HandleFastRoundProposal: duplicate vote from {Sender}, ignoring")]
    private partial void LogDuplicateFastRoundVote(LoggableEndpoint Sender);

    [LoggerMessage(Level = LogLevel.Debug, Message = "HandleFastRoundProposal: already decided, ignoring")]
    private partial void LogFastRoundAlreadyDecided();

    [LoggerMessage(Level = LogLevel.Debug, Message = "HandleFastRoundProposal: vote count for proposal={Count}, total votes received={TotalVotes}, threshold={Threshold}, f={F}")]
    private partial void LogFastRoundVoteCount(int Count, int TotalVotes, long Threshold, int F);

    [LoggerMessage(Level = LogLevel.Debug, Message = "HandleFastRoundProposal: fast round succeeded")]
    private partial void LogFastRoundSucceeded();

    [LoggerMessage(Level = LogLevel.Information, Message = "Early fallback needed: {FailureCount} delivery failures (f={F}, need at least {Threshold} for Fast Paxos)")]
    private partial void LogEarlyFallbackNeeded(int FailureCount, int F, long Threshold);

    private readonly TaskCompletionSource<ConsensusResult> _resultTcs = new();
    private CancellationTokenRegistration _cancellationRegistration;

    /// <summary>
    /// Task that completes when fast round finishes (either success or failure).
    /// </summary>
    public Task<ConsensusResult> Result => _resultTcs.Task;

    public FastPaxos(
            Endpoint myAddr,
            long configurationId,
            int membershipSize,
            IBroadcaster broadcaster,
            ILogger<FastPaxos> logger)
    {
        _myAddr = myAddr;
        _configurationId = configurationId;
        _membershipSize = membershipSize;
        _broadcaster = broadcaster;
        _logger = logger;

        LogFastPaxosInitialized(new LoggableEndpoint(myAddr), configurationId, membershipSize);
    }

    /// <summary>
    /// Register a timeout cancellation token that will complete the result task with Cancelled.
    /// This should be called when starting the fast round to set up the timeout.
    /// </summary>
    public void RegisterTimeoutToken(CancellationToken timeoutToken)
    {
        if (timeoutToken.CanBeCanceled)
        {
            _cancellationRegistration = timeoutToken.Register(() =>
            {
                _resultTcs.TrySetResult(ConsensusResult.Cancelled.Instance);
            });
        }
    }

    /// <summary>
    /// Propose a value for a fast round. Only broadcasts the fast round message.
    /// The coordinator is responsible for scheduling classic round fallback.
    /// </summary>
    /// <param name="proposal">the membership change proposal towards a configuration change.</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public void Propose(List<Endpoint> proposal, CancellationToken cancellationToken = default)
    {
        LogPropose(new LoggableEndpoints(proposal));

        var consensusMessage = new FastRoundPhase2bMessage
        {
            ConfigurationId = _configurationId,
            Sender = _myAddr
        };
        consensusMessage.Endpoints.AddRange(proposal);

        var proposalMessage = RapidUtils.ToRapidRequest(consensusMessage);

        // Calculate threshold for early fallback detection
        // Fast Paxos requires N - f votes, where f = floor((N-1)/4)
        var f = (int)Math.Floor((_membershipSize - 1) / 4.0);
        var fastPaxosThreshold = _membershipSize - f;

        // Track delivery failures to notify coordinator
        int failureCount = 0;

        // Broadcast with failure callback to detect when fast round cannot succeed
        _broadcaster.Broadcast(proposalMessage, failedEndpoint =>
        {
            var newFailureCount = Interlocked.Increment(ref failureCount);

            // Calculate max possible votes: membership - failures
            var maxPossibleVotes = _membershipSize - newFailureCount;

            // If we can't reach the threshold due to delivery failures, complete with failure
            if (maxPossibleVotes < fastPaxosThreshold && !_resultTcs.Task.IsCompleted)
            {
                LogEarlyFallbackNeeded(newFailureCount, f, fastPaxosThreshold);
                _resultTcs.TrySetResult(ConsensusResult.DeliveryFailure.Instance);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Handle a fast round proposal (Phase 2b of Fast Paxos).
    /// </summary>
    /// <param name="proposalMessage">the membership change proposal towards a configuration change.</param>
    public void HandleFastRoundProposal(FastRoundPhase2bMessage proposalMessage)
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

        if (_resultTcs.Task.IsCompleted)
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
                if (_resultTcs.TrySetResult(new ConsensusResult.Decided(proposalList)))
                {
                    LogFastRoundSucceeded();
                }
            }
            else
            {
                // Fast round cannot succeed due to vote split, complete with failure
                LogFastRoundMayNotSucceed();
                _resultTcs.TrySetResult(ConsensusResult.VoteSplit.Instance);
            }
        }
    }

    /// <summary>
    /// Cancel the fast round, completing the result task with Cancelled.
    /// Also cleans up any cancellation token registration.
    /// </summary>
    public void Cancel()
    {
        _cancellationRegistration.Dispose();
        _resultTcs.TrySetResult(ConsensusResult.Cancelled.Instance);
    }
}
