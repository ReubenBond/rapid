using Rapid.Exceptions;
using Rapid.Pb;

namespace Rapid.Tests;

/// <summary>
/// Tests for a standalone MutableMembershipView object.
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
        var mview = new MutableMembershipView(K);
        var addr = Utils.HostFromParts("127.0.0.1", 123);

        mview.RingAdd(addr, Utils.NodeIdFromUuid(Guid.NewGuid()));

        for (var k = 0; k < K; k++)
        {
            var list = mview.GetRing(k);
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
        var mview = new MutableMembershipView(K);
        const int numNodes = 10;

        for (var i = 0; i < numNodes; i++)
        {
            mview.RingAdd(Utils.HostFromParts("127.0.0.1", i), Utils.NodeIdFromUuid(Guid.NewGuid()));
        }

        for (var k = 0; k < K; k++)
        {
            var list = mview.GetRing(k);
            Assert.Equal(numNodes, list.Count);
        }
    }

    /// <summary>
    /// Add multiple nodes twice and verify whether the rings reject duplicates
    /// </summary>
    [Fact]
    public void RingReAdditions()
    {
        var mview = new MutableMembershipView(K);
        const int numNodes = 10;
        const int startPort = 0;

        for (var i = 0; i < numNodes; i++)
        {
            mview.RingAdd(Utils.HostFromParts("127.0.0.1", startPort + i),
                         Utils.NodeIdFromUuid(Guid.NewGuid()));
        }

        for (var k = 0; k < K; k++)
        {
            var list = mview.GetRing(k);
            Assert.Equal(numNodes, list.Count);
        }

        var numThrows = 0;
        for (var i = 0; i < numNodes; i++)
        {
            try
            {
                mview.RingAdd(Utils.HostFromParts("127.0.0.1", startPort + i),
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
        var mview = new MutableMembershipView(K);
        const int numNodes = 10;
        var numThrows = 0;

        for (var i = 0; i < numNodes; i++)
        {
            try
            {
                mview.RingDelete(Utils.HostFromParts("127.0.0.1", i));
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
        var mview = new MutableMembershipView(K);
        const int numNodes = 10;

        for (var i = 0; i < numNodes; i++)
        {
            mview.RingAdd(Utils.HostFromParts("127.0.0.1", i), Utils.NodeIdFromUuid(Guid.NewGuid()));
        }

        for (var i = 0; i < numNodes; i++)
        {
            mview.RingDelete(Utils.HostFromParts("127.0.0.1", i));
        }

        for (var k = 0; k < K; k++)
        {
            var list = mview.GetRing(k);
            Assert.Empty(list);
        }
    }

    /// <summary>
    /// Verify the edge case of monitoring relationships in a single node case.
    /// </summary>
    [Fact]
    public void MonitoringRelationshipEdge()
    {
        var mview = new MutableMembershipView(K);
        var n1 = Utils.HostFromParts("127.0.0.1", 1);

        mview.RingAdd(n1, Utils.NodeIdFromUuid(Guid.NewGuid()));
        Assert.Empty(mview.GetSubjectsOf(n1));
        Assert.Empty(mview.GetObserversOf(n1));

        var n2 = Utils.HostFromParts("127.0.0.1", 2);

        Assert.Throws<NodeNotInRingException>(() => mview.GetSubjectsOf(n2));
        Assert.Throws<NodeNotInRingException>(() => mview.GetObserversOf(n2));
    }

    /// <summary>
    /// Verify the edge case of monitoring relationships in an empty view case.
    /// </summary>
    [Fact]
    public void MonitoringRelationshipEmpty()
    {
        var mview = new MutableMembershipView(K);
        var n = Utils.HostFromParts("127.0.0.1", 1);

        Assert.Throws<NodeNotInRingException>(() => mview.GetSubjectsOf(n));
        Assert.Throws<NodeNotInRingException>(() => mview.GetObserversOf(n));
    }

    /// <summary>
    /// Verify the monitoring relationships in a two node setting
    /// </summary>
    [Fact]
    public void MonitoringRelationshipTwoNodes()
    {
        var mview = new MutableMembershipView(K);
        var n1 = Utils.HostFromParts("127.0.0.1", 1);
        var n2 = Utils.HostFromParts("127.0.0.1", 2);

        mview.RingAdd(n1, Utils.NodeIdFromUuid(Guid.NewGuid()));
        mview.RingAdd(n2, Utils.NodeIdFromUuid(Guid.NewGuid()));

        Assert.Equal(K, mview.GetSubjectsOf(n1).Count);
        Assert.Equal(K, mview.GetObserversOf(n1).Count);
        Assert.Single(mview.GetSubjectsOf(n1).ToHashSet());
        Assert.Single(mview.GetObserversOf(n1).ToHashSet());
    }

    /// <summary>
    /// Verify the monitoring relationships in a three node setting with delete
    /// </summary>
    [Fact]
    public void MonitoringRelationshipThreeNodesWithDelete()
    {
        var mview = new MutableMembershipView(K);
        var n1 = Utils.HostFromParts("127.0.0.1", 1);
        var n2 = Utils.HostFromParts("127.0.0.1", 2);
        var n3 = Utils.HostFromParts("127.0.0.1", 3);

        mview.RingAdd(n1, Utils.NodeIdFromUuid(Guid.NewGuid()));
        mview.RingAdd(n2, Utils.NodeIdFromUuid(Guid.NewGuid()));
        mview.RingAdd(n3, Utils.NodeIdFromUuid(Guid.NewGuid()));

        Assert.Equal(K, mview.GetSubjectsOf(n1).Count);
        Assert.Equal(K, mview.GetObserversOf(n1).Count);
        Assert.Equal(2, mview.GetSubjectsOf(n1).ToHashSet().Count);
        Assert.Equal(2, mview.GetObserversOf(n1).ToHashSet().Count);

        mview.RingDelete(n2);

        Assert.Equal(K, mview.GetSubjectsOf(n1).Count);
        Assert.Equal(K, mview.GetObserversOf(n1).Count);
        Assert.Single(mview.GetSubjectsOf(n1).ToHashSet());
        Assert.Single(mview.GetObserversOf(n1).ToHashSet());
    }

    /// <summary>
    /// Verify configuration ID changes with membership changes
    /// </summary>
    [Fact]
    public void ConfigurationIdChanges()
    {
        var mview = new MutableMembershipView(K);
        var initialConfig = mview.GetCurrentConfigurationId();

        var n1 = Utils.HostFromParts("127.0.0.1", 1);
        mview.RingAdd(n1, Utils.NodeIdFromUuid(Guid.NewGuid()));

        var configAfterAdd = mview.GetCurrentConfigurationId();
        Assert.NotEqual(initialConfig, configAfterAdd);

        var n2 = Utils.HostFromParts("127.0.0.1", 2);
        mview.RingAdd(n2, Utils.NodeIdFromUuid(Guid.NewGuid()));

        var configAfterSecondAdd = mview.GetCurrentConfigurationId();
        Assert.NotEqual(configAfterAdd, configAfterSecondAdd);

        mview.RingDelete(n1);

        var configAfterDelete = mview.GetCurrentConfigurationId();
        Assert.NotEqual(configAfterSecondAdd, configAfterDelete);
    }

    /// <summary>
    /// Verify membership size tracking
    /// </summary>
    [Fact]
    public void MembershipSize()
    {
        var mview = new MutableMembershipView(K);
        Assert.Equal(0, mview.GetMembershipSize());

        var n1 = Utils.HostFromParts("127.0.0.1", 1);
        mview.RingAdd(n1, Utils.NodeIdFromUuid(Guid.NewGuid()));
        Assert.Equal(1, mview.GetMembershipSize());

        var n2 = Utils.HostFromParts("127.0.0.1", 2);
        mview.RingAdd(n2, Utils.NodeIdFromUuid(Guid.NewGuid()));
        Assert.Equal(2, mview.GetMembershipSize());

        mview.RingDelete(n1);
        Assert.Equal(1, mview.GetMembershipSize());

        mview.RingDelete(n2);
        Assert.Equal(0, mview.GetMembershipSize());
    }

    /// <summary>
    /// Verify IsHostPresent and IsIdentifierPresent methods
    /// </summary>
    [Fact]
    public void HostAndIdentifierPresence()
    {
        var mview = new MutableMembershipView(K);
        var n1 = Utils.HostFromParts("127.0.0.1", 1);
        var nodeId1 = Utils.NodeIdFromUuid(Guid.NewGuid());

        Assert.False(mview.IsHostPresent(n1));
        Assert.False(mview.IsIdentifierPresent(nodeId1));

        mview.RingAdd(n1, nodeId1);

        Assert.True(mview.IsHostPresent(n1));
        Assert.True(mview.IsIdentifierPresent(nodeId1));

        mview.RingDelete(n1);

        Assert.False(mview.IsHostPresent(n1));
        // Note: Identifier remains in the set after deletion to prevent UUID reuse
        Assert.True(mview.IsIdentifierPresent(nodeId1));
    }

    /// <summary>
    /// Verify UUID collision detection
    /// </summary>
    [Fact]
    public void UuidCollisionDetection()
    {
        var mview = new MutableMembershipView(K);
        var n1 = Utils.HostFromParts("127.0.0.1", 1);
        var n2 = Utils.HostFromParts("127.0.0.1", 2);
        var sharedUuid = Utils.NodeIdFromUuid(Guid.NewGuid());

        mview.RingAdd(n1, sharedUuid);

        // Adding a different node with the same UUID should throw
        Assert.Throws<UuidAlreadySeenException>(() => mview.RingAdd(n2, sharedUuid));
    }

    /// <summary>
    /// Verify safe to join checks
    /// </summary>
    [Fact]
    public void SafeToJoinChecks()
    {
        var mview = new MutableMembershipView(K);
        var n1 = Utils.HostFromParts("127.0.0.1", 1);
        var n2 = Utils.HostFromParts("127.0.0.1", 2);
        var nodeId1 = Utils.NodeIdFromUuid(Guid.NewGuid());
        var nodeId2 = Utils.NodeIdFromUuid(Guid.NewGuid());

        // Empty view is always safe to join
        Assert.Equal(JoinStatusCode.SafeToJoin, mview.IsSafeToJoin(n1, nodeId1));

        mview.RingAdd(n1, nodeId1);

        // Different node and ID should be safe
        Assert.Equal(JoinStatusCode.SafeToJoin, mview.IsSafeToJoin(n2, nodeId2));

        // Same node should not be safe
        Assert.NotEqual(JoinStatusCode.SafeToJoin, mview.IsSafeToJoin(n1, nodeId2));

        // Same UUID should not be safe
        Assert.NotEqual(JoinStatusCode.SafeToJoin, mview.IsSafeToJoin(n2, nodeId1));
    }

    /// <summary>
    /// Verify the monitoring relationships in a multi node setting
    /// </summary>
    [Fact]
    public void MonitoringRelationshipMultipleNodes()
    {
        var mview = new MutableMembershipView(K);
        const int numNodes = 1000;
        var list = new List<Endpoint>();

        for (var i = 0; i < numNodes; i++)
        {
            var n = Utils.HostFromParts("127.0.0.1", i);
            list.Add(n);
            mview.RingAdd(n, Utils.NodeIdFromUuid(Guid.NewGuid()));
        }

        for (var i = 0; i < numNodes; i++)
        {
            var numSubjects = mview.GetSubjectsOf(list[i]).Count;
            var numObservers = mview.GetObserversOf(list[i]).Count;
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
        var mview = new MutableMembershipView(K);
        const int serverPort = 1234;
        var n = Utils.HostFromParts("127.0.0.1", serverPort);
        mview.RingAdd(n, Utils.NodeIdFromUuid(Guid.NewGuid()));

        var joiningNode = Utils.HostFromParts("127.0.0.1", serverPort + 1);
        var expectedObservers = mview.GetExpectedObserversOf(joiningNode);
        Assert.Equal(K, expectedObservers.Count);
        Assert.Single(expectedObservers.Distinct());
        Assert.Equal(n, expectedObservers[0]);
    }

    /// <summary>
    /// Verify the monitoring relationships during bootstrap with up to K nodes
    /// </summary>
    [Fact]
    public void MonitoringRelationshipBootstrapMultiple()
    {
        var mview = new MutableMembershipView(K);
        const int numNodes = 20;
        const int serverPortBase = 1234;
        var joiningNode = Utils.HostFromParts("127.0.0.1", serverPortBase - 1);
        var numObservers = 0;

        for (var i = 0; i < numNodes; i++)
        {
            var n = Utils.HostFromParts("127.0.0.1", serverPortBase + i);
            mview.RingAdd(n, Utils.NodeIdFromUuid(Guid.NewGuid()));

            var numObserversActual = mview.GetExpectedObserversOf(joiningNode).Count;
            Assert.True(numObservers <= numObserversActual);
            numObservers = numObserversActual;
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
        var mview = new MutableMembershipView(K);
        var numExceptions = 0;
        var n1 = Utils.HostFromParts("127.0.0.1", 1);
        var id1 = Utils.NodeIdFromUuid(Guid.NewGuid());
        mview.RingAdd(n1, id1);

        var n2 = Utils.HostFromParts("127.0.0.1", 1);
        var id2 = id1.Clone();

        // Same host, same ID
        try
        {
            mview.RingAdd(n2, id2);
        }
        catch (UuidAlreadySeenException)
        {
            numExceptions++;
        }
        Assert.Equal(1, numExceptions);

        // Same host, different ID
        try
        {
            mview.RingAdd(n2, Utils.NodeIdFromUuid(Guid.NewGuid()));
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
            mview.RingAdd(n3, id2);
        }
        catch (UuidAlreadySeenException)
        {
            numExceptions++;
        }
        Assert.Equal(3, numExceptions);

        // Different host, different ID
        try
        {
            mview.RingAdd(n3, Utils.NodeIdFromUuid(Guid.NewGuid()));
        }
        catch (NodeNotInRingException)
        {
            numExceptions++;
        }
        // Should not have triggered an exception
        Assert.Equal(3, numExceptions);

        // Only n1 and n3 should have been added
        Assert.Equal(2, mview.GetRing(0).Count);
    }

    /// <summary>
    /// Test for different combinations of a host and unique ID after it was removed
    /// </summary>
    [Fact]
    public void NodeUniqueIdWithDeletions()
    {
        var mview = new MutableMembershipView(K);
        var n1 = Utils.HostFromParts("127.0.0.1", 1);
        var id1 = Utils.NodeIdFromUuid(Guid.NewGuid());
        mview.RingAdd(n1, id1);

        var n2 = Utils.HostFromParts("127.0.0.1", 2);
        var id2 = Utils.NodeIdFromUuid(Guid.NewGuid());

        // Same host, same ID
        mview.RingAdd(n2, id2);

        // Node is removed from the ring
        mview.RingDelete(n2);
        Assert.Single(mview.GetRing(0));

        var numExceptions = 0;
        // Node rejoins with id2
        try
        {
            mview.RingAdd(n2, id2);
        }
        catch (UuidAlreadySeenException)
        {
            numExceptions++;
        }
        Assert.Equal(1, numExceptions);

        // Re-attempt with new ID
        mview.RingAdd(n2, Utils.NodeIdFromUuid(Guid.NewGuid()));
        Assert.Equal(2, mview.GetRing(0).Count);
    }

    /// <summary>
    /// Ensure that N different configuration IDs are generated when N nodes are added to the rings
    /// </summary>
    [Fact]
    public void NodeConfigurationChange()
    {
        var mview = new MutableMembershipView(K);
        const int numNodes = 1000;
        var set = new HashSet<long>(numNodes);

        for (var i = 0; i < numNodes; i++)
        {
            var n = Utils.HostFromParts("127.0.0.1", i);
            var nameBasedGuid = Utils.NodeIdFromUuid(
                GuidUtility.Create(GuidUtility.DnsNamespace, n.ToString()));
            mview.RingAdd(n, nameBasedGuid);
            set.Add(mview.GetCurrentConfigurationId());
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
        var mview1 = new MutableMembershipView(K);
        var mview2 = new MutableMembershipView(K);
        const int numNodes = 1000;
        var list1 = new List<long>(numNodes);
        var list2 = new List<long>(numNodes);

        for (var i = 0; i < numNodes; i++)
        {
            var n = Utils.HostFromParts("127.0.0.1", i);
            var nameBasedGuid = Utils.NodeIdFromUuid(
                GuidUtility.Create(GuidUtility.DnsNamespace, n.ToString()));
            mview1.RingAdd(n, nameBasedGuid);
            list1.Add(mview1.GetCurrentConfigurationId());
        }

        for (var i = numNodes - 1; i >= 0; i--)
        {
            var n = Utils.HostFromParts("127.0.0.1", i);
            var nameBasedGuid = Utils.NodeIdFromUuid(
                GuidUtility.Create(GuidUtility.DnsNamespace, n.ToString()));
            mview2.RingAdd(n, nameBasedGuid);
            list2.Add(mview2.GetCurrentConfigurationId());
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
    /// Verify that ToImmutableView creates a proper immutable snapshot
    /// </summary>
    [Fact]
    public void ImmutableViewSnapshot()
    {
        var mview = new MutableMembershipView(K);
        var n1 = Utils.HostFromParts("127.0.0.1", 1);
        var nodeId1 = Utils.NodeIdFromUuid(Guid.NewGuid());
        mview.RingAdd(n1, nodeId1);

        var immutableView = mview.ToImmutableView();

        // Verify the immutable view has correct values
        Assert.Equal(K, immutableView.K);
        Assert.Equal(mview.GetCurrentConfigurationId(), immutableView.ConfigurationId);
        Assert.Single(immutableView.Members);
        Assert.Equal(n1, immutableView.Members[0]);
        Assert.Single(immutableView.NodeIds);

        // Add another node to the mutable view
        var n2 = Utils.HostFromParts("127.0.0.1", 2);
        mview.RingAdd(n2, Utils.NodeIdFromUuid(Guid.NewGuid()));

        // The immutable view should still show the old state
        Assert.Single(immutableView.Members);
        Assert.NotEqual(mview.GetCurrentConfigurationId(), immutableView.ConfigurationId);

        // Get a new snapshot which should have the updated state
        var newImmutableView = mview.ToImmutableView();
        Assert.Equal(2, newImmutableView.Members.Count);
        Assert.Equal(mview.GetCurrentConfigurationId(), newImmutableView.ConfigurationId);
    }
}

/// <summary>
/// Helper class for creating deterministic GUIDs from names
/// </summary>
internal static class GuidUtility
{
    public static readonly Guid DnsNamespace = new Guid("6ba7b810-9dad-11d1-80b4-00c04fd430c8");

    public static Guid Create(Guid namespaceId, string name)
    {
        var nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
        var namespaceBytes = namespaceId.ToByteArray();

        SwapByteOrder(namespaceBytes);

#pragma warning disable CA5350
        var hash = System.Security.Cryptography.SHA1.HashData(namespaceBytes.Concat(nameBytes).ToArray());
#pragma warning restore CA5350

        var newGuid = new byte[16];
        Array.Copy(hash, newGuid, 16);

        newGuid[6] = (byte)((newGuid[6] & 0x0F) | 0x50);
        newGuid[8] = (byte)((newGuid[8] & 0x3F) | 0x80);

        SwapByteOrder(newGuid);
        return new Guid(newGuid);
    }

    private static void SwapByteOrder(byte[] guid)
    {
        SwapBytes(guid, 0, 3);
        SwapBytes(guid, 1, 2);
        SwapBytes(guid, 4, 5);
        SwapBytes(guid, 6, 7);
    }

    private static void SwapBytes(byte[] guid, int left, int right) => (guid[left], guid[right]) = (guid[right], guid[left]);
}

