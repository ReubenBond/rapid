using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Rapid.Pb;

namespace Rapid.Tests.Unit;

/// <summary>
/// Tests for the protocol improvements:
/// 1. Configurable failure detector interval
/// 2. Rate limiting for stale view detection (tested via MembershipService)
/// 3. Early success detection in FastPaxos
/// 4. Timeout for unstable mode in cut detector
/// 5. Deterministic Paxos.ChooseValue
/// </summary>
public class ImprovementsTests
{
    #region 1. Configurable Failure Detector Interval

    [Fact]
    public void RapidProtocolOptions_FailureDetectorInterval_HasDefaultValue()
    {
        var options = new RapidProtocolOptions();
        Assert.Equal(TimeSpan.FromSeconds(1), options.FailureDetectorInterval);
    }

    [Fact]
    public void RapidProtocolOptions_FailureDetectorInterval_CanBeConfigured()
    {
        var options = new RapidProtocolOptions
        {
            FailureDetectorInterval = TimeSpan.FromMilliseconds(500)
        };
        Assert.Equal(TimeSpan.FromMilliseconds(500), options.FailureDetectorInterval);
    }

    #endregion

    #region 2. Rate Limiting for Stale View Detection

    [Fact]
    public void RapidProtocolOptions_StaleViewRefreshInterval_HasDefaultValue()
    {
        var options = new RapidProtocolOptions();
        Assert.Equal(TimeSpan.FromSeconds(1), options.StaleViewRefreshInterval);
    }

    [Fact]
    public void RapidProtocolOptions_StaleViewRefreshInterval_CanBeConfigured()
    {
        var options = new RapidProtocolOptions
        {
            StaleViewRefreshInterval = TimeSpan.FromMilliseconds(2000)
        };
        Assert.Equal(TimeSpan.FromMilliseconds(2000), options.StaleViewRefreshInterval);
    }

    #endregion

    #region 3. Early Success Detection in FastPaxos

    [Fact]
    public async Task FastPaxos_DeclaresSuccess_BeforeAllVotesReceived()
    {
        // In a 5-node cluster, threshold is N - f = 5 - 1 = 4
        // Early success: if a single proposal gets 4 votes, decide immediately
        var myAddr = Utils.HostFromParts("127.0.0.1", 1000);
        var broadcaster = new TestBroadcaster();
        var fastPaxos = new FastPaxos(myAddr, configurationId: 1, membershipSize: 5, broadcaster, NullLogger<FastPaxos>.Instance);

        var proposal = CreateProposal(Utils.HostFromParts("10.0.0.1", 5001));

        // Send 4 votes for the same proposal (threshold = 5 - 1 = 4)
        for (var i = 0; i < 4; i++)
        {
            var msg = new FastRoundPhase2bMessage
            {
                ConfigurationId = 1,
                Sender = Utils.HostFromParts("127.0.0.1", 1000 + i),
                Proposal = proposal
            };
            fastPaxos.HandleFastRoundProposal(msg);
        }

        // Should have decided after exactly 4 votes (not waiting for 5th)
        Assert.True(fastPaxos.Result.IsCompleted);
        var result = await fastPaxos.Result;
        Assert.IsType<ConsensusResult.Decided>(result);
    }

    [Fact]
    public async Task FastPaxos_DetectsVoteSplit_WhenThresholdReached()
    {
        // In a 5-node cluster, threshold is N - f = 5 - 1 = 4
        var myAddr = Utils.HostFromParts("127.0.0.1", 1000);
        var broadcaster = new TestBroadcaster();
        var fastPaxos = new FastPaxos(myAddr, configurationId: 1, membershipSize: 5, broadcaster, NullLogger<FastPaxos>.Instance);

        var proposalA = CreateProposal(Utils.HostFromParts("10.0.0.1", 5001));
        var proposalB = CreateProposal(Utils.HostFromParts("10.0.0.2", 5002));

        // Send 2 votes for proposal A
        for (var i = 0; i < 2; i++)
        {
            var msg = new FastRoundPhase2bMessage
            {
                ConfigurationId = 1,
                Sender = Utils.HostFromParts("127.0.0.1", 1000 + i),
                Proposal = proposalA
            };
            fastPaxos.HandleFastRoundProposal(msg);
        }

        // Send 2 votes for proposal B (total 4 votes, but split)
        for (var i = 0; i < 2; i++)
        {
            var msg = new FastRoundPhase2bMessage
            {
                ConfigurationId = 1,
                Sender = Utils.HostFromParts("127.0.0.2", 2000 + i),
                Proposal = proposalB
            };
            fastPaxos.HandleFastRoundProposal(msg);
        }

        // Should detect vote split when threshold reached but no single proposal has enough
        Assert.True(fastPaxos.Result.IsCompleted);
        var result = await fastPaxos.Result;
        Assert.IsType<ConsensusResult.VoteSplit>(result);
    }

