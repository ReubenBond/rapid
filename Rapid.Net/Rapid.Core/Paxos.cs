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
        if (phase1aMessage.ConfigurationId != _configurationId)
        {
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

            var request = RapidUtils.ToRapidRequest(phase1b);
            _client.SendOneWayMessage(phase1aMessage.Sender, request, cancellationToken);
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
        if (phase1bMessage.ConfigurationId != _configurationId)
        {
            return;
        }

        if (!phase1bMessage.Rnd.Equals(_crnd))
        {
            return;
        }

        _phase1bMessages.Add(phase1bMessage);

        var f = (int)Math.Floor((_n - 1) / 4.0);
        if (_phase1bMessages.Count >= _n - f)
        {
            // selectProposalUsingCoordinator rule may execute multiple times with each additional phase1bMessage
            // being received, but we can enter the following if statement only once when a valid cval is identified.
            var chosenValue = ChooseValue(_phase1bMessages, _n);
            _cval = chosenValue;

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
        if (phase2aMessage.ConfigurationId != _configurationId)
        {
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

            var request = RapidUtils.ToRapidRequest(phase2b);
            _client.SendOneWayMessage(phase2aMessage.Sender, request, cancellationToken);
        }
    }

    /// <summary>
    /// At acceptor, learn about another acceptor's vote (phase2b messages).
    /// </summary>
    /// <param name="phase2bMessage">acceptor's vote</param>
    public void HandlePhase2bMessage(Phase2bMessage phase2bMessage)
    {
        if (phase2bMessage.ConfigurationId != _configurationId)
        {
            return;
        }

        if (!phase2bMessage.Rnd.Equals(_crnd))
        {
            return;
        }

        if (!_acceptResponses.TryGetValue(_crnd, out var acceptResponses))
        {
            _acceptResponses[_crnd] = acceptResponses = [];
        }

        acceptResponses[phase2bMessage.Sender] = phase2bMessage;

        var f = (int)Math.Floor((_n - 1) / 4.0);
        if (acceptResponses.Count >= _n - f)
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

