using Rapid.Exceptions;
using Rapid.Pb;

namespace Rapid.Tests;

/// <summary>
/// Tests for MembershipViewBuilder and MembershipView.
/// </summary>
public class MembershipViewTests
{
    private const int K = 10;

    /// <summary>
    /// Add a single node and verify whether it appears on all rings
    /// </summary>
    [Fact]
    public void OneRingAddition()
    {
        var builder = new MembershipViewBuilder(K);
        var addr = Utils.HostFromParts("127.0.0.1", 123);

        builder.RingAdd(addr, Utils.NodeIdFromUuid(Guid.NewGuid()));
        var view = builder.Build();

        for (var k = 0; k < K; k++)
        {
            var list = view.GetRing(k);
            Assert.Single(list);
            foreach (var address in list)
            {
                Assert.Equal(address, addr);
            }
        }
    }

    /// <summary>
    /// Add multiple nodes and verify whether they appear on all rings
    /// </summary>
    [Fact]
    public void MultipleRingAdditions()
    {
        var builder = new MembershipViewBuilder(K);
        const int numNodes = 10;

        for (var i = 0; i < numNodes; i++)
        {
            builder.RingAdd(Utils.HostFromParts("127.0.0.1", i), Utils.NodeIdFromUuid(Guid.NewGuid()));
        }

        var view = builder.Build();

        for (var k = 0; k < K; k++)
        {
            var list = view.GetRing(k);
            Assert.Equal(numNodes, list.Length);
        }
    }

    /// <summary>
    /// Add multiple nodes twice and verify whether the rings reject duplicates
    /// </summary>
    [Fact]
    public void RingReAdditions()
    {
        var builder = new MembershipViewBuilder(K);
        const int numNodes = 10;
        const int startPort = 0;

        for (var i = 0; i < numNodes; i++)
        {
            builder.RingAdd(Utils.HostFromParts("127.0.0.1", startPort + i),
                         Utils.NodeIdFromUuid(Guid.NewGuid()));
        }

        for (var k = 0; k < K; k++)
        {
            var list = builder.GetRing(k);
            Assert.Equal(numNodes, list.Count);
        }

        var numThrows = 0;
        for (var i = 0; i < numNodes; i++)
        {
            try
            {
                builder.RingAdd(Utils.HostFromParts("127.0.0.1", startPort + i),
                             Utils.NodeIdFromUuid(Guid.NewGuid()));
            }
            catch (NodeAlreadyInRingException)
            {
                numThrows++;
            }
        }

        Assert.Equal(numNodes, numThrows);
    }

    /// <summary>
    /// Delete nodes that were never added and verify whether the object rejects those attempts
    /// </summary>
    [Fact]
    public void RingDeletionsOnly()
    {
        var builder = new MembershipViewBuilder(K);
        const int numNodes = 10;
        var numThrows = 0;

        for (var i = 0; i < numNodes; i++)
        {
            try
            {
                builder.RingDelete(Utils.HostFromParts("127.0.0.1", i));
            }
            catch (NodeNotInRingException)
            {
                numThrows++;
            }
        }

        Assert.Equal(numNodes, numThrows);
    }

    /// <summary>
    /// Add nodes and then delete them.
    /// </summary>
    [Fact]
    public void RingAdditionsAndDeletions()
    {
        var builder = new MembershipViewBuilder(K);
        const int numNodes = 10;

        for (var i = 0; i < numNodes; i++)
        {
            builder.RingAdd(Utils.HostFromParts("127.0.0.1", i), Utils.NodeIdFromUuid(Guid.NewGuid()));
        }

        for (var i = 0; i < numNodes; i++)
        {
            builder.RingDelete(Utils.HostFromParts("127.0.0.1", i));
        }

        var view = builder.Build();

        for (var k = 0; k < K; k++)
        {
            var list = view.GetRing(k);
            Assert.Empty(list);
        }
    }