    [Fact]
    public void FastPaxos_DoesNotDecide_BeforeThreshold()
    {
        // In a 5-node cluster, threshold is N - f = 5 - 1 = 4
        var myAddr = Utils.HostFromParts("127.0.0.1", 1000);
        var broadcaster = new TestBroadcaster();
        var fastPaxos = new FastPaxos(myAddr, configurationId: 1, membershipSize: 5, broadcaster, NullLogger<FastPaxos>.Instance);

        var proposal = CreateProposal(Utils.HostFromParts("10.0.0.1", 5001));

        // Send only 3 votes (threshold is 4)
        for (var i = 0; i < 3; i++)
        {
            var msg = new FastRoundPhase2bMessage
            {
                ConfigurationId = 1,
                Sender = Utils.HostFromParts("127.0.0.1", 1000 + i),
                Proposal = proposal
            };
            fastPaxos.HandleFastRoundProposal(msg);
        }

        // Should NOT have decided yet
        Assert.False(fastPaxos.Result.IsCompleted);
    }

    #endregion

    #region 4. Timeout for Unstable Mode in Cut Detector

    [Fact]
    public void RapidProtocolOptions_UnstableModeTimeout_HasDefaultValue()
    {
        var options = new RapidProtocolOptions();
        Assert.Equal(TimeSpan.FromSeconds(5), options.UnstableModeTimeout);
    }

    [Fact]
    public void RapidProtocolOptions_UnstableModeTimeout_CanBeConfigured()
    {
        var options = new RapidProtocolOptions
        {
            UnstableModeTimeout = TimeSpan.FromSeconds(10)
        };
        Assert.Equal(TimeSpan.FromSeconds(10), options.UnstableModeTimeout);
    }

    [Fact]
    public void MultiNodeCutDetector_HasNodesInUnstableMode_ReturnsFalse_Initially()
    {
        var view = CreateTestView(30, 10);
        var detector = new MultiNodeCutDetector(8, 2, view);

        Assert.False(detector.HasNodesInUnstableMode());
    }

    [Fact]
    public void MultiNodeCutDetector_HasNodesInUnstableMode_ReturnsTrue_WhenNodesBetweenLAndH()
    {
        var view = CreateTestView(30, 10);
        var detector = new MultiNodeCutDetector(8, 2, view);
        var dst = Utils.HostFromParts("127.0.0.2", 2);

        // Add L reports (node enters unstable mode)
        for (var i = 0; i < 2; i++)
        {
            detector.AggregateForProposal(CreateAlertMessage(
                Utils.HostFromParts("127.0.0.1", i + 1), dst, EdgeStatus.Up, -1, i));
        }

        Assert.True(detector.HasNodesInUnstableMode());
    }

    [Fact]
    public void MultiNodeCutDetector_HasNodesInUnstableMode_ReturnsFalse_AfterReachingH()
    {
        var view = CreateTestView(30, 10);
        var detector = new MultiNodeCutDetector(8, 2, view);
        var dst = Utils.HostFromParts("127.0.0.2", 2);

        // Add H reports (node leaves unstable mode)
        for (var i = 0; i < 8; i++)
        {
            detector.AggregateForProposal(CreateAlertMessage(
                Utils.HostFromParts("127.0.0.1", i + 1), dst, EdgeStatus.Up, -1, i));
        }

        Assert.False(detector.HasNodesInUnstableMode());
    }

    [Fact]
    public void MultiNodeCutDetector_ForcePromoteUnstableNodes_ReturnsEmpty_WhenNoUnstableNodes()
    {
        var view = CreateTestView(30, 10);
        var detector = new MultiNodeCutDetector(8, 2, view);

        var result = detector.ForcePromoteUnstableNodes();

        Assert.Empty(result);
    }

