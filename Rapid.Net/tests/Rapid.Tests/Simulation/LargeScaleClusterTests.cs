using System.Diagnostics.CodeAnalysis;
using Rapid.Tests.Simulation;

namespace Rapid.Tests.SimulationTests;

/// <summary>
/// Tests for large-scale cluster operations using the simulation harness.
/// Covers cluster formation, sequential joins, and parallel joins at scale (10-50 nodes).
/// These tests verify that the consensus protocol and membership management can handle larger cluster sizes.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test naming convention")]
public sealed class LargeScaleClusterTests : IAsyncLifetime
{
    private SimulationHarness _harness = null!;
    private const int TestSeed = 56789;

    public ValueTask InitializeAsync()
    {
        _harness = new SimulationHarness(seed: TestSeed);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeAsync();
    }

    #region Large Cluster Formation (SCALE-001 to SCALE-005)

    /// <summary>
    /// Tests formation of a 10-node cluster.
    /// Verifies all nodes initialize and converge to the same membership view.
    /// </summary>
    [Theory]
    [InlineData(10)]
    [InlineData(15)]
    [InlineData(20)]
    public void LargeClusterFormation(int clusterSize)
    {
        var nodes = _harness.CreateCluster(size: clusterSize);

        _harness.WaitForConvergence(expectedSize: clusterSize);

        Assert.Equal(clusterSize, nodes.Count);
        Assert.All(nodes, n => Assert.True(n.IsInitialized));
        Assert.All(nodes, n => Assert.Equal(clusterSize, n.MembershipSize));
    }

    /// <summary>
    /// Tests that all nodes in a large cluster have consistent configuration IDs.
    /// </summary>
    [Fact]
    public void LargeClusterHasConsistentConfigurationIds()
    {
        var nodes = _harness.CreateCluster(size: 10);

        _harness.WaitForConvergence(expectedSize: 10);

        var configIds = nodes.Select(n => n.CurrentView.ConfigurationId).Distinct().ToList();

        // All nodes should have the same configuration ID
        Assert.Single(configIds);
    }

    /// <summary>
    /// Tests that membership views in a large cluster contain all nodes.
    /// </summary>
    [Fact]
    public void LargeClusterMembershipViewContainsAllNodes()
    {
        var nodes = _harness.CreateCluster(size: 10);

        _harness.WaitForConvergence(expectedSize: 10);

        var referenceView = nodes[0].CurrentView;
        var referenceAddresses = referenceView.Members
            .Select(m => $"{m.Hostname.ToStringUtf8()}:{m.Port}")
            .ToHashSet();

        // Verify all nodes are in the membership
        foreach (var node in nodes)
        {
            var nodeAddress = $"{node.Address.Hostname.ToStringUtf8()}:{node.Address.Port}";
            Assert.Contains(nodeAddress, referenceAddresses);
        }
    }

    #endregion

    #region Sequential Joins at Scale (SCALE-010 to SCALE-015)

    /// <summary>
    /// Tests that 10 nodes can join sequentially.
    /// </summary>
    [Fact]
    public void TenNodesJoinSequentially()
    {
        var seedNode = _harness.CreateSeedNode();

        for (var i = 1; i <= 9; i++)
        {
            var joiner = _harness.CreateJoinerNode(seedNode, nodeId: i);
            Assert.True(joiner.IsInitialized);
            Assert.Equal(i + 1, joiner.MembershipSize);
        }

        _harness.WaitForConvergence(expectedSize: 10);

        Assert.All(_harness.Nodes, n => Assert.Equal(10, n.MembershipSize));
    }

    /// <summary>
    /// Tests that nodes joining sequentially all converge to the same view.
    /// </summary>
    [Fact]
    public void SequentialJoinsConvergeToSameView()
    {
        var seedNode = _harness.CreateSeedNode();
        var joiners = new List<SimulationNode> { seedNode };

        for (var i = 1; i <= 7; i++)
        {
            var joiner = _harness.CreateJoinerNode(seedNode, nodeId: i);
            joiners.Add(joiner);
        }

        _harness.WaitForConvergence(expectedSize: 8);

        // All nodes should have the same view
        var configIds = joiners.Select(n => n.CurrentView.ConfigurationId).Distinct().ToList();
        Assert.Single(configIds);
    }

    /// <summary>
    /// Tests that joins can use different existing nodes as seeds.
    /// </summary>
    [Fact]
    public void JoinsUsingDifferentSeeds()
    {
        var seedNode = _harness.CreateSeedNode();
        var joiner1 = _harness.CreateJoinerNode(seedNode, nodeId: 1);
        var joiner2 = _harness.CreateJoinerNode(seedNode, nodeId: 2);

        _harness.WaitForConvergence(expectedSize: 3);

        // Join using different existing members as "seeds"
        var joiner3 = _harness.CreateJoinerNode(joiner1, nodeId: 3);
        _harness.WaitForConvergence(expectedSize: 4);

        var joiner4 = _harness.CreateJoinerNode(joiner2, nodeId: 4);
        _harness.WaitForConvergence(expectedSize: 5);

        var joiner5 = _harness.CreateJoinerNode(joiner3, nodeId: 5);
        _harness.WaitForConvergence(expectedSize: 6);

        Assert.All(_harness.Nodes, n => Assert.Equal(6, n.MembershipSize));
    }

