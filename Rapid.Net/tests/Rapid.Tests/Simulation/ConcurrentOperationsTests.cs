using System.Diagnostics.CodeAnalysis;
using Rapid.Tests.Simulation;

namespace Rapid.Tests.SimulationTests;

/// <summary>
/// Tests for concurrent operations using the simulation harness.
/// These tests verify that the cluster handles concurrent joins, failures,
/// and other overlapping operations correctly using per-node suspension.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test naming convention")]
public sealed class ConcurrentOperationsTests : IAsyncLifetime
{
    private SimulationHarness _harness = null!;
    private const int TestSeed = 78912;

    public ValueTask InitializeAsync()
    {
        _harness = new SimulationHarness(seed: TestSeed);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeAsync();
    }

    #region Concurrent Joins (CONC-001 to CONC-005)

    /// <summary>
    /// Tests that multiple nodes can join the cluster in rapid succession.
    /// </summary>
    [Fact]
    public void RapidConcurrentJoins()
    {
        var seedNode = _harness.CreateSeedNode();

        // Add multiple nodes rapidly
        var joiner1 = _harness.CreateJoinerNode(seedNode, nodeId: 1);
        var joiner2 = _harness.CreateJoinerNode(seedNode, nodeId: 2);
        var joiner3 = _harness.CreateJoinerNode(seedNode, nodeId: 3);

        _harness.WaitForConvergence(expectedSize: 4);

        Assert.All(_harness.Nodes, n => Assert.Equal(4, n.MembershipSize));
    }

    /// <summary>
    /// Tests concurrent joins while some nodes are suspended.
    /// Uses per-node suspension to simulate concurrent processing.
    /// </summary>
    [Fact(Skip = "Requires simulation failure detection to propagate and reach consensus - see infrastructure issue")]
    public void JoinsWithSuspendedNodes()
    {
        var seedNode = _harness.CreateSeedNode();
        var joiner1 = _harness.CreateJoinerNode(seedNode, nodeId: 1);
        var joiner2 = _harness.CreateJoinerNode(seedNode, nodeId: 2);

        _harness.WaitForConvergence(expectedSize: 3);

        // Suspend one node
        _harness.SuspendNode(joiner1);

        // Join while node is suspended
        var joiner3 = _harness.CreateJoinerNode(seedNode, nodeId: 3);

        // Resume suspended node
        _harness.ResumeNode(joiner1);

        // All should converge
        _harness.WaitForConvergence(expectedSize: 4);

        Assert.All(_harness.Nodes, n => Assert.Equal(4, n.MembershipSize));
    }

    /// <summary>
    /// Tests joining while multiple nodes are suspended.
    /// </summary>
    [Fact(Skip = "Requires simulation failure detection to propagate and reach consensus - see infrastructure issue")]
    public void JoinWhileMultipleNodesSuspended()
    {
        var nodes = _harness.CreateCluster(size: 5);
        _harness.WaitForConvergence(expectedSize: 5);

        // Suspend minority (2 out of 5)
        _harness.SuspendNode(nodes[3]);
        _harness.SuspendNode(nodes[4]);

        // Join new node - should succeed with 3 active nodes (quorum)
        var newNode = _harness.CreateJoinerNode(nodes[0], nodeId: 5);

        // Resume suspended nodes
        _harness.ResumeNode(nodes[3]);
        _harness.ResumeNode(nodes[4]);

        // All should converge to 6
        _harness.WaitForConvergence(expectedSize: 6);

        Assert.All(_harness.Nodes, n => Assert.Equal(6, n.MembershipSize));
    }

    #endregion

    #region Concurrent Joins and Failures (CONC-010 to CONC-015)