    /// <summary>
    /// Verify the edge case of monitoring relationships in a single node case.
    /// </summary>
    [Fact]
    public void MonitoringRelationshipEdge()
    {
        var builder = new MembershipViewBuilder(K);
        var n1 = Utils.HostFromParts("127.0.0.1", 1);

        builder.RingAdd(n1, Utils.NodeIdFromUuid(Guid.NewGuid()));
        var view = builder.Build();

        Assert.Empty(view.GetSubjectsOf(n1));
        Assert.Empty(view.GetObserversOf(n1));

        var n2 = Utils.HostFromParts("127.0.0.1", 2);

        Assert.Throws<NodeNotInRingException>(() => view.GetSubjectsOf(n2));
        Assert.Throws<NodeNotInRingException>(() => view.GetObserversOf(n2));
    }

    /// <summary>
    /// Verify the edge case of monitoring relationships in an empty view case.
    /// </summary>
    [Fact]
    public void MonitoringRelationshipEmpty()
    {
        var view = new MembershipViewBuilder(K).Build();
        var n = Utils.HostFromParts("127.0.0.1", 1);

        Assert.Throws<NodeNotInRingException>(() => view.GetSubjectsOf(n));
        Assert.Throws<NodeNotInRingException>(() => view.GetObserversOf(n));
    }

    /// <summary>
    /// Verify the monitoring relationships in a two node setting
    /// </summary>
    [Fact]
    public void MonitoringRelationshipTwoNodes()
    {
        var builder = new MembershipViewBuilder(K);
        var n1 = Utils.HostFromParts("127.0.0.1", 1);
        var n2 = Utils.HostFromParts("127.0.0.1", 2);

        builder.RingAdd(n1, Utils.NodeIdFromUuid(Guid.NewGuid()));
        builder.RingAdd(n2, Utils.NodeIdFromUuid(Guid.NewGuid()));
        var view = builder.Build();

        Assert.Equal(K, view.GetSubjectsOf(n1).Length);
        Assert.Equal(K, view.GetObserversOf(n1).Length);
        Assert.Single(view.GetSubjectsOf(n1).ToHashSet());
        Assert.Single(view.GetObserversOf(n1).ToHashSet());
    }

    /// <summary>
    /// Verify the monitoring relationships in a three node setting with delete
    /// </summary>
    [Fact]
    public void MonitoringRelationshipThreeNodesWithDelete()
    {
        var builder = new MembershipViewBuilder(K);
        var n1 = Utils.HostFromParts("127.0.0.1", 1);
        var n2 = Utils.HostFromParts("127.0.0.1", 2);
        var n3 = Utils.HostFromParts("127.0.0.1", 3);

        builder.RingAdd(n1, Utils.NodeIdFromUuid(Guid.NewGuid()));
        builder.RingAdd(n2, Utils.NodeIdFromUuid(Guid.NewGuid()));
        builder.RingAdd(n3, Utils.NodeIdFromUuid(Guid.NewGuid()));
        var view = builder.Build();

        Assert.Equal(K, view.GetSubjectsOf(n1).Length);
        Assert.Equal(K, view.GetObserversOf(n1).Length);
        Assert.Equal(2, view.GetSubjectsOf(n1).ToHashSet().Count);
        Assert.Equal(2, view.GetObserversOf(n1).ToHashSet().Count);

        // Create new view without n2
        var builder2 = view.ToBuilder();
        builder2.RingDelete(n2);
        var view2 = builder2.Build();

        Assert.Equal(K, view2.GetSubjectsOf(n1).Length);
        Assert.Equal(K, view2.GetObserversOf(n1).Length);
        Assert.Single(view2.GetSubjectsOf(n1).ToHashSet());
        Assert.Single(view2.GetObserversOf(n1).ToHashSet());
    }

