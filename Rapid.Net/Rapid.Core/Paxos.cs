using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rapid.Messaging;
using Rapid.Pb;

namespace Rapid;

/// <summary>
/// Single-decree consensus. Implements classic Paxos with the modified rule for the coordinator to pick values as per
/// the Fast Paxos paper: https://www.microsoft.com/en-us/research/wp-content/uploads/2016/02/tr-2005-112.pdf
///
/// The code below assumes that the first round in a consensus instance (done per configuration change) is the
/// only round that is a fast round. A round is identified by a tuple (rnd-number, nodeId), where nodeId is a unique
/// identifier per node that initiates phase1.
/// </summary>
internal sealed partial class Paxos
{
    private readonly ILogger<Paxos> _logger;
    private readonly IBroadcaster _broadcaster;
    private readonly IMessagingClient _client;
    private readonly long _configurationId;
    private readonly Endpoint _myAddr;
    private readonly int _n;

    private readonly struct LoggableEndpoints(IEnumerable<Endpoint> endpoints)
    {
        private readonly IEnumerable<Endpoint> _endpoints = endpoints;
        public override readonly string ToString() => string.Join(", ", _endpoints.Select(RapidUtils.Loggable));
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Prepare called by {MyAddr} for round {Crnd}")]
    private partial void LogPrepareCalled(Endpoint MyAddr, Rank Crnd);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Decided on value: {Value}")]
    private partial void LogDecidedValue(LoggableEndpoints Value);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Broadcasting startPhase1a message")]
    private partial void LogBroadcastingPhase1a();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Paxos initialized: myAddr={MyAddr}, configId={ConfigId}, n={N}")]
    private partial void LogPaxosInitialized(LoggableEndpoint MyAddr, long ConfigId, int N);

    [LoggerMessage(Level = LogLevel.Debug, Message = "RegisterFastRoundVote: proposal={Proposal}, voteCount={VoteCount}")]
    private partial void LogRegisterFastRoundVote(LoggableEndpoints Proposal, int VoteCount);

    [LoggerMessage(Level = LogLevel.Debug, Message = "StartPhase1a: skipping, current round {CurrentRound} > requested {RequestedRound}")]
    private partial void LogStartPhase1aSkipped(int CurrentRound, int RequestedRound);

    [LoggerMessage(Level = LogLevel.Debug, Message = "HandlePhase1aMessage: received from {Sender}, rank={Rank}, configId={ConfigId}")]
    private partial void LogHandlePhase1aReceived(LoggableEndpoint Sender, Rank Rank, long ConfigId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "HandlePhase1aMessage: config mismatch, expected={Expected}, got={Got}")]
    private partial void LogPhase1aConfigMismatch(long Expected, long Got);

    [LoggerMessage(Level = LogLevel.Debug, Message = "HandlePhase1aMessage: rank too low, received={Received}, current={Current}")]
    private partial void LogPhase1aRankTooLow(Rank Received, Rank Current);

    [LoggerMessage(Level = LogLevel.Debug, Message = "HandlePhase1aMessage: sending phase1b to {Destination}, rnd={Rnd}, vrnd={Vrnd}, vval={Vval}")]
    private partial void LogSendingPhase1b(LoggableEndpoint Destination, Rank Rnd, Rank Vrnd, LoggableEndpoints Vval);