    /// <summary>
    /// Tests that the cluster handles joins during node failures.
    /// </summary>
    [Fact]
    public void JoinDuringNodeFailure()
    {
        var seedNode = _harness.CreateSeedNode();
        var joiner1 = _harness.CreateJoinerNode(seedNode, nodeId: 1);
        var joiner2 = _harness.CreateJoinerNode(seedNode, nodeId: 2);
        var joiner3 = _harness.CreateJoinerNode(seedNode, nodeId: 3);

        _harness.WaitForConvergence(expectedSize: 4);

        // Crash one node
        _harness.CrashNode(joiner3);

        // Join new node while failure detection is in progress
        var joiner4 = _harness.CreateJoinerNode(seedNode, nodeId: 4);

        // Wait for convergence (should have 4 nodes: original 4 - 1 crash + 1 join)
        _harness.WaitForConvergence(expectedSize: 4, maxIterations: 500000);

        Assert.All(_harness.Nodes, n => Assert.Equal(4, n.MembershipSize));
    }

    /// <summary>
    /// Tests that multiple failures and joins interleaved are handled correctly.
    /// </summary>
    [Fact]
    public void InterleavedFailuresAndJoins()
    {
        // Start with 5 nodes
        var nodes = _harness.CreateCluster(size: 5);
        _harness.WaitForConvergence(expectedSize: 5);

        // Crash one node
        _harness.CrashNode(nodes[4]);

        // Join a new node
        var newNode1 = _harness.CreateJoinerNode(nodes[0], nodeId: 5);

        // Crash another node
        _harness.CrashNode(nodes[3]);

        // Join another node
        var newNode2 = _harness.CreateJoinerNode(nodes[0], nodeId: 6);

        // Wait for convergence
        _harness.WaitForConvergence(expectedSize: 5, maxIterations: 500000);

        Assert.Equal(5, _harness.Nodes.Count);
    }

    /// <summary>
    /// Tests that failure during consensus doesn't break the cluster.
    /// </summary>
    [Fact]
    public void FailureDuringConsensusHandled()
    {
        var nodes = _harness.CreateCluster(size: 5);
        _harness.WaitForConvergence(expectedSize: 5);

        // Suspend nodes to delay consensus
        _harness.SuspendNode(nodes[3]);
        _harness.SuspendNode(nodes[4]);

        // Crash a node while some are suspended
        _harness.CrashNode(nodes[2]);

        // Resume suspended nodes
        _harness.ResumeNode(nodes[3]);
        _harness.ResumeNode(nodes[4]);

        // Cluster should eventually converge
        _harness.WaitForConvergence(expectedSize: 4, maxIterations: 500000);

        Assert.All(_harness.Nodes, n => Assert.Equal(4, n.MembershipSize));
    }

    #endregion

    #region Concurrent Leaves and Joins (CONC-020 to CONC-025)

    /// <summary>
    /// Tests that leaves and joins happening close together are handled.
    /// </summary>
    [Fact]
    public void ConcurrentLeaveAndJoin()
    {
        var seedNode = _harness.CreateSeedNode();
        var joiner1 = _harness.CreateJoinerNode(seedNode, nodeId: 1);
        var joiner2 = _harness.CreateJoinerNode(seedNode, nodeId: 2);

        _harness.WaitForConvergence(expectedSize: 3);

        // Leave and join close together
        _harness.RemoveNodeGracefully(joiner2);

        var joiner3 = _harness.CreateJoinerNode(seedNode, nodeId: 3);

        _harness.WaitForConvergence(expectedSize: 3);

        Assert.All(_harness.Nodes, n => Assert.Equal(3, n.MembershipSize));
    }

    /// <summary>
    /// Tests multiple leaves interspersed with joins.
    /// </summary>
    [Fact]
    public void MultipleLeavesWithJoins()
    {
        var nodes = _harness.CreateCluster(size: 5);
        _harness.WaitForConvergence(expectedSize: 5);

        var nodeIdCounter = 5;

        // Leave one, join one, leave another, join another
        _harness.RemoveNodeGracefully(nodes[4]);
        var newNode1 = _harness.CreateJoinerNode(nodes[0], nodeId: nodeIdCounter++);

        _harness.RemoveNodeGracefully(nodes[3]);
        var newNode2 = _harness.CreateJoinerNode(nodes[0], nodeId: nodeIdCounter++);

        _harness.WaitForConvergence(expectedSize: 5);

        Assert.All(_harness.Nodes, n => Assert.Equal(5, n.MembershipSize));
    }