    /// <summary>
    /// Verify configuration ID changes with membership changes
    /// </summary>
    [Fact]
    public void ConfigurationIdChanges()
    {
        var builder1 = new MembershipViewBuilder(K);
        var view1 = builder1.Build();
        var initialConfig = view1.ConfigurationId;

        var builder2 = view1.ToBuilder();
        var n1 = Utils.HostFromParts("127.0.0.1", 1);
        builder2.RingAdd(n1, Utils.NodeIdFromUuid(Guid.NewGuid()));
        var view2 = builder2.Build();

        var configAfterAdd = view2.ConfigurationId;
        Assert.NotEqual(initialConfig, configAfterAdd);

        var builder3 = view2.ToBuilder();
        var n2 = Utils.HostFromParts("127.0.0.1", 2);
        builder3.RingAdd(n2, Utils.NodeIdFromUuid(Guid.NewGuid()));
        var view3 = builder3.Build();

        var configAfterSecondAdd = view3.ConfigurationId;
        Assert.NotEqual(configAfterAdd, configAfterSecondAdd);

        var builder4 = view3.ToBuilder();
        builder4.RingDelete(n1);
        var view4 = builder4.Build();

        var configAfterDelete = view4.ConfigurationId;
        Assert.NotEqual(configAfterSecondAdd, configAfterDelete);
    }

    /// <summary>
    /// Verify membership size tracking
    /// </summary>
    [Fact]
    public void MembershipSize()
    {
        var builder = new MembershipViewBuilder(K);
        Assert.Equal(0, builder.GetMembershipSize());

        var n1 = Utils.HostFromParts("127.0.0.1", 1);
        builder.RingAdd(n1, Utils.NodeIdFromUuid(Guid.NewGuid()));
        Assert.Equal(1, builder.GetMembershipSize());

        var n2 = Utils.HostFromParts("127.0.0.1", 2);
        builder.RingAdd(n2, Utils.NodeIdFromUuid(Guid.NewGuid()));
        Assert.Equal(2, builder.GetMembershipSize());

        builder.RingDelete(n1);
        Assert.Equal(1, builder.GetMembershipSize());

        builder.RingDelete(n2);
        Assert.Equal(0, builder.GetMembershipSize());
    }

    /// <summary>
    /// Verify IsHostPresent and IsIdentifierPresent methods
    /// </summary>
    [Fact]
    public void HostAndIdentifierPresence()
    {
        var builder = new MembershipViewBuilder(K);
        var n1 = Utils.HostFromParts("127.0.0.1", 1);
        var nodeId1 = Utils.NodeIdFromUuid(Guid.NewGuid());

        Assert.False(builder.IsHostPresent(n1));
        Assert.False(builder.IsIdentifierPresent(nodeId1));

        builder.RingAdd(n1, nodeId1);

        Assert.True(builder.IsHostPresent(n1));
        Assert.True(builder.IsIdentifierPresent(nodeId1));

        builder.RingDelete(n1);

        Assert.False(builder.IsHostPresent(n1));
        // Note: Identifier remains in the set after deletion to prevent UUID reuse
        Assert.True(builder.IsIdentifierPresent(nodeId1));
    }

    /// <summary>
    /// Verify UUID collision detection
    /// </summary>
    [Fact]
    public void UuidCollisionDetection()
    {
        var builder = new MembershipViewBuilder(K);
        var n1 = Utils.HostFromParts("127.0.0.1", 1);
        var n2 = Utils.HostFromParts("127.0.0.1", 2);
        var sharedUuid = Utils.NodeIdFromUuid(Guid.NewGuid());

        builder.RingAdd(n1, sharedUuid);

        // Adding a different node with the same UUID should throw
        Assert.Throws<UuidAlreadySeenException>(() => builder.RingAdd(n2, sharedUuid));
    }