    #endregion

    #region Joins with Existing Cluster (SCALE-020 to SCALE-025)

    /// <summary>
    /// Tests that 5 nodes can join an existing 5-node cluster.
    /// </summary>
    [Fact]
    public void FiveNodesJoinFiveNodeCluster()
    {
        // Create initial 5-node cluster
        var initialNodes = _harness.CreateCluster(size: 5);
        _harness.WaitForConvergence(expectedSize: 5);

        // Join 5 more nodes
        for (var i = 5; i < 10; i++)
        {
            var joiner = _harness.CreateJoinerNode(initialNodes[0], nodeId: i);
            Assert.True(joiner.IsInitialized);
        }

        _harness.WaitForConvergence(expectedSize: 10);

        Assert.Equal(10, _harness.Nodes.Count);
        Assert.All(_harness.Nodes, n => Assert.Equal(10, n.MembershipSize));
    }

    /// <summary>
    /// Tests that many nodes can join through different entry points.
    /// </summary>
    [Fact]
    public void ManyNodesJoinThroughDifferentEntryPoints()
    {
        // Create initial 3-node cluster
        var initialNodes = _harness.CreateCluster(size: 3);
        _harness.WaitForConvergence(expectedSize: 3);

        // Join 6 more nodes through different entry points (round-robin)
        for (var i = 3; i < 9; i++)
        {
            var entryPoint = initialNodes[i % 3];
            var joiner = _harness.CreateJoinerNode(entryPoint, nodeId: i);
            Assert.True(joiner.IsInitialized);
        }

        _harness.WaitForConvergence(expectedSize: 9);

        Assert.All(_harness.Nodes, n => Assert.Equal(9, n.MembershipSize));
    }

    #endregion

    #region Failures in Large Clusters (SCALE-030 to SCALE-035)

    /// <summary>
    /// Tests that a large cluster can handle single node failure.
    /// </summary>
    [Fact]
    public void LargeClusterHandlesSingleNodeFailure()
    {
        var nodes = _harness.CreateCluster(size: 10);
        _harness.WaitForConvergence(expectedSize: 10);

        // Crash one node
        _harness.CrashNode(nodes[9]);

        // Wait for failure detection
        _harness.WaitForConvergence(expectedSize: 9, maxIterations: 500000);

        Assert.All(_harness.Nodes, n => Assert.Equal(9, n.MembershipSize));
    }

    /// <summary>
    /// Tests that a large cluster can handle multiple node failures.
    /// </summary>
    [Fact]
    public void LargeClusterHandlesMultipleNodeFailures()
    {
        var nodes = _harness.CreateCluster(size: 10);
        _harness.WaitForConvergence(expectedSize: 10);

        // Crash 3 nodes (30% failure, still have 7/10 majority)
        _harness.CrashNode(nodes[7]);
        _harness.CrashNode(nodes[8]);
        _harness.CrashNode(nodes[9]);

        // Wait for failure detection
        _harness.WaitForConvergence(expectedSize: 7, maxIterations: 500000);

        Assert.All(_harness.Nodes, n => Assert.Equal(7, n.MembershipSize));
    }

    /// <summary>
    /// Tests that a large cluster can recover after failures by adding new nodes.
    /// </summary>
    [Fact]
    public void LargeClusterRecoversAfterFailures()
    {
        var nodes = _harness.CreateCluster(size: 10);
        _harness.WaitForConvergence(expectedSize: 10);

        // Crash 3 nodes
        _harness.CrashNode(nodes[7]);
        _harness.CrashNode(nodes[8]);
        _harness.CrashNode(nodes[9]);

        // Wait for failure detection
        _harness.WaitForConvergence(expectedSize: 7, maxIterations: 500000);

        // Add 3 new nodes to recover
        var newNode1 = _harness.CreateJoinerNode(nodes[0], nodeId: 10);
        var newNode2 = _harness.CreateJoinerNode(nodes[0], nodeId: 11);
        var newNode3 = _harness.CreateJoinerNode(nodes[0], nodeId: 12);

        _harness.WaitForConvergence(expectedSize: 10);

        Assert.All(_harness.Nodes, n => Assert.Equal(10, n.MembershipSize));
    }

    #endregion

    #region Graceful Operations in Large Clusters (SCALE-040 to SCALE-045)

    /// <summary>
    /// Tests that nodes can leave gracefully from a large cluster.
    /// </summary>
    [Fact]
    public void LargeClusterHandlesGracefulLeave()
    {
        var nodes = _harness.CreateCluster(size: 10);
        _harness.WaitForConvergence(expectedSize: 10);

        // Graceful leave of one node
        _harness.RemoveNodeGracefully(nodes[9]);
        _harness.WaitForConvergence(expectedSize: 9);

        Assert.All(_harness.Nodes, n => Assert.Equal(9, n.MembershipSize));
    }