    [Fact]
    public void MultiNodeCutDetector_ForcePromoteUnstableNodes_PromotesUnstableNodes()
    {
        var view = CreateTestView(30, 10);
        var detector = new MultiNodeCutDetector(8, 2, view);
        var dst1 = Utils.HostFromParts("127.0.0.2", 2);
        var dst2 = Utils.HostFromParts("127.0.0.3", 3);

        // Bring dst1 to H-1 reports (almost stable but still unstable)
        for (var i = 0; i < 7; i++)
        {
            detector.AggregateForProposal(CreateAlertMessage(
                Utils.HostFromParts("127.0.0.1", i + 1), dst1, EdgeStatus.Up, -1, i));
        }

        // Bring dst2 to L reports (unstable)
        for (var i = 0; i < 2; i++)
        {
            detector.AggregateForProposal(CreateAlertMessage(
                Utils.HostFromParts("127.0.0.1", i + 100), dst2, EdgeStatus.Up, -1, i));
        }

        Assert.True(detector.HasNodesInUnstableMode());
        Assert.Equal(0, detector.GetNumProposals());

        // Force promote
        var result = detector.ForcePromoteUnstableNodes();

        // Both nodes should be proposed
        Assert.Equal(2, result.Count);
        Assert.Contains(dst1, result);
        Assert.Contains(dst2, result);
        Assert.False(detector.HasNodesInUnstableMode());
        Assert.Equal(1, detector.GetNumProposals());
    }

    [Fact]
    public void MultiNodeCutDetector_ForcePromoteUnstableNodes_IncludesStableNodesWaiting()
    {
        var view = CreateTestView(30, 10);
        var detector = new MultiNodeCutDetector(8, 2, view);
        var dst1 = Utils.HostFromParts("127.0.0.2", 2);
        var dst2 = Utils.HostFromParts("127.0.0.3", 3);

        // Bring dst1 to H reports (stable, but blocked by dst2)
        for (var i = 0; i < 7; i++)
        {
            detector.AggregateForProposal(CreateAlertMessage(
                Utils.HostFromParts("127.0.0.1", i + 1), dst1, EdgeStatus.Up, -1, i));
        }

        // Bring dst2 to L reports (unstable - blocking dst1)
        for (var i = 0; i < 2; i++)
        {
            detector.AggregateForProposal(CreateAlertMessage(
                Utils.HostFromParts("127.0.0.1", i + 100), dst2, EdgeStatus.Up, -1, i));
        }

        // Now bring dst1 to H (should NOT trigger proposal due to dst2 blocking)
        var intermediateResult = detector.AggregateForProposal(CreateAlertMessage(
            Utils.HostFromParts("127.0.0.1", 8), dst1, EdgeStatus.Up, -1, 7));
        Assert.Empty(intermediateResult); // Blocked

        // Force promote - both should be included
        var result = detector.ForcePromoteUnstableNodes();

        Assert.Equal(2, result.Count);
        Assert.Contains(dst1, result);
        Assert.Contains(dst2, result);
    }

    [Fact]
    public void SimpleCutDetector_HasNodesInUnstableMode_ReturnsTrue_WhenPendingProposals()
    {
        var view = CreateTestView(5, 2); // Small cluster uses SimpleCutDetector
        var detector = new SimpleCutDetector(view);
        var dst = Utils.HostFromParts("127.0.0.2", 2);

        // Add 1 report (pending, needs 2)
        detector.AggregateForProposal(CreateAlertMessage(
            Utils.HostFromParts("127.0.0.1", 1), dst, EdgeStatus.Up, -1, 0));

        Assert.True(detector.HasNodesInUnstableMode());
    }

    [Fact]
    public void SimpleCutDetector_ForcePromoteUnstableNodes_PromotesPendingNodes()
    {
        var view = CreateTestView(5, 2); // Small cluster uses SimpleCutDetector
        var detector = new SimpleCutDetector(view);
        var dst = Utils.HostFromParts("127.0.0.2", 2);

        // Add 1 report (pending, needs 2)
        detector.AggregateForProposal(CreateAlertMessage(
            Utils.HostFromParts("127.0.0.1", 1), dst, EdgeStatus.Up, -1, 0));

        Assert.True(detector.HasNodesInUnstableMode());
        Assert.Equal(0, detector.GetNumProposals());

        // Force promote
        var result = detector.ForcePromoteUnstableNodes();

        Assert.Single(result);
        Assert.Equal(dst, result[0]);
        Assert.False(detector.HasNodesInUnstableMode());
        Assert.Equal(1, detector.GetNumProposals());
    }

