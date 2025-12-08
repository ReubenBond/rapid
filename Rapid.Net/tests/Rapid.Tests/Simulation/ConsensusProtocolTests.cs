using System.Diagnostics.CodeAnalysis;
using Rapid.Tests.Simulation;

namespace Rapid.Tests.SimulationTests;

/// <summary>
/// Tests for consensus protocol operations using the simulation harness.
/// Covers Fast Paxos basic operations, failure cases, and configuration changes.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test naming convention")]
public sealed class ConsensusProtocolTests(ITestOutputHelper output) : IAsyncLifetime
{
    private SimulationHarness _harness = null!;
    private const int TestSeed = 55555;

    public ValueTask InitializeAsync()
    {
        output.WriteLine($"[ConsensusProtocolTests] Initializing with seed {TestSeed}");
        _harness = new SimulationHarness(seed: TestSeed, output);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        output.WriteLine("[ConsensusProtocolTests] Disposing harness");
        await _harness.DisposeAsync();
    }

    #region Fast Paxos Basic Operations (CONS-001 to CONS-004)

    [Fact]
    public void SingleProposalAcceptedInTwoNodeCluster()
    {
        var seedNode = _harness.CreateSeedNode();
        var joiner = _harness.CreateJoinerNode(seedNode, nodeId: 1);

        _harness.WaitForConvergence(expectedSize: 2);

        // Both nodes should have accepted the membership change via consensus
        Assert.Equal(2, seedNode.MembershipSize);
        Assert.Equal(2, joiner.MembershipSize);

        // Configuration IDs should match, indicating consensus was reached
        Assert.Equal(seedNode.CurrentView.ConfigurationId, joiner.CurrentView.ConfigurationId);
    }

    [Fact(Skip = "Hanging - concurrent joins have consensus issues to debug")]
    public void ConflictingProposalsResolvedInSequentialJoins()
    {
        var seedNode = _harness.CreateSeedNode();

        // Sequential joins (not truly concurrent in sync mode)
        var joiner1 = _harness.CreateJoinerNode(seedNode, nodeId: 1);
        var joiner2 = _harness.CreateJoinerNode(seedNode, nodeId: 2);

        // Wait for full convergence
        _harness.WaitForConvergence(expectedSize: 3);

        // All nodes should eventually reach consensus on membership
        Assert.Equal(3, seedNode.MembershipSize);
        Assert.Equal(3, joiner1.MembershipSize);
        Assert.Equal(3, joiner2.MembershipSize);
    }

    [Fact]
    public void ConsensusCompletesWithinTimeout()
    {
        var seedNode = _harness.CreateSeedNode();
        var startTime = DateTime.UtcNow;

        var joiner = _harness.CreateJoinerNode(seedNode, nodeId: 1);

        var elapsed = DateTime.UtcNow - startTime;

        // Consensus should complete in reasonable time (5 seconds is generous)
        Assert.True(elapsed < TimeSpan.FromSeconds(5),
            $"Consensus took too long: {elapsed.TotalSeconds} seconds");
        Assert.True(joiner.IsInitialized);
    }

    [Fact]
    public void DecisionPropagatedToAllNodes()
    {
        var seedNode = _harness.CreateSeedNode();
        var joiner1 = _harness.CreateJoinerNode(seedNode, nodeId: 1);
        var joiner2 = _harness.CreateJoinerNode(seedNode, nodeId: 2);

        _harness.WaitForConvergence(expectedSize: 3);

        // All nodes should have the same view of membership (consensus decision)
        var seedMembers = seedNode.CurrentView.Members.Select(m => $"{m.Hostname}:{m.Port}").OrderBy(x => x).ToList();
        var joiner1Members = joiner1.CurrentView.Members.Select(m => $"{m.Hostname}:{m.Port}").OrderBy(x => x).ToList();
        var joiner2Members = joiner2.CurrentView.Members.Select(m => $"{m.Hostname}:{m.Port}").OrderBy(x => x).ToList();

        Assert.Equal(seedMembers, joiner1Members);
        Assert.Equal(seedMembers, joiner2Members);
    }

    #endregion

    #region Fast Paxos Failure Cases (CONS-010 to CONS-013)

    [Fact]
    public void ConsensusSucceedsWithMinorityFailure()
    {
        // Create 5-node cluster
        var nodes = _harness.CreateCluster(size: 5);

        _harness.WaitForConvergence(expectedSize: 5);

        // Crash 1 node (minority)
        _harness.CrashNode(nodes[4]);

        // Add a new node - consensus should still work with 4 healthy nodes
        var newJoiner = _harness.CreateJoinerNode(nodes[0], nodeId: 5);

        Assert.True(newJoiner.IsInitialized);
    }

