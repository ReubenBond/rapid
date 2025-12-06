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
/// Single-decree consensus. We always start with a Fast round.
/// </summary>
internal sealed class FastPaxos
{
    private readonly ILogger<FastPaxos> _logger;
    private readonly double _jitterRate;
    private readonly Endpoint _myAddr;
    private readonly long _configurationId;
    private readonly long _membershipSize;
    private readonly Action<List<Endpoint>> _onDecidedWrapped;
    private readonly IBroadcaster _broadcaster;
    private readonly Dictionary<List<Endpoint>, int> _votesPerProposal = new(new ListEndpointComparer());
    private readonly HashSet<Endpoint> _votesReceived = [];
    private readonly Paxos _paxos;
    private readonly Lock _paxosLock = new();
    private bool _decided = false;
    private CancellationTokenSource? _scheduledClassicRoundCts;
    private readonly Settings _settings;

    public FastPaxos(Endpoint myAddr, long configurationId, int membershipSize,
                     IMessagingClient client, IBroadcaster broadcaster,
                     SharedResources sharedResources, Action<List<Endpoint>> onDecide,
                     Settings settings, ILoggerFactory? loggerFactory = null)
    {
        _myAddr = myAddr;
        _configurationId = configurationId;
        _membershipSize = membershipSize;
        _broadcaster = broadcaster;
        _settings = settings;
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
    /// <param name="recoveryDelayInMs">Delay before starting classic Paxos round</param>
    public void Propose(List<Endpoint> proposal, long recoveryDelayInMs)
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

        var proposalMessage = Utils.ToRapidRequest(consensusMessage);
        _ = _broadcaster.BroadcastAsync(proposalMessage);

        _logger.LogTrace("Scheduling classic round with delay: {Delay}", recoveryDelayInMs);
        _scheduledClassicRoundCts = new CancellationTokenSource();
        _ = Task.Delay(TimeSpan.FromMilliseconds(recoveryDelayInMs), _scheduledClassicRoundCts.Token)
            .ContinueWith(_ => StartClassicPaxosRound(), TaskScheduler.Default);
    }

    /// <summary>
    /// Propose a value for a fast round.
    /// </summary>
    /// <param name="proposal">The membership change proposal towards a configuration change.</param>
    public void Propose(List<Endpoint> proposal)
    {
        Propose(proposal, GetRandomDelayMs());
    }

    /// <summary>
    /// Invoked by the membership service when it receives a proposal for a fast round.
    /// </summary>
    private void HandleFastRoundProposal(FastRoundPhase2bMessage proposalMessage)
    {
        if (proposalMessage.ConfigurationId != _configurationId)
        {
            _logger.LogTrace("Configuration ID mismatch for proposal: current_config:{CurrentConfig}", _configurationId);
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
        if (!_votesPerProposal.ContainsKey(proposalList))
        {
            _votesPerProposal[proposalList] = 0;
        }
        _votesPerProposal[proposalList]++;
        
        var count = _votesPerProposal[proposalList];
        var f = (int)Math.Floor((_membershipSize - 1) / 4.0); // Fast Paxos resiliency.
        
        if (_votesReceived.Count >= _membershipSize - f)
        {
            if (count >= _membershipSize - f)
            {
                _logger.LogTrace("Decided on a view change: {Proposal}", string.Join(", ", proposalList));
                // We have a successful proposal. Consume it.
                _onDecidedWrapped(proposalList);
            }
            else
            {
                // fallback protocol here
                _logger.LogTrace("Fast round may not succeed for proposal");
            }
        }
    }

    /// <summary>
    /// Invoked by the membership service when it receives a consensus proposal.
    /// </summary>
    public RapidResponse HandleMessages(RapidRequest request)
    {
        switch (request.ContentCase)
        {
            case RapidRequest.ContentOneofCase.FastRoundPhase2BMessage:
                HandleFastRoundProposal(request.FastRoundPhase2BMessage);
                break;
            case RapidRequest.ContentOneofCase.Phase1AMessage:
                _paxos.HandlePhase1aMessage(request.Phase1AMessage);
                break;
            case RapidRequest.ContentOneofCase.Phase1BMessage:
                _paxos.HandlePhase1bMessage(request.Phase1BMessage);
                break;
            case RapidRequest.ContentOneofCase.Phase2AMessage:
                _paxos.HandlePhase2aMessage(request.Phase2AMessage);
                break;
            case RapidRequest.ContentOneofCase.Phase2BMessage:
                _paxos.HandlePhase2bMessage(request.Phase2BMessage);
                break;
            default:
                throw new ArgumentException($"Unexpected message case: {request.ContentCase}");
        }
        return Utils.ToRapidResponse(new ConsensusResponse());
    }

    /// <summary>
    /// Trigger Paxos phase1a.
    /// </summary>
    public void StartClassicPaxosRound()
    {
        if (!_decided)
        {
            lock (_paxosLock)
            {
                _paxos.StartPhase1a(2);
            }
        }
    }

    /// <summary>
    /// Random expovariate variable plus a base delay.
    /// </summary>
    private long GetRandomDelayMs()
    {
        var jitter = (long)(-1000 * Math.Log(1 - Random.Shared.NextDouble()) / _jitterRate);
        return jitter + _settings.ConsensusFallbackTimeoutBaseDelayMs;
    }
}