    [LoggerMessage(Level = LogLevel.Debug, Message = "HandlePhase1bMessage: received from {Sender}, rnd={Rnd}, vrnd={Vrnd}, configId={ConfigId}")]
    private partial void LogHandlePhase1bReceived(LoggableEndpoint Sender, Rank Rnd, Rank Vrnd, long ConfigId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "HandlePhase1bMessage: config mismatch, expected={Expected}, got={Got}")]
    private partial void LogPhase1bConfigMismatch(long Expected, long Got);

    [LoggerMessage(Level = LogLevel.Debug, Message = "HandlePhase1bMessage: round mismatch, expected={Expected}, got={Got}")]
    private partial void LogPhase1bRoundMismatch(Rank Expected, Rank Got);

    [LoggerMessage(Level = LogLevel.Debug, Message = "HandlePhase1bMessage: collected {Count} responses, threshold={Threshold}, f={F}")]
    private partial void LogPhase1bCollected(int Count, int Threshold, int F);

    [LoggerMessage(Level = LogLevel.Debug, Message = "HandlePhase1bMessage: chosen value={ChosenValue}, broadcasting phase2a")]
    private partial void LogPhase1bChosenValue(LoggableEndpoints ChosenValue);

    [LoggerMessage(Level = LogLevel.Debug, Message = "HandlePhase2aMessage: received from {Sender}, rnd={Rnd}, vval={Vval}, configId={ConfigId}")]
    private partial void LogHandlePhase2aReceived(LoggableEndpoint Sender, Rank Rnd, LoggableEndpoints Vval, long ConfigId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "HandlePhase2aMessage: config mismatch, expected={Expected}, got={Got}")]
    private partial void LogPhase2aConfigMismatch(long Expected, long Got);

    [LoggerMessage(Level = LogLevel.Debug, Message = "HandlePhase2aMessage: rank too low, received={Received}, current={Current}")]
    private partial void LogPhase2aRankTooLow(Rank Received, Rank Current);

    [LoggerMessage(Level = LogLevel.Debug, Message = "HandlePhase2aMessage: accepting value, sending phase2b to {Destination}, rnd={Rnd}, vval={Vval}")]
    private partial void LogSendingPhase2b(LoggableEndpoint Destination, Rank Rnd, LoggableEndpoints Vval);

    [LoggerMessage(Level = LogLevel.Debug, Message = "HandlePhase2bMessage: received from {Sender}, rnd={Rnd}, endpoints={Endpoints}, configId={ConfigId}")]
    private partial void LogHandlePhase2bReceived(LoggableEndpoint Sender, Rank Rnd, LoggableEndpoints Endpoints, long ConfigId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "HandlePhase2bMessage: config mismatch, expected={Expected}, got={Got}")]
    private partial void LogPhase2bConfigMismatch(long Expected, long Got);

    [LoggerMessage(Level = LogLevel.Debug, Message = "HandlePhase2bMessage: round mismatch, expected={Expected}, got={Got}")]
    private partial void LogPhase2bRoundMismatch(Rank Expected, Rank Got);

    [LoggerMessage(Level = LogLevel.Debug, Message = "HandlePhase2bMessage: collected {Count} accept responses for round {Rnd}, threshold={Threshold}, f={F}")]
    private partial void LogPhase2bCollected(int Count, Rank Rnd, int Threshold, int F);

    [LoggerMessage(Level = LogLevel.Debug, Message = "ChooseValue: processing {Count} phase1b messages, n={N}")]
    private partial void LogChooseValueStart(int Count, int N);

    [LoggerMessage(Level = LogLevel.Debug, Message = "ChooseValue: no values with non-empty vval, returning empty")]
    private partial void LogChooseValueEmpty();

    [LoggerMessage(Level = LogLevel.Debug, Message = "ChooseValue: maxVrnd={MaxVrnd}, valuesWithMaxVrnd count={Count}")]
    private partial void LogChooseValueMaxVrnd(Rank MaxVrnd, int Count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "ChooseValue: all values identical, returning {Value}")]
    private partial void LogChooseValueIdentical(LoggableEndpoints Value);

    [LoggerMessage(Level = LogLevel.Debug, Message = "ChooseValue: maxCount={MaxCount}, threshold={Threshold}, choosing by count")]
    private partial void LogChooseValueByCount(int MaxCount, int Threshold);

    private Rank _rnd;
    private Rank _vrnd;
    private List<Endpoint> _vval = [];
    private readonly List<Phase1bMessage> _phase1bMessages = [];
    private readonly Dictionary<Rank, Dictionary<Endpoint, Phase2bMessage>> _acceptResponses = [];

    private Rank _crnd;
    private List<Endpoint> _cval = [];

    private readonly TaskCompletionSource<List<Endpoint>> _completion;

    // Fast round votes tracking
    private readonly Dictionary<List<Endpoint>, int> _fastRoundVotes = new(ListEndpointComparer.Instance);

    public Paxos(
        Endpoint myAddr,
        long configurationId,
        int n,
        IMessagingClient client,
        IBroadcaster broadcaster,
        TaskCompletionSource<List<Endpoint>> completion,
        ILoggerFactory? loggerFactory = null)
    {
        _myAddr = myAddr;
        _configurationId = configurationId;
        _n = n;
        _broadcaster = broadcaster;
        _client = client;
        _completion = completion;
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<Paxos>();

        _crnd = new Rank { Round = 0, NodeIndex = 0 };
        _rnd = new Rank { Round = 0, NodeIndex = 0 };
        _vrnd = new Rank { Round = 0, NodeIndex = 0 };

        LogPaxosInitialized(new LoggableEndpoint(myAddr), configurationId, n);
    }

    private readonly struct LoggableEndpoint(Endpoint endpoint)
    {
        private readonly Endpoint _endpoint = endpoint;
        public override readonly string ToString() => RapidUtils.Loggable(_endpoint);
    }

    /// <summary>
    /// This is how we're notified that a fast round is initiated. Invoked by a FastPaxos instance. This
    /// represents the logic at an acceptor receiving a phase2a message directly.
    /// </summary>
    /// <param name="proposal">the vote for the fast round</param>
    public void RegisterFastRoundVote(List<Endpoint> proposal)
    {
        // Do not participate in our only fast round if we are already participating in a classic round.
        // This is the 1st round in the consensus instance, is always a fast round, and is always the *only* fast round.
        // If this round does not succeed and we fallback to a classic round, we start with round number 2
        // and each node sets its node-index as the hash of its hostname. Doing so ensures that all classic
        // rounds initiated by any host is higher than the fast round, and there is an ordering between rounds
        // initiated by different endpoints.
        ref var voteCount = ref CollectionsMarshal.GetValueRefOrAddDefault(_fastRoundVotes, proposal, out var _);
        ++voteCount;
        LogRegisterFastRoundVote(new LoggableEndpoints(proposal), voteCount);
    }

    /// <summary>
    /// At coordinator, start a classic round. We ensure that even if round numbers are not unique, the
    /// "rank" = (round, nodeId) is unique by using unique node IDs.
    /// </summary>
    /// <param name="round">The round number to initiate.</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public void StartPhase1a(int round, CancellationToken cancellationToken = default)
    {
        if (_crnd.Round > round)
        {
            LogStartPhase1aSkipped(_crnd.Round, round);
            return;
        }

        _crnd = new Rank { Round = round, NodeIndex = _myAddr.GetHashCode() };
        LogPrepareCalled(_myAddr, _crnd);

        var prepare = new Phase1aMessage
        {
            ConfigurationId = _configurationId,
            Sender = _myAddr,
            Rank = _crnd
        };

        var request = RapidUtils.ToRapidRequest(prepare);
        LogBroadcastingPhase1a();
        _broadcaster.Broadcast(request, cancellationToken);
    }

    /// <summary>
    /// At acceptor, handle a phase1a message from a coordinator.
    /// </summary>
    /// <param name="phase1aMessage">phase1a message from a coordinator.</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public void HandlePhase1aMessage(Phase1aMessage phase1aMessage, CancellationToken cancellationToken = default)
    {
        LogHandlePhase1aReceived(new LoggableEndpoint(phase1aMessage.Sender), phase1aMessage.Rank, phase1aMessage.ConfigurationId);

        if (phase1aMessage.ConfigurationId != _configurationId)
        {
            LogPhase1aConfigMismatch(_configurationId, phase1aMessage.ConfigurationId);
            return;
        }

        if (phase1aMessage.Rank.CompareTo(_rnd) > 0)
        {
            _rnd = phase1aMessage.Rank;

            var phase1b = new Phase1bMessage
            {
                ConfigurationId = _configurationId,
                Sender = _myAddr,
                Rnd = _rnd,
                Vrnd = _vrnd
            };
            phase1b.Vval.AddRange(_vval);

            LogSendingPhase1b(new LoggableEndpoint(phase1aMessage.Sender), _rnd, _vrnd, new LoggableEndpoints(_vval));

            var request = RapidUtils.ToRapidRequest(phase1b);
            _client.SendOneWayMessage(phase1aMessage.Sender, request, cancellationToken);
        }
        else
        {
            LogPhase1aRankTooLow(phase1aMessage.Rank, _rnd);
        }
    }

    /// <summary>
    /// At coordinator, collect phase1b messages from acceptors to learn whether they have already voted for
    /// any values, and if a value might have been chosen.
    /// </summary>
    /// <param name="phase1bMessage">startPhase1a response messages from acceptors.</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public void HandlePhase1bMessage(Phase1bMessage phase1bMessage, CancellationToken cancellationToken = default)
    {
        LogHandlePhase1bReceived(new LoggableEndpoint(phase1bMessage.Sender), phase1bMessage.Rnd, phase1bMessage.Vrnd, phase1bMessage.ConfigurationId);

        if (phase1bMessage.ConfigurationId != _configurationId)
        {
            LogPhase1bConfigMismatch(_configurationId, phase1bMessage.ConfigurationId);
            return;
        }

        if (!phase1bMessage.Rnd.Equals(_crnd))
        {
            LogPhase1bRoundMismatch(_crnd, phase1bMessage.Rnd);
            return;
        }

        _phase1bMessages.Add(phase1bMessage);

        var f = (int)Math.Floor((_n - 1) / 4.0);
        var threshold = _n - f;
        LogPhase1bCollected(_phase1bMessages.Count, threshold, f);

        if (_phase1bMessages.Count >= threshold)
        {
            // selectProposalUsingCoordinator rule may execute multiple times with each additional phase1bMessage
            // being received, but we can enter the following if statement only once when a valid cval is identified.
            var chosenValue = ChooseValue(_phase1bMessages, _n);
            _cval = chosenValue;

            LogPhase1bChosenValue(new LoggableEndpoints(_cval));

            var phase2a = new Phase2aMessage
            {
                ConfigurationId = _configurationId,
                Sender = _myAddr,
                Rnd = _crnd
            };
            phase2a.Vval.AddRange(_cval);

            var request = RapidUtils.ToRapidRequest(phase2a);
            _broadcaster.Broadcast(request, cancellationToken);
        }
    }

    /// <summary>
    /// At acceptor, handle an accept message from a coordinator.
    /// </summary>
    /// <param name="phase2aMessage">accept message from coordinator</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public void HandlePhase2aMessage(Phase2aMessage phase2aMessage, CancellationToken cancellationToken = default)
    {
        LogHandlePhase2aReceived(new LoggableEndpoint(phase2aMessage.Sender), phase2aMessage.Rnd, new LoggableEndpoints(phase2aMessage.Vval), phase2aMessage.ConfigurationId);

        if (phase2aMessage.ConfigurationId != _configurationId)
        {
            LogPhase2aConfigMismatch(_configurationId, phase2aMessage.ConfigurationId);
            return;
        }

        if (phase2aMessage.Rnd.CompareTo(_rnd) >= 0)
        {
            _rnd = phase2aMessage.Rnd;
            _vrnd = phase2aMessage.Rnd;
            _vval = [.. phase2aMessage.Vval];

            var phase2b = new Phase2bMessage
            {
                ConfigurationId = _configurationId,
                Sender = _myAddr,
                Rnd = _rnd
            };
            phase2b.Endpoints.AddRange(_vval);

            LogSendingPhase2b(new LoggableEndpoint(phase2aMessage.Sender), _rnd, new LoggableEndpoints(_vval));

            var request = RapidUtils.ToRapidRequest(phase2b);
            _client.SendOneWayMessage(phase2aMessage.Sender, request, cancellationToken);
        }
        else
        {
            LogPhase2aRankTooLow(phase2aMessage.Rnd, _rnd);
        }
    }

    /// <summary>
    /// At acceptor, learn about another acceptor's vote (phase2b messages).
    /// </summary>
    /// <param name="phase2bMessage">acceptor's vote</param>
    public void HandlePhase2bMessage(Phase2bMessage phase2bMessage)
    {
        LogHandlePhase2bReceived(new LoggableEndpoint(phase2bMessage.Sender), phase2bMessage.Rnd, new LoggableEndpoints(phase2bMessage.Endpoints), phase2bMessage.ConfigurationId);

        if (phase2bMessage.ConfigurationId != _configurationId)
        {
            LogPhase2bConfigMismatch(_configurationId, phase2bMessage.ConfigurationId);
            return;
        }

        if (!phase2bMessage.Rnd.Equals(_crnd))
        {
            LogPhase2bRoundMismatch(_crnd, phase2bMessage.Rnd);
            return;
        }

        if (!_acceptResponses.TryGetValue(_crnd, out var acceptResponses))
        {
            _acceptResponses[_crnd] = acceptResponses = [];
        }

        acceptResponses[phase2bMessage.Sender] = phase2bMessage;

        var f = (int)Math.Floor((_n - 1) / 4.0);
        var threshold = _n - f;
        LogPhase2bCollected(acceptResponses.Count, _crnd, threshold, f);

        if (acceptResponses.Count >= threshold)
        {
            var endpoints = new List<Endpoint>(phase2bMessage.Endpoints);
            if (_completion.TrySetResult(endpoints))
            {
                LogDecidedValue(new LoggableEndpoints(endpoints));
            }
        }
    }

    /// <summary>
    /// The rule with which a coordinator picks a value to propose based on the received phase1b messages.
    /// This corresponds to the logic in Figure 2 of the Fast Paxos paper:
    /// https://www.microsoft.com/en-us/research/wp-content/uploads/2016/02/tr-2005-112.pdf
    /// </summary>
    /// <param name="phase1bMessages">A list of phase1b messages from acceptors.</param>
    /// <param name="n">The membership size</param>
    /// <returns>a proposal to apply</returns>
    private static List<Endpoint> ChooseValue(List<Phase1bMessage> phase1bMessages, int n)
    {
        // Let k be the largest value of vr(a) for all a in Q.
        // V (collectedVvals) be the set of all vv(a) for all a in Q s.t vr(a) == k
        var valuesByVrnd = phase1bMessages
            .Where(m => m.Vval.Count > 0)
            .GroupBy(m => m.Vrnd, RankComparer.Instance)
            .OrderByDescending(g => g.Key, RankComparer.Instance)
            .ToList();

        if (valuesByVrnd.Count == 0)
        {
            return [];
        }

        var maxVrnd = valuesByVrnd.First().Key;
        var valuesWithMaxVrnd = valuesByVrnd.First().Select(m => m.Vval.ToList()).ToList();

        // If V has a single element, then choose v.
        var firstValue = valuesWithMaxVrnd[0];
        if (valuesWithMaxVrnd.All(v => v.SequenceEqual(firstValue)))
        {
            return firstValue;
        }

        // if i-quorum Q of acceptors respond, and there is a k-quorum R such that vrnd = k and vval = v,
        // for all a in intersection(R, Q) -> then choose "v". When choosing E = N/4 and F = N/2, then
        // R intersection Q is N/4 -- meaning if there are more than N/4 identical votes.
        var valueCounts = new Dictionary<List<Endpoint>, int>(ListEndpointComparer.Instance);
        foreach (var value in valuesWithMaxVrnd)
        {
            ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(valueCounts, value, out var exists);
            ++entry;
        }

        var maxCount = valueCounts.Values.Max();
        var f = (int)Math.Floor((n - 1) / 4.0);

        if (maxCount >= (n - f) / 2)
        {
            return valueCounts.First(kv => kv.Value == maxCount).Key;
        }

        // At this point, no value has been selected yet and it is safe for the coordinator to pick any proposed value.
        // If none of the 'vvals' contain valid values (are all empty lists), then this method returns an empty
        // list. This can happen because a quorum of acceptors that did not vote in prior rounds may have responded
        // to the coordinator first. This is safe to do here for two reasons:
        //      1) The coordinator will only proceed with phase 2 if it has a valid vote.
        //      2) It is likely that the coordinator (itself being an acceptor) is the only one with a valid vval,
        //         and has not heard a Phase1bMessage from itself yet. Once that arrives, phase1b will be triggered
        //         again.
        //
        return [];
    }
}