    #endregion

    #region 5. Deterministic Paxos.ChooseValue

    [Fact]
    public void ChooseValue_IsDeterministic_WithMultipleDifferentProposals()
    {
        var node1 = Utils.HostFromParts("10.0.0.1", 5001);
        var node2 = Utils.HostFromParts("10.0.0.2", 5002);
        var node3 = Utils.HostFromParts("10.0.0.3", 5003);

        // Run multiple times with the same inputs to verify determinism
        var results = new List<MembershipProposal?>();
        for (var iteration = 0; iteration < 10; iteration++)
        {
            // Create messages in different orders to stress-test determinism
            var messages = new List<Phase1bMessage>
            {
                CreatePhase1bMessage(1, 1, node3), // node3 first
                CreatePhase1bMessage(1, 1, node1), // node1 second
                CreatePhase1bMessage(1, 1, node2)  // node2 third
            };

            // Shuffle messages on each iteration using a seeded approach for deterministic test behavior
#pragma warning disable CA5394 // Random is fine for deterministic test shuffling (not security)
            var random = new Random(iteration * 12345);
            messages = [.. messages.OrderBy(_ => random.Next())];
#pragma warning restore CA5394

            var result = Paxos.ChooseValue(messages, n: 20);
            results.Add(result);
        }

        // All results should be identical (deterministic)
        var firstResult = results[0];
        Assert.NotNull(firstResult);
        Assert.All(results, r =>
        {
            Assert.NotNull(r);
            Assert.True(MembershipProposalComparer.Instance.Equals(firstResult, r));
        });

        // The result should be the "smallest" proposal by lexicographic order
        // node1 (10.0.0.1) < node2 (10.0.0.2) < node3 (10.0.0.3)
        Assert.Equal(node1.Hostname, firstResult.Members[0].Endpoint.Hostname);
        Assert.Equal(node1.Port, firstResult.Members[0].Endpoint.Port);
    }

    [Fact]
    public void ChooseValue_SelectsSmallestProposal_WhenNoMajority()
    {
        // Create proposals with predictable ordering
        var nodeA = Utils.HostFromParts("10.0.0.1", 5001); // Smallest
        var nodeB = Utils.HostFromParts("10.0.0.2", 5002);
        var nodeC = Utils.HostFromParts("10.0.0.3", 5003); // Largest

        // N=20, so N/4 = 5. Each value has only 1 vote, no majority
        var messages = new List<Phase1bMessage>
        {
            CreatePhase1bMessage(1, 1, nodeC), // Largest first (would be picked by non-deterministic FirstOrDefault)
            CreatePhase1bMessage(1, 1, nodeB),
            CreatePhase1bMessage(1, 1, nodeA)  // Smallest last
        };

        var result = Paxos.ChooseValue(messages, n: 20);

        // Should select nodeA (smallest by lexicographic order), not nodeC (first in list)
        Assert.NotNull(result);
        Assert.Single(result.Members);
        Assert.Equal(nodeA.Hostname, result.Members[0].Endpoint.Hostname);
        Assert.Equal(nodeA.Port, result.Members[0].Endpoint.Port);
    }

    [Fact]
    public void MembershipProposalComparer_Compare_OrdersByConfigurationId()
    {
        var node = Utils.HostFromParts("10.0.0.1", 5001);
        var proposalLowConfig = CreateProposal(node, configId: 1);
        var proposalHighConfig = CreateProposal(node, configId: 2);

        var result = MembershipProposalComparer.Instance.Compare(proposalLowConfig, proposalHighConfig);

        Assert.True(result < 0); // Lower config ID comes first
    }

    [Fact]
    public void MembershipProposalComparer_Compare_OrdersByMemberCount()
    {
        var node1 = Utils.HostFromParts("10.0.0.1", 5001);
        var node2 = Utils.HostFromParts("10.0.0.2", 5002);

        var proposalSingle = CreateProposal(node1);
        var proposalDouble = CreateProposal(node1, node2);

        var result = MembershipProposalComparer.Instance.Compare(proposalSingle, proposalDouble);

        Assert.True(result < 0); // Fewer members comes first
    }

