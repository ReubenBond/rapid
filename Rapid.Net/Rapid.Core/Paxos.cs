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
    private List<Endpoint> _vval = new();
    private readonly List<Phase1bMessage> _phase1bMessages = new();
    private readonly Dictionary<Rank, Dictionary<Endpoint, Phase2bMessage>> _acceptResponses = new();
    
    private Rank _crnd;
    private List<Endpoint> _cval = new();
    
    private readonly Action<List<Endpoint>> _onDecide;
    private bool _decided = false;
    
    // Fast round votes tracking
    private readonly Dictionary<List<Endpoint>, int> _fastRoundVotes = new(new ListEndpointComparer());

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
        if (!_fastRoundVotes.ContainsKey(proposal))
        {
            _fastRoundVotes[proposal] = 0;
        }
        _fastRoundVotes[proposal]++;
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

        if (CompareRanks(phase1aMessage.Rank, _rnd) > 0)
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

        if (!CompareRanksEqual(phase1bMessage.Rnd, _crnd))
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

        if (CompareRanks(phase2aMessage.Rnd, _rnd) >= 0)
        {
            _rnd = phase2aMessage.Rnd;
            _vrnd = phase2aMessage.Rnd;
            _vval = new List<Endpoint>(phase2aMessage.Vval);
            
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

        if (!CompareRanksEqual(phase2bMessage.Rnd, _crnd))
        {
            return;
        }

        if (!_acceptResponses.ContainsKey(_crnd))
        {
            _acceptResponses[_crnd] = new Dictionary<Endpoint, Phase2bMessage>();
        }
        
        _acceptResponses[_crnd][phase2bMessage.Sender] = phase2bMessage;
        
        var f = (int)Math.Floor((_n - 1) / 4.0);
        if (_acceptResponses[_crnd].Count >= _n - f && !_decided)
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
            .GroupBy(m => m.Vrnd, new RankComparer())
            .OrderByDescending(g => g.Key, new RankComparer())
            .ToList();

        if (valuesByVrnd.Count == 0)
        {
            return new List<Endpoint>();
        }

        var maxVrnd = valuesByVrnd.First().Key;
        var valuesWithMaxVrnd = valuesByVrnd.First().Select(m => m.Vval.ToList()).ToList();
        
        // Check if all values are the same
        var firstValue = valuesWithMaxVrnd[0];
        if (valuesWithMaxVrnd.All(v => AreListsEqual(v, firstValue)))
        {
            return firstValue;
        }

        // Find the value with the most votes
        var valueCounts = new Dictionary<List<Endpoint>, int>(new ListEndpointComparer());
        foreach (var value in valuesWithMaxVrnd)
        {
            if (!valueCounts.ContainsKey(value))
            {
                valueCounts[value] = 0;
            }
            valueCounts[value]++;
        }

        var maxCount = valueCounts.Values.Max();
        var f = (int)Math.Floor((n - 1) / 4.0);
        
        if (maxCount >= (n - f) / 2)
        {
            return valueCounts.First(kv => kv.Value == maxCount).Key;
        }

        return new List<Endpoint>();
    }

    private static bool AreListsEqual(IList<Endpoint> list1, IList<Endpoint> list2)
    {
        if (list1.Count != list2.Count) return false;
        for (int i = 0; i < list1.Count; i++)
        {
            if (!list1[i].Equals(list2[i])) return false;
        }
        return true;
    }

    private static int CompareRanks(Rank r1, Rank r2)
    {
        var roundCmp = r1.Round.CompareTo(r2.Round);
        if (roundCmp != 0) return roundCmp;
        return r1.NodeIndex.CompareTo(r2.NodeIndex);
    }

    private static bool CompareRanksEqual(Rank r1, Rank r2)
    {
        return r1.Round == r2.Round && r1.NodeIndex == r2.NodeIndex;
    }

    private class RankComparer : IEqualityComparer<Rank>, IComparer<Rank>
    {
        public bool Equals(Rank? x, Rank? y)
        {
            if (x == null && y == null) return true;
            if (x == null || y == null) return false;
            return CompareRanksEqual(x, y);
        }

        public int GetHashCode(Rank obj)
        {
            return HashCode.Combine(obj.Round, obj.NodeIndex);
        }

        public int Compare(Rank? x, Rank? y)
        {
            if (x == null && y == null) return 0;
            if (x == null) return -1;
            if (y == null) return 1;
            return CompareRanks(x, y);
        }
    }

    private class ListEndpointComparer : IEqualityComparer<List<Endpoint>>
    {
        public bool Equals(List<Endpoint>? x, List<Endpoint>? y)
        {
            if (x == null && y == null) return true;
            if (x == null || y == null) return false;
            return AreListsEqual(x, y);
        }

        public int GetHashCode(List<Endpoint> obj)
        {
            var hash = new HashCode();
            foreach (var endpoint in obj)
            {
                hash.Add(endpoint.GetHashCode());
            }
            return hash.ToHashCode();
        }
    }
}