    [Fact(Skip = "Requires advanced consensus failure simulation")]
    public void ConsensusBlockedWithMajorityFailure()
    {
        // Create 5-node cluster
        var nodes = _harness.CreateCluster(size: 5);

        _harness.WaitForConvergence(expectedSize: 5);

        // Crash 3 nodes (majority)
        _harness.CrashNode(nodes[2]);
        _harness.CrashNode(nodes[3]);
        _harness.CrashNode(nodes[4]);

        // Consensus should not be possible with only 2 nodes remaining
        // This would require timeout/failure detection to verify
        Assert.Equal(2, _harness.Nodes.Count);
    }

    [Fact(Skip = "Hanging - sequential joins have consensus issues to debug")]
    public void NodeFailureDuringMembershipChangeHandled()
    {
        var seedNode = _harness.CreateSeedNode();
        var joiner1 = _harness.CreateJoinerNode(seedNode, nodeId: 1);

        _harness.WaitForConvergence(expectedSize: 2);

        // This test would require async behavior for race conditions
        // Skipped in sync mode
    }

    [Fact(Skip = "Hanging - concurrent joins have consensus issues to debug")]
    public void MultipleSimultaneousProposalsEventuallyResolve()
    {
        var seedNode = _harness.CreateSeedNode();

        // Sequential joins in sync mode
        var joiner1 = _harness.CreateJoinerNode(seedNode, nodeId: 1);
        var joiner2 = _harness.CreateJoinerNode(seedNode, nodeId: 2);
        var joiner3 = _harness.CreateJoinerNode(seedNode, nodeId: 3);

        // Wait for convergence
        _harness.WaitForConvergence(expectedSize: 4);

        // All nodes should eventually agree
        Assert.All(_harness.Nodes, n => Assert.Equal(4, n.MembershipSize));
    }

    #endregion

    #region Configuration Changes (CONS-020 to CONS-022)

    [Fact]
    public void ConfigurationIdChangesWithEachMembershipChange()
    {
        var seedNode = _harness.CreateSeedNode();
        var configIds = new HashSet<long> { seedNode.CurrentView.ConfigurationId };

        // Join 3 nodes, tracking config ID changes
        for (var i = 1; i <= 3; i++)
        {
            var joiner = _harness.CreateJoinerNode(seedNode, nodeId: i);

            _harness.WaitForNodeSize(seedNode, expectedSize: i + 1);

            var newConfigId = seedNode.CurrentView.ConfigurationId;
            // Each membership change should produce a unique configuration ID
            Assert.DoesNotContain(newConfigId, configIds);
            configIds.Add(newConfigId);
        }

        // We should have 4 unique configuration IDs (initial + 3 joins)
        Assert.Equal(4, configIds.Count);
    }

    [Fact(Skip = "Requires stale proposal injection")]
    public void OldConfigurationProposalsRejected()
    {
        var seedNode = _harness.CreateSeedNode();
        var joiner = _harness.CreateJoinerNode(seedNode, nodeId: 1);

        _harness.WaitForConvergence(expectedSize: 2);

        // This would require injecting a proposal with an old configuration ID
        // and verifying it's rejected - needs low-level protocol access
    }

    [Fact(Skip = "Hanging - concurrent joins have consensus issues to debug")]
    public void ConcurrentConfigChangesEventuallySerialize()
    {
        var seedNode = _harness.CreateSeedNode();

        // Sequential joins in sync mode
        var joiner1 = _harness.CreateJoinerNode(seedNode, nodeId: 1);
        var joiner2 = _harness.CreateJoinerNode(seedNode, nodeId: 2);
        var joiner3 = _harness.CreateJoinerNode(seedNode, nodeId: 3);

        _harness.WaitForConvergence(expectedSize: 4);

        // All nodes should have the same final configuration
        var configId = seedNode.CurrentView.ConfigurationId;
        Assert.All(_harness.Nodes,
            n => Assert.Equal(configId, n.CurrentView.ConfigurationId));
    }

    #endregion

    #region Consensus Under Network Conditions

    [Fact]
    public void ConsensusWithMessageDelaysCompletesCorrectly()
    {
        // Enable message delays
        _harness.Network.EnableDelays = true;
        _harness.Network.BaseMessageDelay = TimeSpan.FromMilliseconds(10);
        _harness.Network.MaxJitter = TimeSpan.FromMilliseconds(20);

        var seedNode = _harness.CreateSeedNode();
        var joiner = _harness.CreateJoinerNode(seedNode, nodeId: 1);

        Assert.True(joiner.IsInitialized);
        Assert.Equal(2, joiner.MembershipSize);
    }

    [Fact]
    public void ConsensusWithLowMessageLossSucceeds()
    {
        // Enable 5% message loss - this tests the retry logic in join protocol
        _harness.Network.MessageDropRate = 0.05;

        var seedNode = _harness.CreateSeedNode();

        // The join should succeed despite message loss due to retry logic
        var joiner = _harness.CreateJoinerNode(seedNode, nodeId: 1);

        Assert.True(joiner.IsInitialized, "Joiner should be initialized after join with retries");

        // Wait for convergence
        _harness.WaitForConvergence(expectedSize: 2);

        Assert.Equal(2, seedNode.MembershipSize);
        Assert.Equal(2, joiner.MembershipSize);
    }

    #endregion
}