    [Fact]
    public void MembershipProposalComparer_Compare_OrdersByEndpoint()
    {
        var nodeA = Utils.HostFromParts("10.0.0.1", 5001);
        var nodeB = Utils.HostFromParts("10.0.0.2", 5002);

        var proposalA = CreateProposal(nodeA);
        var proposalB = CreateProposal(nodeB);

        var result = MembershipProposalComparer.Instance.Compare(proposalA, proposalB);

        Assert.True(result < 0); // nodeA < nodeB
    }

    [Fact]
    public void MembershipProposalComparer_Compare_ReturnsZero_ForEqualProposals()
    {
        var node = Utils.HostFromParts("10.0.0.1", 5001);
        var proposal1 = CreateProposal(node);
        var proposal2 = CreateProposal(node);

        var result = MembershipProposalComparer.Instance.Compare(proposal1, proposal2);

        Assert.Equal(0, result);
    }

    [Fact]
    public void MembershipProposalComparer_Compare_HandlesNull()
    {
        var node = Utils.HostFromParts("10.0.0.1", 5001);
        var proposal = CreateProposal(node);

        Assert.True(MembershipProposalComparer.Instance.Compare(null, proposal) < 0);
        Assert.True(MembershipProposalComparer.Instance.Compare(proposal, null) > 0);
        Assert.Equal(0, MembershipProposalComparer.Instance.Compare(null, null));
    }

    #endregion

    #region Helper Methods

    private static AlertMessage CreateAlertMessage(Endpoint src, Endpoint dst, EdgeStatus status,
        long configurationId, int ringNumber)
    {
        var msg = new AlertMessage
        {
            EdgeSrc = src,
            EdgeDst = dst,
            EdgeStatus = status,
            ConfigurationId = configurationId
        };
        msg.RingNumber.Add(ringNumber);
        return msg;
    }

    private static MembershipView CreateTestView(int numNodes = 30, int k = 10)
    {
        var builder = new MembershipViewBuilder(k);
        for (var i = 0; i < numNodes; i++)
        {
            var node = Utils.HostFromParts("127.0.0." + (i + 1), 1000 + i);
            builder.RingAdd(node, Utils.NodeIdFromUuid(Guid.NewGuid()));
        }
        return builder.Build();
    }

    private static MembershipProposal CreateProposal(params Endpoint[] endpoints)
    {
        return CreateProposal(100, endpoints);
    }

    private static MembershipProposal CreateProposal(Endpoint endpoint, long configId)
    {
        return CreateProposal(configId, endpoint);
    }

    private static MembershipProposal CreateProposal(long configId, params Endpoint[] endpoints)
    {
        var proposal = new MembershipProposal { ConfigurationId = configId };
        var counter = 0;
        foreach (var endpoint in endpoints)
        {
            proposal.Members.Add(new MemberInfo
            {
                Endpoint = endpoint,
                NodeId = CreateNodeId("node" + counter++)
            });
        }
        return proposal;
    }

    private static NodeId CreateNodeId(string id)
    {
        return new NodeId
        {
            High = (long)id.GetHashCode(StringComparison.Ordinal),
            Low = (long)id.GetHashCode(StringComparison.Ordinal) * 31
        };
    }

    private static Phase1bMessage CreatePhase1bMessage(int vrndRound, int vrndNodeIndex, params Endpoint[] endpoints)
    {
        var msg = new Phase1bMessage
        {
            Sender = Utils.HostFromParts("127.0.0.1", 1234),
            ConfigurationId = 100,
            Rnd = new Rank { Round = 2, NodeIndex = 1 },
            Vrnd = new Rank { Round = vrndRound, NodeIndex = vrndNodeIndex }
        };

        if (endpoints.Length > 0)
        {
            msg.Proposal = CreateProposal(endpoints);
        }

        return msg;
    }

    /// <summary>
    /// Simple test broadcaster that does nothing (for unit testing FastPaxos).
    /// </summary>
    private sealed class TestBroadcaster : Rapid.Messaging.IBroadcaster
    {
        public void Broadcast(RapidRequest request, CancellationToken cancellationToken) { }
        public void Broadcast(RapidRequest request, Rapid.Messaging.BroadcastFailureCallback? onDeliveryFailure, CancellationToken cancellationToken) { }
        public void SetMembership(IReadOnlyList<Endpoint> membership) { }
    }

    #endregion
}
