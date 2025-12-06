/*
 * Copyright © 2016 - 2025 VMware, Inc. All Rights Reserved.
 *
 * Licensed under the Apache License, Version 2.0 (the "License"); you may not use this file
 * except in compliance with the License. You may obtain a copy of the License at
 * http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software distributed under the
 * License is distributed on an "AS IS" BASIS, without warranties or conditions of any kind,
 * EITHER EXPRESS OR IMPLIED. See the License for the specific language governing
 * permissions and limitations under the License.
 */

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
internal sealed class Paxos
{
    private readonly ILogger<Paxos> _logger;
    private readonly IBroadcaster _broadcaster;
    private readonly IMessagingClient _client;
    private readonly long _configurationId;
    private readonly Endpoint _myAddr;
    private readonly int _n;

    private Rank _rnd;
    private Rank _vrnd;
    private List<Endpoint> _vval = [];
    private readonly List<Phase1bMessage> _phase1bMessages = [];
    private readonly Dictionary<Rank, Dictionary<Endpoint, Phase2bMessage>> _acceptResponses = [];

    private Rank _crnd;
    private List<Endpoint> _cval = [];

    private readonly Action<List<Endpoint>> _onDecide;
    private bool _decided = false;

    // Fast round votes tracking
    private readonly Dictionary<List<Endpoint>, int> _fastRoundVotes = new(ListEndpointComparer.Instance);

    public Paxos(Endpoint myAddr, long configurationId, int n, IMessagingClient client,
                 IBroadcaster broadcaster, Action<List<Endpoint>> onDecide, ILoggerFactory? loggerFactory = null)
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

    public void StartPhase1a(int round)
    {
        if (_crnd.Round > round)
        {
            return;
        }

        _crnd = new Rank { Round = round, NodeIndex = _myAddr.GetHashCode() };
        _logger.LogDebug("Prepare called by {MyAddr} for round {Crnd}", _myAddr, _crnd);

        var prepare = new Phase1aMessage
        {
            ConfigurationId = _configurationId,
            Sender = _myAddr,
            Rank = _crnd
        };

        var request = Utils.ToRapidRequest(prepare);
        _logger.LogTrace("Broadcasting startPhase1a message");
        _ = _broadcaster.BroadcastAsync(request);
    }

    public void HandlePhase1aMessage(Phase1aMessage phase1aMessage)
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

            var request = Utils.ToRapidRequest(phase1b);
            _ = _client.SendMessageAsync(phase1aMessage.Sender, request);
        }
    }

    public void HandlePhase1bMessage(Phase1bMessage phase1bMessage)
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

            var request = Utils.ToRapidRequest(phase2a);
            _ = _broadcaster.BroadcastAsync(request);
        }
    }

    public void HandlePhase2aMessage(Phase2aMessage phase2aMessage)
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

            var request = Utils.ToRapidRequest(phase2b);
            _ = _client.SendMessageAsync(phase2aMessage.Sender, request);
        }
    }

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
        if (acceptResponses.Count >= _n - f && !_decided)
        {
            _decided = true;
            var endpoints = new List<Endpoint>(phase2bMessage.Endpoints);
            _logger.LogDebug("Decided on value: {Value}", string.Join(", ", endpoints));
            _onDecide(endpoints);
        }
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

