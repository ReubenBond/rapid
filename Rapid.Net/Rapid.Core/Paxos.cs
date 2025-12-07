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

    private readonly Action<List<Endpoint>> _onDecide;
    private bool _decided;

    // Fast round votes tracking
    private readonly Dictionary<List<Endpoint>, int> _fastRoundVotes = new(ListEndpointComparer.Instance);

    public Paxos(
        Endpoint myAddr,
        long configurationId,
        int n,
        IMessagingClient client,
        IBroadcaster broadcaster,
        Action<List<Endpoint>> onDecide,
        ILoggerFactory? loggerFactory = null)
    {
        _myAddr = myAddr;
        _configurationId = configurationId;
        _n = n;
        _broadcaster = broadcaster;
        _client = client;
        _onDecide = onDecide;
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<Paxos>();

        _crnd = new Rank { Round = 0, NodeIndex = 0 };
        _rnd = new Rank { Round = 0, NodeIndex = 0 };
        _vrnd = new Rank { Round = 0, NodeIndex = 0 };
    }

    public void RegisterFastRoundVote(List<Endpoint> proposal)
    {
        ref var voteCount = ref CollectionsMarshal.GetValueRefOrAddDefault(_fastRoundVotes, proposal, out var _);
        ++voteCount;
    }

    public async Task StartPhase1aAsync(int round, CancellationToken cancellationToken = default)
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
        await _broadcaster.BroadcastAsync(request).ConfigureAwait(false);
    }

    public async Task HandlePhase1aMessageAsync(Phase1aMessage phase1aMessage, CancellationToken cancellationToken = default)
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
            await _client.SendMessageAsync(phase1aMessage.Sender, request, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task HandlePhase1bMessageAsync(Phase1bMessage phase1bMessage, CancellationToken cancellationToken = default)
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
            await _broadcaster.BroadcastAsync(request).ConfigureAwait(false);
        }
    }

    public async Task HandlePhase2aMessageAsync(Phase2aMessage phase2aMessage, CancellationToken cancellationToken = default)
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
            await _client.SendMessageAsync(phase2aMessage.Sender, request, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task HandlePhase2bMessageAsync(Phase2bMessage phase2bMessage, CancellationToken cancellationToken = default)
    {
        if (phase2bMessage.ConfigurationId != _configurationId)
        {
            return Task.CompletedTask;
        }

        if (!phase2bMessage.Rnd.Equals(_crnd))
        {
            return Task.CompletedTask;
        }

        if (!_acceptResponses.TryGetValue(_crnd, out var acceptResponses))
        {
            _acceptResponses[_crnd] = acceptResponses = [];
        }

        acceptResponses[phase2bMessage.Sender] = phase2bMessage;

        var f = (int)Math.Floor((_n - 1) / 4.0);
        if (acceptResponses.Count >= _n - f && !_decided)
        {
            _decided = true;
            var endpoints = new List<Endpoint>(phase2bMessage.Endpoints);
            LogDecidedValue(new LoggableEndpoints(endpoints));
            _onDecide(endpoints);
        }

        return Task.CompletedTask;
    }

    private static List<Endpoint> ChooseValue(List<Phase1bMessage> phase1bMessages, int n)
    {
        // Implement Fast Paxos value selection rule
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

        // Check if all values are the same
        var firstValue = valuesWithMaxVrnd[0];
        if (valuesWithMaxVrnd.All(v => v.SequenceEqual(firstValue)))
        {
            return firstValue;
        }

        // Find the value with the most votes
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

        return [];
    }
}