    #endregion

    #region Suspension-Based Concurrency Tests (CONC-030 to CONC-035)

    /// <summary>
    /// Tests that a suspended node doesn't participate in consensus.
    /// </summary>
    [Fact(Skip = "Requires simulation failure detection to propagate and reach consensus - see infrastructure issue")]
    public void SuspendedNodeDoesNotParticipateInConsensus()
    {
        var nodes = _harness.CreateCluster(size: 4);
        _harness.WaitForConvergence(expectedSize: 4);

        // Suspend one node
        _harness.SuspendNode(nodes[3]);
        Assert.True(_harness.IsNodeSuspended(nodes[3]));

        // Join a new node - should succeed with 3 active nodes
        var newNode = _harness.CreateJoinerNode(nodes[0], nodeId: 4);

        // Remaining active nodes should see the new member
        Assert.Equal(5, nodes[0].MembershipSize);
        Assert.Equal(5, nodes[1].MembershipSize);
        Assert.Equal(5, nodes[2].MembershipSize);

        // Suspended node should still have old view
        Assert.Equal(4, nodes[3].MembershipSize);

        // Resume and let it catch up
        _harness.ResumeNode(nodes[3]);
        _harness.WaitForConvergence(expectedSize: 5);

        Assert.All(_harness.Nodes, n => Assert.Equal(5, n.MembershipSize));
    }

    /// <summary>
    /// Tests timed suspension using SuspendNodeFor.
    /// </summary>
    [Fact]
    public void TimedSuspensionWorksCorrectly()
    {
        var nodes = _harness.CreateCluster(size: 3);
        _harness.WaitForConvergence(expectedSize: 3);

        // Suspend node for a duration
        _harness.SuspendNodeFor(nodes[2], TimeSpan.FromSeconds(2));

        Assert.True(_harness.IsNodeSuspended(nodes[2]));

        // Advance time past suspension duration
        _harness.AdvanceTime(TimeSpan.FromSeconds(3));

        // Node should be automatically resumed
        Assert.False(_harness.IsNodeSuspended(nodes[2]));
    }

    /// <summary>
    /// Tests operations while majority of nodes are suspended (should stall).
    /// </summary>
    [Fact(Skip = "Requires simulation failure detection to propagate and reach consensus - see infrastructure issue")]
    public void OperationsStallWhenMajoritySuspended()
    {
        var nodes = _harness.CreateCluster(size: 5);
        _harness.WaitForConvergence(expectedSize: 5);

        // Suspend majority (3 out of 5)
        _harness.SuspendNode(nodes[2]);
        _harness.SuspendNode(nodes[3]);
        _harness.SuspendNode(nodes[4]);

        // Only 2 nodes are active - cannot reach quorum
        // Operations should not complete until majority is restored

        // Resume one node to restore quorum
        _harness.ResumeNode(nodes[2]);

        // Now operations should work
        var newNode = _harness.CreateJoinerNode(nodes[0], nodeId: 5);

        // Resume remaining
        _harness.ResumeNode(nodes[3]);
        _harness.ResumeNode(nodes[4]);

        _harness.WaitForConvergence(expectedSize: 6);

        Assert.All(_harness.Nodes, n => Assert.Equal(6, n.MembershipSize));
    }

    #endregion

    #region Node Stepping Tests (CONC-040 to CONC-045)

    /// <summary>
    /// Tests that StepNode executes exactly one task.
    /// </summary>
    [Fact]
    public void StepNodeExecutesOneTask()
    {
        var seedNode = _harness.CreateSeedNode();
        var initialLogicalTime = _harness.LogicalTime;

        // Step the node once
        var executed = _harness.StepNode(seedNode);

        // If a task was executed, logical time should have increased by 1
        if (executed)
        {
            Assert.Equal(initialLogicalTime + 1, _harness.LogicalTime);
        }
    }