    /// <summary>
    /// Tests that multiple nodes can leave gracefully from a large cluster.
    /// </summary>
    [Fact]
    public void LargeClusterHandlesMultipleGracefulLeaves()
    {
        var nodes = _harness.CreateCluster(size: 10);
        _harness.WaitForConvergence(expectedSize: 10);

        // Graceful leave of 3 nodes
        _harness.RemoveNodeGracefully(nodes[9]);
        _harness.WaitForConvergence(expectedSize: 9);

        _harness.RemoveNodeGracefully(nodes[8]);
        _harness.WaitForConvergence(expectedSize: 8);

        _harness.RemoveNodeGracefully(nodes[7]);
        _harness.WaitForConvergence(expectedSize: 7);

        Assert.All(_harness.Nodes, n => Assert.Equal(7, n.MembershipSize));
    }

    #endregion

    #region Mixed Operations (SCALE-050 to SCALE-055)

    /// <summary>
    /// Tests mixed join and leave operations in a large cluster.
    /// </summary>
    [Fact]
    public void LargeClusterHandlesMixedJoinAndLeave()
    {
        var nodes = _harness.CreateCluster(size: 8);
        _harness.WaitForConvergence(expectedSize: 8);

        // Leave one, join two
        _harness.RemoveNodeGracefully(nodes[7]);
        _harness.WaitForConvergence(expectedSize: 7);

        var newNode1 = _harness.CreateJoinerNode(nodes[0], nodeId: 8);
        _harness.WaitForConvergence(expectedSize: 8);

        var newNode2 = _harness.CreateJoinerNode(nodes[0], nodeId: 9);
        _harness.WaitForConvergence(expectedSize: 9);

        Assert.All(_harness.Nodes, n => Assert.Equal(9, n.MembershipSize));
    }

    /// <summary>
    /// Tests that configuration changes are properly tracked as cluster grows and shrinks.
    /// </summary>
    [Fact]
    public void ConfigurationChangesTrackedThroughGrowthAndShrinkage()
    {
        var seedNode = _harness.CreateSeedNode();
        var configIds = new HashSet<long> { seedNode.CurrentView.ConfigurationId };

        // Grow the cluster
        for (var i = 1; i <= 5; i++)
        {
            var joiner = _harness.CreateJoinerNode(seedNode, nodeId: i);
            _harness.WaitForConvergence(expectedSize: i + 1);
            configIds.Add(seedNode.CurrentView.ConfigurationId);
        }

        // Shrink the cluster
        var nodesToRemove = _harness.Nodes.Skip(3).ToList();
        foreach (var node in nodesToRemove)
        {
            _harness.RemoveNodeGracefully(node);
            configIds.Add(seedNode.CurrentView.ConfigurationId);
        }

        // Each membership change should result in a different configuration ID
        // (At minimum, we should have more than 1 unique config ID)
        Assert.True(configIds.Count > 1,
            $"Expected multiple configuration IDs through cluster lifecycle, got {configIds.Count}");
    }

    #endregion

    #region Stress Tests (SCALE-060 to SCALE-063)

    /// <summary>
    /// Tests sustained join/leave churn in a cluster.
    /// </summary>
    [Fact]
    public void SustainedJoinLeaveChurn()
    {
        // Start with a 5-node cluster
        var nodes = _harness.CreateCluster(size: 5);
        _harness.WaitForConvergence(expectedSize: 5);

        var nodeIdCounter = 5;

        // Perform 5 rounds of churn
        for (var round = 0; round < 5; round++)
        {
            // Remove one node
            var nodeToRemove = _harness.Nodes.Last();
            _harness.RemoveNodeGracefully(nodeToRemove);

            var expectedSize = _harness.Nodes.Count;
            _harness.WaitForConvergence(expectedSize: expectedSize);

            // Add one node
            var newNode = _harness.CreateJoinerNode(_harness.Nodes[0], nodeId: nodeIdCounter++);
            _harness.WaitForConvergence(expectedSize: expectedSize + 1);
        }

        // Cluster should be stable with 5 nodes
        Assert.Equal(5, _harness.Nodes.Count);
        Assert.All(_harness.Nodes, n => Assert.Equal(5, n.MembershipSize));
    }

    /// <summary>
    /// Tests rapid sequential joins.
    /// </summary>
    [Fact]
    public void RapidSequentialJoins()
    {
        var seedNode = _harness.CreateSeedNode();

        // Rapidly add 10 nodes
        for (var i = 1; i <= 10; i++)
        {
            var joiner = _harness.CreateJoinerNode(seedNode, nodeId: i);
            Assert.True(joiner.IsInitialized);
        }

        _harness.WaitForConvergence(expectedSize: 11);

        Assert.Equal(11, _harness.Nodes.Count);
        Assert.All(_harness.Nodes, n => Assert.Equal(11, n.MembershipSize));
    }

    #endregion
}
