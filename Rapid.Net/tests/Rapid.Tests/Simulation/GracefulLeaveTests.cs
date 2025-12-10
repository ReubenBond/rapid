using System.Diagnostics.CodeAnalysis;
using Rapid.Pb;
using Rapid.Tests.Simulation;

namespace Rapid.Tests.SimulationTests;

/// <summary>
/// Tests for graceful leave scenarios using the simulation harness.
/// Graceful leave involves a node announcing its departure and waiting for consensus
/// before shutting down, ensuring the cluster smoothly transitions to the new membership.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test naming convention")]
public sealed class GracefulLeaveTests : IAsyncLifetime
{
    private SimulationHarness _harness = null!;
    private const int TestSeed = 67890;
    private readonly List<AsyncEnumerablePoller<ClusterEventNotification>> _consumers = [];

    public ValueTask InitializeAsync()
    {
        _harness = new SimulationHarness(seed: TestSeed);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var consumer in _consumers)
        {
            await consumer.DisposeAsync().ConfigureAwait(false);
        }
        _consumers.Clear();

        await _harness.DisposeAsync();
    }

    /// <summary>
    /// Creates an event consumer for the node and registers it for cleanup during disposal.
    /// </summary>
    private AsyncEnumerablePoller<ClusterEventNotification> CreateEventConsumer(SimulationNode node)
    {
        var consumer = new AsyncEnumerablePoller<ClusterEventNotification>(node.EventStream);
        _consumers.Add(consumer);
        return consumer;
    }

    /// <summary>
    /// Drains all available events from the consumer and counts ViewChange events.
    /// </summary>
    private static int CountViewChangeEvents(AsyncEnumerablePoller<ClusterEventNotification> consumer)
    {
        var count = 0;
        while (consumer.Poll() is { } notification)
        {
            if (notification.Event == ClusterEvents.ViewChange)
            {
                count++;
            }
        }
        return count;
    }

    /// <summary>
    /// Drains all available events from the consumer and collects membership sizes from ViewChange events.
    /// </summary>
    private static List<int> CollectViewChangeMembershipSizes(AsyncEnumerablePoller<ClusterEventNotification> consumer)
    {
        var sizes = new List<int>();
        while (consumer.Poll() is { } notification)
        {
            if (notification.Event == ClusterEvents.ViewChange)
            {
                sizes.Add(notification.Change.Membership.Count);
            }
        }
        return sizes;
    }

    #region Basic Graceful Leave (LEAVE-001 to LEAVE-005)

    [Fact]
    public void GracefulLeave_SingleNode_RemainingNodesConverge()
    {
        // Arrange: Create a 3-node cluster
        var nodes = _harness.CreateCluster(size: 3);
        _harness.WaitForConvergence(expectedSize: 3);

        var leavingNode = nodes[2];
        var remainingNodes = new[] { nodes[0], nodes[1] };

        // Act: Graceful leave
        _harness.RemoveNodeGracefully(leavingNode);

        // Assert: Remaining nodes converge to size 2
        Assert.All(remainingNodes, n => Assert.Equal(2, n.MembershipSize));
    }

    [Fact]
    public void GracefulLeave_LeavingNodeRemovedFromHarness()
    {
        // Arrange: Create a 3-node cluster
        var nodes = _harness.CreateCluster(size: 3);
        _harness.WaitForConvergence(expectedSize: 3);

        var leavingNode = nodes[2];

        // Act: Graceful leave
        _harness.RemoveNodeGracefully(leavingNode);

        // Assert: Leaving node is removed from harness
        Assert.DoesNotContain(leavingNode, _harness.Nodes);
        Assert.Equal(2, _harness.Nodes.Count);
    }

    [Fact]
    public void GracefulLeave_SeedNode_ClusterContinues()
    {
        // Arrange: Create a 4-node cluster (need 4 for quorum after seed leaves)
        var nodes = _harness.CreateCluster(size: 4);
        _harness.WaitForConvergence(expectedSize: 4);

        var seedNode = nodes[0];
        var remainingNodes = nodes.Skip(1).ToList();

        // Act: Seed node gracefully leaves
        _harness.RemoveNodeGracefully(seedNode);

        // Assert: Remaining nodes converge
        _harness.WaitForConvergence(expectedSize: 3);
        Assert.All(remainingNodes, n => Assert.Equal(3, n.MembershipSize));
    }

    [Fact]
    public void GracefulLeave_MiddleNode_ClusterContinues()
    {
        // Arrange: Create a 4-node cluster
        var nodes = _harness.CreateCluster(size: 4);
        _harness.WaitForConvergence(expectedSize: 4);

        var middleNode = nodes[2];
        var remainingNodes = nodes.Where(n => n != middleNode).ToList();

        // Act: Middle node gracefully leaves
        _harness.RemoveNodeGracefully(middleNode);

        // Assert: Remaining nodes converge
        _harness.WaitForConvergence(expectedSize: 3);
        Assert.All(remainingNodes, n => Assert.Equal(3, n.MembershipSize));
    }

    [Fact]
    public void GracefulLeave_LastJoiner_ClusterContinues()
    {
        // Arrange: Create a 4-node cluster
        var nodes = _harness.CreateCluster(size: 4);
        _harness.WaitForConvergence(expectedSize: 4);

        var lastNode = nodes[3];
        var remainingNodes = nodes.Take(3).ToList();

        // Act: Last joiner gracefully leaves
        _harness.RemoveNodeGracefully(lastNode);

        // Assert: Remaining nodes converge
        _harness.WaitForConvergence(expectedSize: 3);
        Assert.All(remainingNodes, n => Assert.Equal(3, n.MembershipSize));
    }

    #endregion

    #region Sequential Graceful Leaves (LEAVE-010 to LEAVE-015)

    [Fact]
    public void GracefulLeave_TwoNodesSequentially()
    {
        // Arrange: Create a 5-node cluster
        var nodes = _harness.CreateCluster(size: 5);
        _harness.WaitForConvergence(expectedSize: 5);

        // Act: Two nodes leave sequentially
        _harness.RemoveNodeGracefully(nodes[4]);
        _harness.WaitForConvergence(expectedSize: 4);

        _harness.RemoveNodeGracefully(nodes[3]);
        _harness.WaitForConvergence(expectedSize: 3);

        // Assert: Three nodes remain
        Assert.Equal(3, _harness.Nodes.Count);
        Assert.All(_harness.Nodes, n => Assert.Equal(3, n.MembershipSize));
    }

    [Fact]
    public void GracefulLeave_ThreeNodesSequentially()
    {
        // Arrange: Create a 6-node cluster
        var nodes = _harness.CreateCluster(size: 6);
        _harness.WaitForConvergence(expectedSize: 6);

        // Act: Three nodes leave sequentially
        _harness.RemoveNodeGracefully(nodes[5]);
        _harness.WaitForConvergence(expectedSize: 5);

        _harness.RemoveNodeGracefully(nodes[4]);
        _harness.WaitForConvergence(expectedSize: 4);

        _harness.RemoveNodeGracefully(nodes[3]);
        _harness.WaitForConvergence(expectedSize: 3);

        // Assert: Three nodes remain
        Assert.Equal(3, _harness.Nodes.Count);
        Assert.All(_harness.Nodes, n => Assert.Equal(3, n.MembershipSize));
    }

    [Fact]
    public void GracefulLeave_AlternatingNodes()
    {
        // Arrange: Create a 6-node cluster
        var nodes = _harness.CreateCluster(size: 6);
        _harness.WaitForConvergence(expectedSize: 6);

        // Act: Remove alternating nodes (1, 3, 5 - keeping 0, 2, 4)
        _harness.RemoveNodeGracefully(nodes[1]);
        _harness.WaitForConvergence(expectedSize: 5);

        _harness.RemoveNodeGracefully(nodes[3]);
        _harness.WaitForConvergence(expectedSize: 4);

        _harness.RemoveNodeGracefully(nodes[5]);
        _harness.WaitForConvergence(expectedSize: 3);

        // Assert: Correct nodes remain
        Assert.Contains(nodes[0], _harness.Nodes);
        Assert.Contains(nodes[2], _harness.Nodes);
        Assert.Contains(nodes[4], _harness.Nodes);
        Assert.Equal(3, _harness.Nodes.Count);
    }

    [Fact]
    public void GracefulLeave_AllButOneNode()
    {
        // Arrange: Create a 4-node cluster
        var nodes = _harness.CreateCluster(size: 4);
        _harness.WaitForConvergence(expectedSize: 4);

        var keepNode = nodes[0];

        // Act: All nodes except one leave
        _harness.RemoveNodeGracefully(nodes[3]);
        _harness.RemoveNodeGracefully(nodes[2]);
        _harness.RemoveNodeGracefully(nodes[1]);

        // Assert: One node remains (it's still "in the cluster" from its own perspective)
        Assert.Single(_harness.Nodes);
        Assert.Contains(keepNode, _harness.Nodes);
    }

    [Fact]
    public void GracefulLeave_SeedLeavesFirst_ThenOthers()
    {
        // Arrange: Create a 5-node cluster
        var nodes = _harness.CreateCluster(size: 5);
        _harness.WaitForConvergence(expectedSize: 5);

        // Act: Seed leaves first, then others
        _harness.RemoveNodeGracefully(nodes[0]); // seed
        _harness.WaitForConvergence(expectedSize: 4);

        _harness.RemoveNodeGracefully(nodes[4]);
        _harness.WaitForConvergence(expectedSize: 3);

        // Assert: Three nodes remain (1, 2, 3)
        Assert.Equal(3, _harness.Nodes.Count);
        Assert.Contains(nodes[1], _harness.Nodes);
        Assert.Contains(nodes[2], _harness.Nodes);
        Assert.Contains(nodes[3], _harness.Nodes);
    }

    #endregion

    #region Graceful Leave with Subscriptions (LEAVE-020 to LEAVE-025)

    [Fact]
    public void GracefulLeave_TriggersSubscriptionCallback()
    {
        // Arrange: Create a 3-node cluster and set up event consumer before any actions
        var nodes = _harness.CreateCluster(size: 3);
        _harness.WaitForConvergence(expectedSize: 3);

        var consumer = CreateEventConsumer(nodes[0]);

        // Act: One node gracefully leaves
        _harness.RemoveNodeGracefully(nodes[2]);
        _harness.WaitForConvergence(expectedSize: 2);

        // Assert: ViewChange events were emitted
        var viewChangeCount = CountViewChangeEvents(consumer);
        Assert.True(viewChangeCount >= 1, "Membership changed callback should be invoked");
    }

    [Fact]
    public void GracefulLeave_AllRemainingNodesReceiveNotification()
    {
        // Arrange: Create a 4-node cluster and set up event consumers before any actions
        var nodes = _harness.CreateCluster(size: 4);
        _harness.WaitForConvergence(expectedSize: 4);

        var consumers = new[]
        {
            CreateEventConsumer(nodes[0]),
            CreateEventConsumer(nodes[1]),
            CreateEventConsumer(nodes[2])
        };

        // Act: Node 3 gracefully leaves
        _harness.RemoveNodeGracefully(nodes[3]);
        _harness.WaitForConvergence(expectedSize: 3);

        // Assert: All remaining nodes received notification
        var viewChangeCounts = consumers.Select(CountViewChangeEvents).ToArray();
        Assert.All(viewChangeCounts, count => Assert.True(count >= 1));
    }

    [Fact]
    public void GracefulLeave_CallbackIncludesCorrectMembershipSize()
    {
        // Arrange: Create a 4-node cluster and set up event consumer before any actions
        var nodes = _harness.CreateCluster(size: 4);
        _harness.WaitForConvergence(expectedSize: 4);

        var consumer = CreateEventConsumer(nodes[0]);

        // Act: Two nodes leave
        _harness.RemoveNodeGracefully(nodes[3]);
        _harness.WaitForConvergence(expectedSize: 3);

        _harness.RemoveNodeGracefully(nodes[2]);
        _harness.WaitForConvergence(expectedSize: 2);

        // Assert: Observed sizes should include 3 and 2
        var observedSizes = CollectViewChangeMembershipSizes(consumer);
        Assert.Contains(3, observedSizes);
        Assert.Contains(2, observedSizes);
    }

    [Fact]
    public void GracefulLeave_ConfigurationIdChanges()
    {
        // Arrange: Create a 4-node cluster
        var nodes = _harness.CreateCluster(size: 4);
        _harness.WaitForConvergence(expectedSize: 4);

        var initialConfigId = nodes[0].CurrentView.ConfigurationId;

        // Act: Node gracefully leaves
        _harness.RemoveNodeGracefully(nodes[3]);
        _harness.WaitForConvergence(expectedSize: 3);

        // Assert: Configuration ID changed and version increased
        var newConfigId = nodes[0].CurrentView.ConfigurationId;
        Assert.NotEqual(initialConfigId, newConfigId);
        Assert.True(newConfigId.Version > initialConfigId.Version,
            $"Version should increase after leave. Initial: {initialConfigId.Version}, Current: {newConfigId.Version}");
    }

    [Fact]
    public void GracefulLeave_MultipleLeaves_ConfigurationIdIncrementsEachTime()
    {
        // Arrange: Create a 5-node cluster
        var nodes = _harness.CreateCluster(size: 5);
        _harness.WaitForConvergence(expectedSize: 5);

        var configIds = new List<ConfigurationId> { nodes[0].CurrentView.ConfigurationId };

        // Act: Multiple nodes leave
        _harness.RemoveNodeGracefully(nodes[4]);
        _harness.WaitForConvergence(expectedSize: 4);
        configIds.Add(nodes[0].CurrentView.ConfigurationId);

        _harness.RemoveNodeGracefully(nodes[3]);
        _harness.WaitForConvergence(expectedSize: 3);
        configIds.Add(nodes[0].CurrentView.ConfigurationId);

        // Assert: Configuration IDs have strictly increasing versions
        for (var i = 1; i < configIds.Count; i++)
        {
            Assert.True(configIds[i].Version > configIds[i - 1].Version,
                $"Version at index {i} ({configIds[i].Version}) should be greater than at {i - 1} ({configIds[i - 1].Version})");
        }
    }

    #endregion

    #region Graceful Leave Under Network Conditions (LEAVE-030 to LEAVE-035)

    [Fact]
    public void GracefulLeave_WithMessageDrops()
    {
        // Arrange: Create a 4-node cluster with message drops
        var nodes = _harness.CreateCluster(size: 4);
        _harness.WaitForConvergence(expectedSize: 4);

        // Enable some message drops
        _harness.Network.MessageDropRate = 0.1; // 10% drop rate

        // Act: Graceful leave with drops
        _harness.RemoveNodeGracefully(nodes[3]);

        // Wait for convergence with higher iteration count due to retries
        _harness.WaitForConvergence(expectedSize: 3, maxIterations: 200000);

        // Clean up
        _harness.Network.MessageDropRate = 0;

        // Assert: Cluster converged despite drops
        Assert.Equal(3, _harness.Nodes.Count);
    }

    [Fact]
    public void GracefulLeave_WithNetworkDelay()
    {
        // Arrange: Create a 4-node cluster with network delays
        var nodes = _harness.CreateCluster(size: 4);
        _harness.WaitForConvergence(expectedSize: 4);

        // Enable delays
        _harness.Network.EnableDelays = true;
        _harness.Network.BaseMessageDelay = TimeSpan.FromMilliseconds(10);
        _harness.Network.MaxJitter = TimeSpan.FromMilliseconds(20);

        // Act: Graceful leave with delays
        _harness.RemoveNodeGracefully(nodes[3]);

        // Wait for convergence
        _harness.WaitForConvergence(expectedSize: 3, maxIterations: 150000);

        // Clean up
        _harness.Network.EnableDelays = false;

        // Assert: Cluster converged
        Assert.Equal(3, _harness.Nodes.Count);
    }

    [Fact]
    public void GracefulLeave_PartitionDuringLeave_EventuallyConverges()
    {
        // Arrange: Create a 5-node cluster
        var nodes = _harness.CreateCluster(size: 5);
        _harness.WaitForConvergence(expectedSize: 5);

        var leavingNode = nodes[4];

        // Create a partition affecting the leaving node
        _harness.PartitionNodes(leavingNode, nodes[1]);

        // Act: Attempt graceful leave
        _harness.RemoveNodeGracefully(leavingNode);

        // Heal partition
        _harness.HealPartition(nodes[4], nodes[1]);

        // Wait for convergence
        _harness.RunUntilIdle(maxSimulatedTime: TimeSpan.FromMinutes(2));

        // Assert: Cluster should eventually converge (leaving node removed)
        Assert.DoesNotContain(leavingNode, _harness.Nodes);
    }

    [Fact]
    public void GracefulLeave_AfterPartitionHeals()
    {
        // Arrange: Create a 4-node cluster
        var nodes = _harness.CreateCluster(size: 4);
        _harness.WaitForConvergence(expectedSize: 4);

        // Create and heal a partition
        _harness.PartitionNodes(nodes[2], nodes[3]);
        _harness.AdvanceTime(TimeSpan.FromSeconds(5));
        _harness.HealPartition(nodes[2], nodes[3]);
        _harness.AdvanceTime(TimeSpan.FromSeconds(5));

        // Act: Now perform graceful leave
        _harness.RemoveNodeGracefully(nodes[3]);
        _harness.WaitForConvergence(expectedSize: 3);

        // Assert: Leave succeeded
        Assert.Equal(3, _harness.Nodes.Count);
    }

    #endregion

    #region Graceful Leave with Concurrent Operations (LEAVE-040 to LEAVE-045)

    [Fact]
    public void GracefulLeave_FollowedByJoin()
    {
        // Arrange: Create a 4-node cluster
        var nodes = _harness.CreateCluster(size: 4);
        _harness.WaitForConvergence(expectedSize: 4);

        // Act: One leaves, then new one joins
        _harness.RemoveNodeGracefully(nodes[3]);
        _harness.WaitForConvergence(expectedSize: 3);

        var newNode = _harness.CreateJoinerNode(nodes[0], nodeId: 10);
        _harness.WaitForConvergence(expectedSize: 4);

        // Assert: New configuration is correct
        Assert.Equal(4, _harness.Nodes.Count);
        Assert.Contains(newNode, _harness.Nodes);
        Assert.DoesNotContain(nodes[3], _harness.Nodes);
    }

    [Fact]
    public void GracefulLeave_PrecededByJoin()
    {
        // Arrange: Create a 3-node cluster
        var nodes = _harness.CreateCluster(size: 3);
        _harness.WaitForConvergence(expectedSize: 3);

        // Act: Join then leave
        var newNode = _harness.CreateJoinerNode(nodes[0], nodeId: 10);
        _harness.WaitForConvergence(expectedSize: 4);

        _harness.RemoveNodeGracefully(nodes[2]);
        _harness.WaitForConvergence(expectedSize: 3);

        // Assert: Correct membership
        Assert.Equal(3, _harness.Nodes.Count);
        Assert.Contains(newNode, _harness.Nodes);
        Assert.DoesNotContain(nodes[2], _harness.Nodes);
    }

    [Fact]
    public void GracefulLeave_MultipleLeavesAndJoins()
    {
        // Arrange: Create a 5-node cluster
        var nodes = _harness.CreateCluster(size: 5);
        _harness.WaitForConvergence(expectedSize: 5);

        // Act: Complex sequence of leaves and joins
        _harness.RemoveNodeGracefully(nodes[4]);
        _harness.WaitForConvergence(expectedSize: 4);

        var newNode1 = _harness.CreateJoinerNode(nodes[0], nodeId: 10);
        _harness.WaitForConvergence(expectedSize: 5);

        _harness.RemoveNodeGracefully(nodes[3]);
        _harness.WaitForConvergence(expectedSize: 4);

        var newNode2 = _harness.CreateJoinerNode(nodes[1], nodeId: 11);
        _harness.WaitForConvergence(expectedSize: 5);

        // Assert: Final state correct
        Assert.Equal(5, _harness.Nodes.Count);
        Assert.Contains(newNode1, _harness.Nodes);
        Assert.Contains(newNode2, _harness.Nodes);
        Assert.DoesNotContain(nodes[3], _harness.Nodes);
        Assert.DoesNotContain(nodes[4], _harness.Nodes);
    }

    [Fact]
    public void GracefulLeave_AfterCrash()
    {
        // Arrange: Create a 5-node cluster
        var nodes = _harness.CreateCluster(size: 5);
        _harness.WaitForConvergence(expectedSize: 5);

        // Crash one node
        _harness.CrashNode(nodes[4]);
        _harness.WaitForConvergence(expectedSize: 4);

        // Act: Graceful leave after crash
        _harness.RemoveNodeGracefully(nodes[3]);
        _harness.WaitForConvergence(expectedSize: 3);

        // Assert: Cluster converged correctly
        Assert.Equal(3, _harness.Nodes.Count);
    }

    [Fact]
    public void GracefulLeave_ThenCrash()
    {
        // Arrange: Create a 5-node cluster
        var nodes = _harness.CreateCluster(size: 5);
        _harness.WaitForConvergence(expectedSize: 5);

        // Act: Graceful leave then crash
        _harness.RemoveNodeGracefully(nodes[4]);
        _harness.WaitForConvergence(expectedSize: 4);

        _harness.CrashNode(nodes[3]);
        _harness.WaitForConvergence(expectedSize: 3);

        // Assert: Cluster converged correctly
        Assert.Equal(3, _harness.Nodes.Count);
    }

    #endregion

    #region Edge Cases (LEAVE-050 to LEAVE-055)

    [Fact]
    public void GracefulLeave_MinimumClusterSize()
    {
        // Arrange: Create minimum viable cluster (3 nodes for consensus)
        var nodes = _harness.CreateCluster(size: 3);
        _harness.WaitForConvergence(expectedSize: 3);

        // Act: Reduce to 2 nodes (still functional but at limit)
        _harness.RemoveNodeGracefully(nodes[2]);
        _harness.WaitForConvergence(expectedSize: 2);

        // Assert: Cluster still functional with 2 nodes
        Assert.Equal(2, _harness.Nodes.Count);
    }

    [Fact]
    public void GracefulLeave_LeavingNodeMetadataRemoved()
    {
        // Arrange: Create a cluster with metadata
        var nodes = _harness.CreateCluster(size: 4);
        _harness.WaitForConvergence(expectedSize: 4);

        var leavingNode = nodes[3];
        var leavingAddress = leavingNode.Address;

        // Act: Graceful leave
        _harness.RemoveNodeGracefully(leavingNode);
        _harness.WaitForConvergence(expectedSize: 3);

        // Assert: Leaving node's address no longer in membership
        var remainingAddresses = nodes[0].CurrentView.Members;
        Assert.DoesNotContain(leavingAddress, remainingAddresses);
    }

    [Fact]
    public void GracefulLeave_RapidSuccessiveLeaves()
    {
        // Arrange: Create a 6-node cluster
        var nodes = _harness.CreateCluster(size: 6);
        _harness.WaitForConvergence(expectedSize: 6);

        // Act: Rapid successive leaves with minimal delay between
        _harness.RemoveNodeGracefully(nodes[5]);
        // Don't wait for full convergence - immediately start next leave
        _harness.RemoveNodeGracefully(nodes[4]);
        _harness.RemoveNodeGracefully(nodes[3]);

        // Now wait for convergence
        _harness.WaitForConvergence(expectedSize: 3, maxIterations: 300000);

        // Assert: Final state correct
        Assert.Equal(3, _harness.Nodes.Count);
    }

    [Fact]
    public void GracefulLeave_VerifyMembershipConsistency()
    {
        // Arrange: Create a 4-node cluster
        var nodes = _harness.CreateCluster(size: 4);
        _harness.WaitForConvergence(expectedSize: 4);

        // Act: Graceful leave
        _harness.RemoveNodeGracefully(nodes[3]);
        _harness.WaitForConvergence(expectedSize: 3);

        // Assert: All remaining nodes have identical membership views
        var membershipSets = _harness.Nodes
            .Select(n => new HashSet<Endpoint>(n.CurrentView.Members))
            .ToList();

        for (var i = 1; i < membershipSets.Count; i++)
        {
            Assert.True(membershipSets[0].SetEquals(membershipSets[i]),
                "All nodes should have identical membership view");
        }
    }

    [Fact]
    public void GracefulLeave_VerifyConfigurationConsistency()
    {
        // Arrange: Create a 4-node cluster
        var nodes = _harness.CreateCluster(size: 4);
        _harness.WaitForConvergence(expectedSize: 4);

        // Act: Graceful leave
        _harness.RemoveNodeGracefully(nodes[3]);
        _harness.WaitForConvergence(expectedSize: 3);

        // Assert: All remaining nodes have same configuration ID
        var configIds = _harness.Nodes.Select(n => n.CurrentView.ConfigurationId).Distinct().ToList();
        Assert.Single(configIds);
    }

    #endregion

    #region Observer-Subject Relationship Tests (LEAVE-060 to LEAVE-065)

    [Fact]
    public void GracefulLeave_ObservedNodeLeaves_ObserverUpdated()
    {
        // Arrange: Create a 4-node cluster where monitoring relationships exist
        var nodes = _harness.CreateCluster(size: 4);
        _harness.WaitForConvergence(expectedSize: 4);

        // In the K=3 model, each node is observed by K other nodes
        // When a node leaves, its observers should update their monitoring

        var leavingNode = nodes[2];

        // Act: Node that's being observed leaves
        _harness.RemoveNodeGracefully(leavingNode);
        _harness.WaitForConvergence(expectedSize: 3);

        // Assert: Cluster restructures and remains healthy
        Assert.Equal(3, _harness.Nodes.Count);
        Assert.All(_harness.Nodes, n => Assert.Equal(3, n.MembershipSize));
    }

    [Fact]
    public void GracefulLeave_ObserverNodeLeaves_SubjectStillMonitored()
    {
        // Arrange: Create a 5-node cluster for more complex monitoring topology
        var nodes = _harness.CreateCluster(size: 5);
        _harness.WaitForConvergence(expectedSize: 5);

        // Remove a node - other nodes will take over monitoring duties
        _harness.RemoveNodeGracefully(nodes[1]);
        _harness.WaitForConvergence(expectedSize: 4);

        // Advance time to trigger failure detection cycles
        _harness.AdvanceTime(TimeSpan.FromSeconds(10));
        _harness.RunUntilIdle();

        // Assert: Remaining nodes are still monitored (cluster stable)
        Assert.Equal(4, _harness.Nodes.Count);
        Assert.All(_harness.Nodes, n => Assert.Equal(4, n.MembershipSize));
    }

    [Fact]
    public void GracefulLeave_MultipleObserversLeave()
    {
        // Arrange: Create a 6-node cluster
        var nodes = _harness.CreateCluster(size: 6);
        _harness.WaitForConvergence(expectedSize: 6);

        // Act: Multiple nodes leave (some may be observers of remaining nodes)
        _harness.RemoveNodeGracefully(nodes[5]);
        _harness.WaitForConvergence(expectedSize: 5);

        _harness.RemoveNodeGracefully(nodes[4]);
        _harness.WaitForConvergence(expectedSize: 4);

        _harness.RemoveNodeGracefully(nodes[3]);
        _harness.WaitForConvergence(expectedSize: 3);

        // Assert: Remaining nodes are healthy
        Assert.Equal(3, _harness.Nodes.Count);
        Assert.All(_harness.Nodes, n => Assert.Equal(3, n.MembershipSize));
    }

    #endregion

    #region Graceful Leave with Suspended Nodes (LEAVE-070 to LEAVE-075)

    [Fact]
    public void GracefulLeave_WithSuspendedNode()
    {
        // Arrange: Create a 5-node cluster
        var nodes = _harness.CreateCluster(size: 5);
        _harness.WaitForConvergence(expectedSize: 5);

        // Suspend one node (not the one leaving)
        _harness.SuspendNode(nodes[3]);

        // Act: Different node leaves gracefully
        _harness.RemoveNodeGracefully(nodes[4]);

        // Resume suspended node
        _harness.ResumeNode(nodes[3]);

        // Wait for convergence
        _harness.WaitForConvergence(expectedSize: 4, maxIterations: 200000);

        // Assert: Cluster converged
        Assert.Equal(4, _harness.Nodes.Count);
    }

    [Fact]
    public void GracefulLeave_SuspendedNodeNotAffected()
    {
        // Arrange: Create a 5-node cluster
        var nodes = _harness.CreateCluster(size: 5);
        _harness.WaitForConvergence(expectedSize: 5);

        var suspendedNode = nodes[2];
        _harness.SuspendNode(suspendedNode);

        // Act: Another node leaves
        _harness.RemoveNodeGracefully(nodes[4]);

        // Resume
        _harness.ResumeNode(suspendedNode);

        // Wait for convergence
        _harness.WaitForConvergence(expectedSize: 4, maxIterations: 200000);

        // Assert: Suspended node is still in cluster
        Assert.Contains(suspendedNode, _harness.Nodes);
        Assert.Equal(4, _harness.Nodes.Count);
    }

    [Fact]
    public void GracefulLeave_ResumeAfterLeaveCompletes()
    {
        // Arrange: Create a 5-node cluster
        var nodes = _harness.CreateCluster(size: 5);
        _harness.WaitForConvergence(expectedSize: 5);

        // Suspend a node
        var suspendedNode = nodes[1];
        _harness.SuspendNode(suspendedNode);

        // Act: Another node leaves while one is suspended
        _harness.RemoveNodeGracefully(nodes[4]);

        // Don't wait - resume immediately
        _harness.ResumeNode(suspendedNode);

        // Now wait for convergence
        _harness.WaitForConvergence(expectedSize: 4, maxIterations: 200000);

        // Assert: Correct final state
        Assert.Equal(4, _harness.Nodes.Count);
        Assert.Contains(suspendedNode, _harness.Nodes);
    }

    #endregion
}
