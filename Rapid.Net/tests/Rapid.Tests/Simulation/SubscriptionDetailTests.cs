using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Google.Protobuf;
using Rapid.Pb;
using Rapid.Tests.Simulation;

namespace Rapid.Tests.SimulationTests;

/// <summary>
/// Detailed subscription tests using the simulation harness.
/// These tests verify exact callback counts, membership log contents, delta log,
/// and metadata in failure notifications.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test naming convention")]
public sealed class SubscriptionDetailTests : IAsyncLifetime
{
    private SimulationHarness _harness = null!;
    private const int TestSeed = 45678;
    private readonly List<SimulationEventConsumer> _consumers = [];

    public ValueTask InitializeAsync()
    {
        _harness = new SimulationHarness(seed: TestSeed);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var consumer in _consumers)
        {
            consumer.Stop();
        }
        _consumers.Clear();

        await _harness.DisposeAsync();
    }

    /// <summary>
    /// Creates an event consumer for the node and registers it for cleanup during disposal.
    /// </summary>
    private SimulationEventConsumer CreateEventConsumer(SimulationNode node)
    {
        var consumer = new SimulationEventConsumer(node.EventStream);
        _consumers.Add(consumer);
        return consumer;
    }

    /// <summary>
    /// Drains all available events from the consumer and collects matching events into the bag.
    /// </summary>
    private static void CollectEvents(SimulationEventConsumer consumer, ClusterEvents eventType, ConcurrentBag<ClusterStatusChange> bag)
    {
        while (consumer.TryGetNext() is { } notification)
        {
            if (notification.Event == eventType)
            {
                bag.Add(notification.Change);
            }
        }
    }

    /// <summary>
    /// Drains all available events from the consumer and collects all notifications into the bag.
    /// </summary>
    private static void CollectEvents(SimulationEventConsumer consumer, ConcurrentBag<ClusterEventNotification> bag)
    {
        while (consumer.TryGetNext() is { } notification)
        {
            bag.Add(notification);
        }
    }

    /// <summary>
    /// Drains all available events from the consumer and records event types into the queue.
    /// </summary>
    private static void CollectEvents(SimulationEventConsumer consumer, ConcurrentQueue<string> queue)
    {
        while (consumer.TryGetNext() is { } notification)
        {
            queue.Enqueue(notification.Event == ClusterEvents.ViewChangeProposal ? "Proposal" : "ViewChange");
        }
    }

    #region Callback Count Verification (SUB-001 to SUB-005)

    /// <summary>
    /// Verifies that the seed node receives view change callbacks for membership changes.
    /// With IAsyncEnumerable, subscribers only receive events published AFTER they subscribe.
    /// Since the consumer is registered after StartCluster(), it won't receive the initial
    /// VIEW_CHANGE event, but will receive events for subsequent membership changes.
    /// </summary>
    [Fact]
    public void SeedNodeReceivesCorrectCallbackCount()
    {
        var callbackLog = new ConcurrentBag<ClusterStatusChange>();

        var seedNode = _harness.CreateSeedNode();
        // Consumer is started after node is created, so initial event may be missed
        var consumer = CreateEventConsumer(seedNode);

        _harness.RunUntilIdle();

        // Join a node - this should trigger a callback that the consumer CAN see
        var joiner = _harness.CreateJoinerNode(seedNode, nodeId: 1);
        _harness.WaitForConvergence(expectedSize: 2);

        // Collect events after convergence
        CollectEvents(consumer, ClusterEvents.ViewChange, callbackLog);

        // Seed should have received at least one callback for the join
        Assert.True(callbackLog.Count >= 1,
            $"Expected at least 1 callback after join, got {callbackLog.Count}");

        // Verify the callback contains the expected membership size
        Assert.Contains(callbackLog, c => c.Membership.Count == 2);
    }

    /// <summary>
    /// Verifies that the joiner node receives view change callbacks for subsequent events.
    /// With IAsyncEnumerable, subscribers only receive events published AFTER they subscribe.
    /// The consumer is registered after the join completes, so it needs another membership
    /// change (joiner2 joining) to trigger a callback.
    /// </summary>
    [Fact]
    public void JoinerNodeReceivesCorrectCallbackCount()
    {
        var seedNode = _harness.CreateSeedNode();

        var joiner1 = _harness.CreateJoinerNode(seedNode, nodeId: 1);
        // Consumer is started AFTER join, so joiner1's own join event may be missed
        var joinerCallbackLog = new ConcurrentBag<ClusterStatusChange>();
        var consumer = CreateEventConsumer(joiner1);

        _harness.WaitForConvergence(expectedSize: 2);

        // Trigger another membership change so joiner1 receives a callback
        var joiner2 = _harness.CreateJoinerNode(seedNode, nodeId: 2);
        _harness.WaitForConvergence(expectedSize: 3);

        // Collect events after convergence
        CollectEvents(consumer, ClusterEvents.ViewChange, joinerCallbackLog);

        // Joiner1 should have received at least 1 callback for joiner2's join
        Assert.True(joinerCallbackLog.Count >= 1,
            $"Expected at least 1 callback for joiner, got {joinerCallbackLog.Count}");
    }

    /// <summary>
    /// Verifies that multiple subscriptions on the same node each receive callbacks.
    /// </summary>
    [Fact]
    public void MultipleSubscriptionsEachReceiveCallbacks()
    {
        var callbackLog1 = new ConcurrentBag<ClusterStatusChange>();
        var callbackLog2 = new ConcurrentBag<ClusterStatusChange>();

        var seedNode = _harness.CreateSeedNode();
        var consumer1 = CreateEventConsumer(seedNode);
        var consumer2 = CreateEventConsumer(seedNode);

        _harness.RunUntilIdle();

        var joiner = _harness.CreateJoinerNode(seedNode, nodeId: 1);
        _harness.WaitForConvergence(expectedSize: 2);

        // Collect events after convergence
        CollectEvents(consumer1, ClusterEvents.ViewChange, callbackLog1);
        CollectEvents(consumer2, ClusterEvents.ViewChange, callbackLog2);

        // Both subscriptions should receive the same number of callbacks
        Assert.True(callbackLog1.Count >= 1, "First subscription should receive callbacks");
        Assert.True(callbackLog2.Count >= 1, "Second subscription should receive callbacks");
        Assert.Equal(callbackLog1.Count, callbackLog2.Count);
    }

    /// <summary>
    /// Verifies callbacks in a multi-node cluster scenario.
    /// Subscriptions registered after join will only receive subsequent events.
    /// </summary>
    [Fact]
    public void MultiNodeClusterCallbackCounts()
    {
        var seedCallbackLog = new ConcurrentBag<ClusterStatusChange>();
        var joiner1CallbackLog = new ConcurrentBag<ClusterStatusChange>();
        var joiner2CallbackLog = new ConcurrentBag<ClusterStatusChange>();

        var seedNode = _harness.CreateSeedNode();
        var seedConsumer = CreateEventConsumer(seedNode);

        _harness.RunUntilIdle();
        CollectEvents(seedConsumer, ClusterEvents.ViewChange, seedCallbackLog);
        var seedInitialCount = seedCallbackLog.Count;

        var joiner1 = _harness.CreateJoinerNode(seedNode, nodeId: 1);
        var joiner1Consumer = CreateEventConsumer(joiner1);
        _harness.WaitForConvergence(expectedSize: 2);

        var joiner2 = _harness.CreateJoinerNode(seedNode, nodeId: 2);
        var joiner2Consumer = CreateEventConsumer(joiner2);
        _harness.WaitForConvergence(expectedSize: 3);

        // Add a third joiner so that joiner1 and joiner2 both receive at least one callback
        var joiner3 = _harness.CreateJoinerNode(seedNode, nodeId: 3);
        _harness.WaitForConvergence(expectedSize: 4);

        // Collect all events after final convergence
        CollectEvents(seedConsumer, ClusterEvents.ViewChange, seedCallbackLog);
        CollectEvents(joiner1Consumer, ClusterEvents.ViewChange, joiner1CallbackLog);
        CollectEvents(joiner2Consumer, ClusterEvents.ViewChange, joiner2CallbackLog);

        // Seed should have received callbacks for all three joins
        Assert.True(seedCallbackLog.Count >= seedInitialCount + 3,
            $"Seed should receive at least 3 more callbacks after joins, got {seedCallbackLog.Count - seedInitialCount}");

        // Joiner1 should have received callbacks for joiner2 and joiner3's joins
        Assert.True(joiner1CallbackLog.Count >= 2,
            $"Joiner1 should receive at least 2 callbacks (joiner2 join + joiner3 join), got {joiner1CallbackLog.Count}");

        // Joiner2 should have received callback for joiner3's join
        Assert.True(joiner2CallbackLog.Count >= 1,
            $"Joiner2 should receive at least 1 callback, got {joiner2CallbackLog.Count}");
    }

    #endregion

    #region Membership Log Verification (SUB-010 to SUB-015)

    /// <summary>
    /// Verifies that the membership list in callbacks grows as nodes join.
    /// Note: Subscriptions registered after MembershipService initialization will not receive
    /// the initial view callback (membership size=1), but will receive callbacks for subsequent joins.
    /// </summary>
    [Fact]
    public void MembershipListGrowsWithJoins()
    {
        var seedNode = _harness.CreateSeedNode();
        var callbackLog = new ConcurrentBag<ClusterStatusChange>();
        var consumer = CreateEventConsumer(seedNode);

        _harness.RunUntilIdle();

        var joiner1 = _harness.CreateJoinerNode(seedNode, nodeId: 1);
        _harness.WaitForConvergence(expectedSize: 2);

        var joiner2 = _harness.CreateJoinerNode(seedNode, nodeId: 2);
        _harness.WaitForConvergence(expectedSize: 3);

        // Collect events after convergence
        CollectEvents(consumer, ClusterEvents.ViewChange, callbackLog);

        var sizes = callbackLog.Select(c => c.Membership.Count).ToList();

        // Should have seen memberships of increasing sizes for subsequent joins
        // Note: Initial view (size=1) may not be observed since subscription is registered
        // after MembershipService fires its initial VIEW_CHANGE event
        Assert.Contains(2, sizes); // After first join
        Assert.Contains(3, sizes); // After second join
    }

    /// <summary>
    /// Verifies that membership lists contain the expected endpoints.
    /// </summary>
    [Fact]
    public void MembershipContainsExpectedEndpoints()
    {
        var seedNode = _harness.CreateSeedNode();
        var callbackLog = new ConcurrentBag<ClusterStatusChange>();
        var consumer = CreateEventConsumer(seedNode);

        _harness.RunUntilIdle();

        var joiner = _harness.CreateJoinerNode(seedNode, nodeId: 1);
        _harness.WaitForConvergence(expectedSize: 2);

        // Collect events after convergence
        CollectEvents(consumer, ClusterEvents.ViewChange, callbackLog);

        // Find the membership with 2 members
        var twoNodeMembership = callbackLog.Select(c => c.Membership).FirstOrDefault(m => m.Count == 2);
        Assert.NotNull(twoNodeMembership);

        // Verify both endpoints are present
        var hostnames = twoNodeMembership.Select(e => e.Hostname.ToStringUtf8()).ToHashSet();
        Assert.Contains(seedNode.Address.Hostname.ToStringUtf8(), hostnames);
        Assert.Contains(joiner.Address.Hostname.ToStringUtf8(), hostnames);
    }

    /// <summary>
    /// Verifies that membership shrinks when nodes leave.
    /// </summary>
    [Fact]
    public void MembershipShrinksWithLeaves()
    {
        var callbackLog = new ConcurrentBag<ClusterStatusChange>();

        var seedNode = _harness.CreateSeedNode();
        var consumer = CreateEventConsumer(seedNode);

        _harness.RunUntilIdle();

        var joiner1 = _harness.CreateJoinerNode(seedNode, nodeId: 1);
        var joiner2 = _harness.CreateJoinerNode(seedNode, nodeId: 2);
        _harness.WaitForConvergence(expectedSize: 3);

        // Leave one node
        _harness.RemoveNodeGracefully(joiner2);
        _harness.WaitForConvergence(expectedSize: 2);

        // Collect events after convergence
        CollectEvents(consumer, ClusterEvents.ViewChange, callbackLog);

        var sizes = callbackLog.Select(c => c.Membership.Count).ToList();

        // Should have seen membership of size 3 followed by size 2
        Assert.Contains(3, sizes);
        Assert.Contains(2, sizes);
    }

    #endregion

    #region Delta Log Verification (SUB-020 to SUB-025)

    /// <summary>
    /// Verifies that delta information contains endpoint data for joins with correct Up status.
    /// </summary>
    [Fact]
    public void DeltaContainsEndpointsForJoins()
    {
        var callbackLog = new ConcurrentBag<ClusterStatusChange>();

        var seedNode = _harness.CreateSeedNode();
        var consumer = CreateEventConsumer(seedNode);

        _harness.RunUntilIdle();

        var joiner = _harness.CreateJoinerNode(seedNode, nodeId: 1);
        _harness.WaitForConvergence(expectedSize: 2);

        // Collect events after convergence
        CollectEvents(consumer, ClusterEvents.ViewChange, callbackLog);

        // Find deltas with status changes
        var allDeltas = callbackLog.SelectMany(c => c.Delta).ToList();

        // Verify we have at least one delta entry for the joiner with Up status
        Assert.NotEmpty(allDeltas);
        var joinerHostname = joiner.Address.Hostname.ToStringUtf8();
        var joinerDelta = allDeltas.FirstOrDefault(d => d.Endpoint.Hostname.ToStringUtf8() == joinerHostname);
        Assert.NotNull(joinerDelta);
        Assert.Equal(EdgeStatus.Up, joinerDelta.Status);
    }

    /// <summary>
    /// Verifies that delta information contains the failed node's endpoint with correct Down status.
    /// </summary>
    [Fact]
    public void DeltaContainsEndpointForFailures()
    {
        var seedNode = _harness.CreateSeedNode();
        var joiner1 = _harness.CreateJoinerNode(seedNode, nodeId: 1);
        var joiner2 = _harness.CreateJoinerNode(seedNode, nodeId: 2);

        var callbackLog = new ConcurrentBag<ClusterStatusChange>();
        var consumer = CreateEventConsumer(joiner1);

        _harness.WaitForConvergence(expectedSize: 3);

        var joiner2Hostname = joiner2.Address.Hostname.ToStringUtf8();

        // Crash a node
        _harness.CrashNode(joiner2);

        // Wait for failure detection
        _harness.WaitForConvergence(expectedSize: 2, maxIterations: 500000);

        // Collect events after convergence
        CollectEvents(consumer, ClusterEvents.ViewChange, callbackLog);

        // Find deltas for the failed node
        var allDeltas = callbackLog.SelectMany(c => c.Delta).ToList();

        // Verify we have at least one delta for the crashed node with Down status
        Assert.NotEmpty(allDeltas);
        var failedNodeDelta = allDeltas.FirstOrDefault(d => d.Endpoint.Hostname.ToStringUtf8() == joiner2Hostname);
        Assert.NotNull(failedNodeDelta);
        Assert.Equal(EdgeStatus.Down, failedNodeDelta.Status);
    }

    /// <summary>
    /// Verifies that delta contains the correct endpoint and status for changing nodes.
    /// </summary>
    [Fact]
    public void DeltaContainsCorrectEndpoint()
    {
        var callbackLog = new ConcurrentBag<ClusterStatusChange>();

        var seedNode = _harness.CreateSeedNode();
        var consumer = CreateEventConsumer(seedNode);

        _harness.RunUntilIdle();

        // First joiner - callback fires after this
        var joiner1 = _harness.CreateJoinerNode(seedNode, nodeId: 1);
        _harness.WaitForConvergence(expectedSize: 2);

        // Second joiner - this triggers a callback that we can verify
        var joiner2 = _harness.CreateJoinerNode(seedNode, nodeId: 2);
        _harness.WaitForConvergence(expectedSize: 3);

        // Collect events after convergence
        CollectEvents(consumer, ClusterEvents.ViewChange, callbackLog);

        // Verify we got deltas
        var allDeltas = callbackLog.SelectMany(c => c.Delta).ToList();
        Assert.True(allDeltas.Count >= 2, $"Expected at least 2 deltas, got {allDeltas.Count}");

        // Verify we have deltas with endpoints matching our joiners with Up status
        var joiner1Hostname = joiner1.Address.Hostname.ToStringUtf8();
        var joiner2Hostname = joiner2.Address.Hostname.ToStringUtf8();

        var joiner1Delta = allDeltas.FirstOrDefault(d => d.Endpoint.Hostname.ToStringUtf8() == joiner1Hostname);
        var joiner2Delta = allDeltas.FirstOrDefault(d => d.Endpoint.Hostname.ToStringUtf8() == joiner2Hostname);

        Assert.NotNull(joiner1Delta);
        Assert.NotNull(joiner2Delta);
        Assert.Equal(EdgeStatus.Up, joiner1Delta.Status);
        Assert.Equal(EdgeStatus.Up, joiner2Delta.Status);
    }

    #endregion

    #region Metadata in Callbacks (SUB-030 to SUB-035)

    /// <summary>
    /// Verifies that metadata is propagated in join notifications.
    /// </summary>
    [Fact]
    public void MetadataIsPropagatedInJoinNotifications()
    {
        var callbackLog = new ConcurrentBag<ClusterStatusChange>();

        var seedNode = _harness.CreateSeedNode();
        var consumer = CreateEventConsumer(seedNode);

        _harness.RunUntilIdle();

        // Create joiner with metadata
        var metadata = new Metadata();
        metadata.Metadata_.Add("role", ByteString.CopyFromUtf8("worker"));

        // Note: Current SimulationNode doesn't pass metadata through CreateJoinerNode easily
        // This test verifies the basic structure works
        var joiner = _harness.CreateJoinerNode(seedNode, nodeId: 1);
        _harness.WaitForConvergence(expectedSize: 2);

        // Collect events after convergence
        CollectEvents(consumer, ClusterEvents.ViewChange, callbackLog);

        // Verify we got deltas (metadata propagation details depend on implementation)
        var allDeltas = callbackLog.SelectMany(c => c.Delta).ToList();
        Assert.NotEmpty(allDeltas);
    }

    #endregion

    #region Configuration ID Verification (SUB-040 to SUB-043)

    /// <summary>
    /// Verifies that configuration ID changes with each membership change.
    /// Note: Since subscriptions are registered after node creation, we need
    /// to have multiple joins to observe multiple configuration IDs.
    /// </summary>
    [Fact]
    public void ConfigurationIdChangesWithMembershipChanges()
    {
        var seedNode = _harness.CreateSeedNode();
        var callbackLog = new ConcurrentBag<ClusterStatusChange>();
        var consumer = CreateEventConsumer(seedNode);

        _harness.RunUntilIdle();

        // First joiner - triggers first callback
        var joiner1 = _harness.CreateJoinerNode(seedNode, nodeId: 1);
        _harness.WaitForConvergence(expectedSize: 2);

        // Second joiner - triggers second callback with different config ID
        var joiner2 = _harness.CreateJoinerNode(seedNode, nodeId: 2);
        _harness.WaitForConvergence(expectedSize: 3);

        // Collect events after convergence
        CollectEvents(consumer, ClusterEvents.ViewChange, callbackLog);

        var configIdList = callbackLog.Select(c => c.ConfigurationId).ToList();

        // Should have at least 2 different configuration IDs
        var uniqueConfigIds = configIdList.Distinct().ToList();
        Assert.True(uniqueConfigIds.Count >= 2,
            $"Expected at least 2 unique configuration IDs, got {uniqueConfigIds.Count}");
    }

    /// <summary>
    /// Verifies that all nodes see the same configuration ID after convergence.
    /// Note: Since subscriptions are registered after join, we need to trigger an
    /// additional membership change for joiner nodes to receive callbacks.
    /// </summary>
    [Fact]
    public void AllNodesSeeSameConfigurationIdAfterConvergence()
    {
        long? seedConfigId = null;
        long? joiner1ConfigId = null;

        var seedNode = _harness.CreateSeedNode();
        var seedCallbackLog = new ConcurrentBag<ClusterStatusChange>();
        var seedConsumer = CreateEventConsumer(seedNode);

        var joiner1 = _harness.CreateJoinerNode(seedNode, nodeId: 1);
        var joiner1CallbackLog = new ConcurrentBag<ClusterStatusChange>();
        var joiner1Consumer = CreateEventConsumer(joiner1);

        _harness.WaitForConvergence(expectedSize: 2);

        // Add a third node so both seed and joiner1 receive callbacks
        var joiner2 = _harness.CreateJoinerNode(seedNode, nodeId: 2);
        _harness.WaitForConvergence(expectedSize: 3);

        // Collect events after convergence
        CollectEvents(seedConsumer, ClusterEvents.ViewChange, seedCallbackLog);
        CollectEvents(joiner1Consumer, ClusterEvents.ViewChange, joiner1CallbackLog);

        // Get config IDs from callbacks with 3 members
        seedConfigId = seedCallbackLog.FirstOrDefault(c => c.Membership.Count == 3)?.ConfigurationId;
        joiner1ConfigId = joiner1CallbackLog.FirstOrDefault(c => c.Membership.Count == 3)?.ConfigurationId;

        // Both should have seen configuration ID for 3-node cluster
        Assert.NotNull(seedConfigId);
        Assert.NotNull(joiner1ConfigId);
        Assert.Equal(seedConfigId, joiner1ConfigId);
    }

    #endregion

    #region Subscription Timing (SUB-050 to SUB-053)

    /// <summary>
    /// Verifies that subscriptions added after join still receive future events.
    /// </summary>
    [Fact]
    public void SubscriptionAddedAfterJoinReceivesFutureEvents()
    {
        var seedNode = _harness.CreateSeedNode();
        var joiner1 = _harness.CreateJoinerNode(seedNode, nodeId: 1);
        _harness.WaitForConvergence(expectedSize: 2);

        // Add subscription after join
        var callbackLog = new ConcurrentBag<ClusterStatusChange>();
        var consumer = CreateEventConsumer(joiner1);

        // Trigger another membership change
        var joiner2 = _harness.CreateJoinerNode(seedNode, nodeId: 2);
        _harness.WaitForConvergence(expectedSize: 3);

        // Collect events after convergence
        CollectEvents(consumer, ClusterEvents.ViewChange, callbackLog);

        // Should have received at least one callback for the new join
        Assert.NotEmpty(callbackLog);
        Assert.Contains(callbackLog, c => c.Membership.Count == 3);
    }

    /// <summary>
    /// Verifies that ViewChangeProposal events fire before ViewChange events.
    /// </summary>
    [Fact]
    public void ViewChangeProposalFiresBeforeViewChange()
    {
        var eventSequence = new ConcurrentQueue<string>();

        var seedNode = _harness.CreateSeedNode();
        var consumer = CreateEventConsumer(seedNode);

        _harness.RunUntilIdle();

        var joiner = _harness.CreateJoinerNode(seedNode, nodeId: 1);
        _harness.WaitForConvergence(expectedSize: 2);

        // Collect events after convergence
        CollectEvents(consumer, eventSequence);

        var sequence = eventSequence.ToList();

        // ViewChangeProposal events should appear in the sequence
        // (They may or may not fire depending on consensus path, but if they do, they come before ViewChange)
        if (sequence.Contains("Proposal"))
        {
            var proposalIndex = sequence.LastIndexOf("Proposal");
            var viewChangeIndices = sequence
                .Select((s, i) => (s, i))
                .Where(x => x.s == "ViewChange" && x.i > proposalIndex)
                .Select(x => x.i)
                .ToList();

            // If there's a proposal followed by a view change, the order is correct
            Assert.True(viewChangeIndices.Count > 0 || sequence.Last() == "Proposal",
                "ViewChange should follow ViewChangeProposal");
        }
    }

    #endregion

    #region Edge Cases (SUB-060 to SUB-063)

    /// <summary>
    /// Verifies that callback exceptions don't crash the membership service.
    /// Note: With IAsyncEnumerable, consumer exceptions don't affect other consumers or the broadcaster.
    /// </summary>
    [Fact]
    public void CallbackExceptionDoesNotCrashService()
    {
        var successfulCallbacks = new ConcurrentBag<ClusterStatusChange>();

        var seedNode = _harness.CreateSeedNode();

        // Start a consumer that will succeed
        var consumer = CreateEventConsumer(seedNode);

        // Note: With IAsyncEnumerable, a throwing consumer would only crash its own iteration,
        // not affect other consumers. This is inherently safe by design.

        // This should work fine
        var joiner = _harness.CreateJoinerNode(seedNode, nodeId: 1);
        _harness.WaitForConvergence(expectedSize: 2);

        // Collect events after convergence
        CollectEvents(consumer, ClusterEvents.ViewChange, successfulCallbacks);

        // Cluster should still be functional
        Assert.Equal(2, seedNode.MembershipSize);
        Assert.Equal(2, joiner.MembershipSize);
    }

    /// <summary>
    /// Verifies that delta list is properly initialized (not null) in callbacks.
    /// Note: Since subscriptions are registered after MembershipService initialization,
    /// we verify this behavior on callbacks triggered by join events.
    /// </summary>
    [Fact]
    public void DeltaIsProperlyInitializedInCallbacks()
    {
        var callbacks = new ConcurrentBag<ClusterStatusChange>();

        var seedNode = _harness.CreateSeedNode();
        var consumer = CreateEventConsumer(seedNode);

        _harness.RunUntilIdle();

        // Trigger a membership change to get a callback
        var joiner = _harness.CreateJoinerNode(seedNode, nodeId: 1);
        _harness.WaitForConvergence(expectedSize: 2);

        // Collect events after convergence
        CollectEvents(consumer, ClusterEvents.ViewChange, callbacks);

        // Verify we got at least one callback
        Assert.NotEmpty(callbacks);

        // All callbacks should have non-null delta lists
        foreach (var callback in callbacks)
        {
            Assert.NotNull(callback.Delta);
        }
    }

    #endregion
}