    /// <summary>
    /// Verify safe to join checks
    /// </summary>
    [Fact]
    public void SafeToJoinChecks()
    {
        var builder = new MembershipViewBuilder(K);
        var n1 = Utils.HostFromParts("127.0.0.1", 1);
        var n2 = Utils.HostFromParts("127.0.0.1", 2);
        var nodeId1 = Utils.NodeIdFromUuid(Guid.NewGuid());
        var nodeId2 = Utils.NodeIdFromUuid(Guid.NewGuid());

        // Empty view is always safe to join
        Assert.Equal(JoinStatusCode.SafeToJoin, builder.IsSafeToJoin(n1, nodeId1));

        builder.RingAdd(n1, nodeId1);

        // Different node and ID should be safe
        Assert.Equal(JoinStatusCode.SafeToJoin, builder.IsSafeToJoin(n2, nodeId2));

        // Same node should not be safe
        Assert.NotEqual(JoinStatusCode.SafeToJoin, builder.IsSafeToJoin(n1, nodeId2));

        // Same UUID should not be safe
        Assert.NotEqual(JoinStatusCode.SafeToJoin, builder.IsSafeToJoin(n2, nodeId1));
    }

    /// <summary>
    /// Verify the monitoring relationships in a multi node setting
    /// </summary>
    [Fact]
    public void MonitoringRelationshipMultipleNodes()
    {
        var builder = new MembershipViewBuilder(K);
        const int numNodes = 1000;
        var list = new List<Endpoint>();

        for (var i = 0; i < numNodes; i++)
        {
            var n = Utils.HostFromParts("127.0.0.1", i);
            list.Add(n);
            builder.RingAdd(n, Utils.NodeIdFromUuid(Guid.NewGuid()));
        }

        var view = builder.Build();

        for (var i = 0; i < numNodes; i++)
        {
            var numSubjects = view.GetSubjectsOf(list[i]).Length;
            var numObservers = view.GetObserversOf(list[i]).Length;
            Assert.True(K == numSubjects, $"NumSubjects: {numSubjects}");
            Assert.True(K == numObservers, $"NumObservers: {numObservers}");
        }
    }

    /// <summary>
    /// Verify the monitoring relationships during bootstrap
    /// </summary>
    [Fact]
    public void MonitoringRelationshipBootstrap()
    {
        var builder = new MembershipViewBuilder(K);
        const int serverPort = 1234;
        var n = Utils.HostFromParts("127.0.0.1", serverPort);
        builder.RingAdd(n, Utils.NodeIdFromUuid(Guid.NewGuid()));
        var view = builder.Build();

        var joiningNode = Utils.HostFromParts("127.0.0.1", serverPort + 1);
        var expectedObservers = view.GetExpectedObserversOf(joiningNode);
        Assert.Equal(K, expectedObservers.Length);
        Assert.Single(expectedObservers.Distinct());
        Assert.Equal(n, expectedObservers[0]);
    }

    /// <summary>
    /// Verify the monitoring relationships during bootstrap with up to K nodes
    /// </summary>
    [Fact]
    public void MonitoringRelationshipBootstrapMultiple()
    {
        var builder = new MembershipViewBuilder(K);
        const int numNodes = 20;
        const int serverPortBase = 1234;
        var joiningNode = Utils.HostFromParts("127.0.0.1", serverPortBase - 1);
        var numObservers = 0;

        for (var i = 0; i < numNodes; i++)
        {
            var n = Utils.HostFromParts("127.0.0.1", serverPortBase + i);
            builder.RingAdd(n, Utils.NodeIdFromUuid(Guid.NewGuid()));

            var view = builder.Build();
            var numObserversActual = view.GetExpectedObserversOf(joiningNode).Length;
            Assert.True(numObservers <= numObserversActual);
            numObservers = numObserversActual;

            // Need to create new builder from view to continue adding
            builder = view.ToBuilder();
        }

        // See if we have roughly K observers
        Assert.True(K - 3 <= numObservers);
        Assert.True(K >= numObservers);
    }