    /// <summary>
    /// Tests that suspended node's StepNode returns false.
    /// </summary>
    [Fact]
    public void StepNodeReturnsFalseWhenSuspended()
    {
        var seedNode = _harness.CreateSeedNode();

        // Suspend the node
        _harness.SuspendNode(seedNode);

        // Step should return false
        var executed = _harness.StepNode(seedNode);
        Assert.False(executed);

        // Resume
        _harness.ResumeNode(seedNode);
    }

    #endregion

    #region Complex Concurrent Scenarios (CONC-050 to CONC-055)

    /// <summary>
    /// Tests a complex scenario with mixed concurrent operations.
    /// </summary>
    [Fact(Skip = "Requires simulation failure detection to propagate and reach consensus - see infrastructure issue")]
    public void ComplexMixedConcurrentOperations()
    {
        // Start with 6 nodes
        var nodes = _harness.CreateCluster(size: 6);
        _harness.WaitForConvergence(expectedSize: 6);

        var nodeIdCounter = 6;

        // Suspend some nodes
        _harness.SuspendNode(nodes[4]);
        _harness.SuspendNode(nodes[5]);

        // Crash one node
        _harness.CrashNode(nodes[3]);

        // Join a new node
        var newNode1 = _harness.CreateJoinerNode(nodes[0], nodeId: nodeIdCounter++);

        // Resume one suspended node
        _harness.ResumeNode(nodes[4]);

        // Join another node
        var newNode2 = _harness.CreateJoinerNode(nodes[0], nodeId: nodeIdCounter++);

        // Resume the last suspended node
        _harness.ResumeNode(nodes[5]);

        // Wait for convergence
        _harness.WaitForConvergence(expectedSize: 7, maxIterations: 500000);

        Assert.All(_harness.Nodes, n => Assert.Equal(7, n.MembershipSize));
    }

    /// <summary>
    /// Tests that the cluster maintains consistency through heavy churn.
    /// </summary>
    [Fact]
    public void MaintainsConsistencyThroughHeavyChurn()
    {
        var seedNode = _harness.CreateSeedNode();
        var joiner1 = _harness.CreateJoinerNode(seedNode, nodeId: 1);
        var joiner2 = _harness.CreateJoinerNode(seedNode, nodeId: 2);

        _harness.WaitForConvergence(expectedSize: 3);

        var nodeIdCounter = 3;

        // Heavy churn: 5 rounds of remove + add
        for (var i = 0; i < 5; i++)
        {
            // Remove last node
            var nodeToRemove = _harness.Nodes.Last();
            _harness.RemoveNodeGracefully(nodeToRemove);

            // Add new node
            var newNode = _harness.CreateJoinerNode(seedNode, nodeId: nodeIdCounter++);

            _harness.WaitForConvergence(expectedSize: 3);
        }

        // Verify final consistency
        Assert.Equal(3, _harness.Nodes.Count);
        Assert.All(_harness.Nodes, n => Assert.Equal(3, n.MembershipSize));

        // Verify all nodes have the same view
        var configIds = _harness.Nodes.Select(n => n.CurrentView.ConfigurationId).Distinct().ToList();
        Assert.Single(configIds);
    }

    /// <summary>
    /// Tests consensus with exactly quorum number of nodes active.
    /// </summary>
    [Fact(Skip = "Requires simulation failure detection to propagate and reach consensus - see infrastructure issue")]
    public void ConsensusWithExactQuorum()
    {
        var nodes = _harness.CreateCluster(size: 5);
        _harness.WaitForConvergence(expectedSize: 5);

        // Suspend 2 nodes - leaving exactly 3 (quorum for 5-node cluster)
        _harness.SuspendNode(nodes[3]);
        _harness.SuspendNode(nodes[4]);

        // Operations should still succeed with quorum
        var newNode = _harness.CreateJoinerNode(nodes[0], nodeId: 5);

        // Active nodes should see the new member
        Assert.Equal(6, nodes[0].MembershipSize);
        Assert.Equal(6, nodes[1].MembershipSize);
        Assert.Equal(6, nodes[2].MembershipSize);

        // Resume suspended nodes
        _harness.ResumeNode(nodes[3]);
        _harness.ResumeNode(nodes[4]);

        _harness.WaitForConvergence(expectedSize: 6);
    }

    #endregion
}