    /// <summary>
    /// Test for different combinations of a host joining with a unique ID
    /// </summary>
    [Fact]
    public void NodeUniqueIdNoDeletions()
    {
        var builder = new MembershipViewBuilder(K);
        var numExceptions = 0;
        var n1 = Utils.HostFromParts("127.0.0.1", 1);
        var id1 = Utils.NodeIdFromUuid(Guid.NewGuid());
        builder.RingAdd(n1, id1);

        var n2 = Utils.HostFromParts("127.0.0.1", 1);
        var id2 = id1.Clone();

        // Same host, same ID
        try
        {
            builder.RingAdd(n2, id2);
        }
        catch (UuidAlreadySeenException)
        {
            numExceptions++;
        }
        Assert.Equal(1, numExceptions);

        // Same host, different ID
        try
        {
            builder.RingAdd(n2, Utils.NodeIdFromUuid(Guid.NewGuid()));
        }
        catch (NodeAlreadyInRingException)
        {
            numExceptions++;
        }
        Assert.Equal(2, numExceptions);

        // Different host, same ID
        var n3 = Utils.HostFromParts("127.0.0.1", 2);
        try
        {
            builder.RingAdd(n3, id2);
        }
        catch (UuidAlreadySeenException)
        {
            numExceptions++;
        }
        Assert.Equal(3, numExceptions);

        // Different host, different ID
        try
        {
            builder.RingAdd(n3, Utils.NodeIdFromUuid(Guid.NewGuid()));
        }
        catch (NodeNotInRingException)
        {
            numExceptions++;
        }
        // Should not have triggered an exception
        Assert.Equal(3, numExceptions);

        // Only n1 and n3 should have been added
        Assert.Equal(2, builder.GetRing(0).Count);
    }

    /// <summary>
    /// Test for different combinations of a host and unique ID after it was removed
    /// </summary>
    [Fact]
    public void NodeUniqueIdWithDeletions()
    {
        var builder = new MembershipViewBuilder(K);
        var n1 = Utils.HostFromParts("127.0.0.1", 1);
        var id1 = Utils.NodeIdFromUuid(Guid.NewGuid());
        builder.RingAdd(n1, id1);

        var n2 = Utils.HostFromParts("127.0.0.1", 2);
        var id2 = Utils.NodeIdFromUuid(Guid.NewGuid());

        // Same host, same ID
        builder.RingAdd(n2, id2);

        // Node is removed from the ring
        builder.RingDelete(n2);
        Assert.Single(builder.GetRing(0));

        var numExceptions = 0;
        // Node rejoins with id2
        try
        {
            builder.RingAdd(n2, id2);
        }
        catch (UuidAlreadySeenException)
        {
            numExceptions++;
        }
        Assert.Equal(1, numExceptions);

        // Re-attempt with new ID
        builder.RingAdd(n2, Utils.NodeIdFromUuid(Guid.NewGuid()));
        Assert.Equal(2, builder.GetRing(0).Count);
    }

    /// <summary>
    /// Ensure that N different configuration IDs are generated when N nodes are added to the rings
    /// </summary>
    [Fact]
    public void NodeConfigurationChange()
    {
        var builder = new MembershipViewBuilder(K);
        const int numNodes = 1000;
        var set = new HashSet<long>(numNodes);

        for (var i = 0; i < numNodes; i++)
        {
            var n = Utils.HostFromParts("127.0.0.1", i);
            var nameBasedGuid = Utils.NodeIdFromUuid(
                GuidUtility.Create(GuidUtility.DnsNamespace, n.ToString()));
            builder.RingAdd(n, nameBasedGuid);
            var view = builder.Build();
            set.Add(view.ConfigurationId);
            builder = view.ToBuilder();
        }

        Assert.Equal(numNodes, set.Count); // should be 1000 different configurations
    }

    /// <summary>
    /// Add endpoints to two membership view objects in different orders. 
    /// All except the last generated configuration identifier should be different.
    /// </summary>
    [Fact]
    public void NodeConfigurationsAcrossMViews()
    {
        var builder1 = new MembershipViewBuilder(K);
        var builder2 = new MembershipViewBuilder(K);
        const int numNodes = 1000;
        var list1 = new List<long>(numNodes);
        var list2 = new List<long>(numNodes);

        for (var i = 0; i < numNodes; i++)
        {
            var n = Utils.HostFromParts("127.0.0.1", i);
            var nameBasedGuid = Utils.NodeIdFromUuid(
                GuidUtility.Create(GuidUtility.DnsNamespace, n.ToString()));
            builder1.RingAdd(n, nameBasedGuid);
            var view1 = builder1.Build();
            list1.Add(view1.ConfigurationId);
            builder1 = view1.ToBuilder();
        }

        for (var i = numNodes - 1; i >= 0; i--)
        {
            var n = Utils.HostFromParts("127.0.0.1", i);
            var nameBasedGuid = Utils.NodeIdFromUuid(
                GuidUtility.Create(GuidUtility.DnsNamespace, n.ToString()));
            builder2.RingAdd(n, nameBasedGuid);
            var view2 = builder2.Build();
            list2.Add(view2.ConfigurationId);
            builder2 = view2.ToBuilder();
        }

        Assert.Equal(numNodes, list1.Count);
        Assert.Equal(numNodes, list2.Count);

        // Only the last added elements in the sequence of configurations should have the same value
        for (var i = 0; i < numNodes - 1; i++)
        {
            Assert.NotEqual(list1[i], list2[i]);
        }
        Assert.Equal(list1[numNodes - 1], list2[numNodes - 1]);
    }

    /// <summary>
    /// Verify that ToBuilder creates a proper builder from a view
    /// </summary>
    [Fact]
    public void ToBuilderAndBuild()
    {
        var builder = new MembershipViewBuilder(K);
        var n1 = Utils.HostFromParts("127.0.0.1", 1);
        var nodeId1 = Utils.NodeIdFromUuid(Guid.NewGuid());
        builder.RingAdd(n1, nodeId1);
        var view = builder.Build();

        // Verify the view has correct values
        Assert.Equal(K, view.RingCount);
        Assert.Single(view.Members);
        Assert.Equal(n1, view.Members[0]);
        Assert.Single(view.NodeIds);

        // Create a new builder from the view and add another node
        var builder2 = view.ToBuilder();
        var n2 = Utils.HostFromParts("127.0.0.1", 2);
        builder2.RingAdd(n2, Utils.NodeIdFromUuid(Guid.NewGuid()));
        var view2 = builder2.Build();

        // The original view should still show the old state
        Assert.Single(view.Members);
        Assert.NotEqual(view.ConfigurationId, view2.ConfigurationId);

        // The new view should have the updated state
        Assert.Equal(2, view2.Members.Length);
    }

    /// <summary>
    /// Verify that builder becomes sealed after Build is called
    /// </summary>
    [Fact]
    public void BuilderBecomesSealed()
    {
        var builder = new MembershipViewBuilder(K);
        var n1 = Utils.HostFromParts("127.0.0.1", 1);
        builder.RingAdd(n1, Utils.NodeIdFromUuid(Guid.NewGuid()));

        var view = builder.Build();
        Assert.NotNull(view);

        // Builder should be sealed now - all operations should throw
        Assert.Throws<InvalidOperationException>(() => builder.RingAdd(Utils.HostFromParts("127.0.0.1", 2), Utils.NodeIdFromUuid(Guid.NewGuid())));
        Assert.Throws<InvalidOperationException>(() => builder.RingDelete(n1));
        Assert.Throws<InvalidOperationException>(() => builder.GetRing(0));
        Assert.Throws<InvalidOperationException>(() => builder.GetMembershipSize());
        Assert.Throws<InvalidOperationException>(() => builder.IsHostPresent(n1));
        Assert.Throws<InvalidOperationException>(() => builder.IsIdentifierPresent(Utils.NodeIdFromUuid(Guid.NewGuid())));
        Assert.Throws<InvalidOperationException>(() => builder.IsSafeToJoin(n1, Utils.NodeIdFromUuid(Guid.NewGuid())));
        Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.Throws<InvalidOperationException>(() => _ = builder.RingCount);
    }
}

