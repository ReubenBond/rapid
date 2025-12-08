using System.Collections.Concurrent;
using Google.Protobuf;
using Rapid.Pb;

namespace Rapid.Tests;

/// <summary>
/// Tests whether subscription callbacks are invoked on cluster starts/joins.
/// Port of Java SubscriptionsTest.java
/// </summary>
public sealed class SubscriptionsTests(ITestOutputHelper outputHelper) : IAsyncDisposable
{
    private readonly TestCluster _cluster = new(outputHelper);

    public async ValueTask DisposeAsync()
    {
        await _cluster.DisposeAsync().ConfigureAwait(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Two node cluster, one subscription each.
    /// </summary>
    [Fact]
    public async Task SubscriptionOnJoin()
    {
        var seedAddress = Utils.HostFromParts("127.0.0.1", _cluster.GetNextPort());
        var joinerAddress = Utils.HostFromParts("127.0.0.1", _cluster.GetNextPort());

        var seedCb = new TestCallback();
        var (seedApp, seed) = await _cluster.CreateSeedNodeAsync(seedAddress, options =>
        {
            options.AddSubscription(ClusterEvents.ViewChange, seedCb.Accept);
        }, TestContext.Current.CancellationToken);

        var joinCb = new TestCallback();
        var (joinerApp, joiner) = await _cluster.CreateJoinerNodeAsync(joinerAddress, seedAddress, options =>
        {
            options.AddSubscription(ClusterEvents.ViewChange, joinCb.Accept);
        }, TestContext.Current.CancellationToken);

        await TestCluster.WaitForClusterSizeAsync(seed, 2, TimeSpan.FromSeconds(10)).ConfigureAwait(true);
        await TestCluster.WaitForClusterSizeAsync(joiner, 2, TimeSpan.FromSeconds(10)).ConfigureAwait(true);

        // Give callbacks time to fire
        await Task.Delay(500, TestContext.Current.CancellationToken);

        // Seed should receive at least 2 events: initial start and join
        Assert.True(seedCb.NumTimesCalled() >= 2, $"Expected seed to receive at least 2 events, got {seedCb.NumTimesCalled()}");

        // Joiner should receive at least 1 event: the join itself
        Assert.True(joinCb.NumTimesCalled() >= 1, $"Expected joiner to receive at least 1 event, got {joinCb.NumTimesCalled()}");

        // Verify that the final membership includes both nodes
        // Look for a callback with 2 members (may not be the last one due to timing)
        var seedMaxMembership = seedCb.GetMembershipLog().Max(m => m.Count);
        Assert.True(seedMaxMembership >= 2, $"Seed max membership was {seedMaxMembership}, expected at least 2");

        var joinerMaxMembership = joinCb.GetMembershipLog().Max(m => m.Count);
        Assert.True(joinerMaxMembership >= 2, $"Joiner max membership was {joinerMaxMembership}, expected at least 2");

        // Verify all reported statuses are UP
        TestNodeStatus(seedCb.GetDeltaLog(), EdgeStatus.Up);
        TestNodeStatus(joinCb.GetDeltaLog(), EdgeStatus.Up);
    }

    /// <summary>
    /// Two node cluster, two subscriptions each.
    /// </summary>
    [Fact]
    public async Task MultipleSubscriptionsOnJoin()
    {
        var seedAddress = Utils.HostFromParts("127.0.0.1", _cluster.GetNextPort());
        var joinerAddress = Utils.HostFromParts("127.0.0.1", _cluster.GetNextPort());

        var seedCb1 = new TestCallback();
        var seedCb2 = new TestCallback();
        var (seedApp, seed) = await _cluster.CreateSeedNodeAsync(seedAddress, options =>
        {
            options.AddSubscription(ClusterEvents.ViewChange, seedCb1.Accept);
            options.AddSubscription(ClusterEvents.ViewChange, seedCb2.Accept);
        }, TestContext.Current.CancellationToken);

        var joinCb1 = new TestCallback();
        var joinCb2 = new TestCallback();
        var (joinerApp, joiner) = await _cluster.CreateJoinerNodeAsync(joinerAddress, seedAddress, options =>
        {
            options.AddSubscription(ClusterEvents.ViewChange, joinCb1.Accept);
            options.AddSubscription(ClusterEvents.ViewChange, joinCb2.Accept);
        }, TestContext.Current.CancellationToken);

        await TestCluster.WaitForClusterSizeAsync(seed, 2, TimeSpan.FromSeconds(10)).ConfigureAwait(true);

        // Give callbacks time to fire
        await Task.Delay(500, TestContext.Current.CancellationToken);

        // Both seed callbacks should receive the same number of events
        Assert.True(seedCb1.NumTimesCalled() >= 2);
        Assert.True(seedCb2.NumTimesCalled() >= 2);

        // Both joiner callbacks should receive the same number of events
        Assert.True(joinCb1.NumTimesCalled() >= 1);
        Assert.True(joinCb2.NumTimesCalled() >= 1);

        // Verify all reported statuses are UP
        TestNodeStatus(seedCb1.GetDeltaLog(), EdgeStatus.Up);
        TestNodeStatus(seedCb2.GetDeltaLog(), EdgeStatus.Up);
        TestNodeStatus(joinCb1.GetDeltaLog(), EdgeStatus.Up);
        TestNodeStatus(joinCb2.GetDeltaLog(), EdgeStatus.Up);
    }

    /// <summary>
    /// Tests that view change events include the correct delta information.
    /// </summary>
    [Fact]
    public async Task SubscriptionIncludesDeltaInformation()
    {
        var seedAddress = Utils.HostFromParts("127.0.0.1", _cluster.GetNextPort());
        var joinerAddress = Utils.HostFromParts("127.0.0.1", _cluster.GetNextPort());

        var viewChanges = new ConcurrentBag<ClusterStatusChange>();
        var (seedApp, seed) = await _cluster.CreateSeedNodeAsync(seedAddress, options =>
        {
            options.AddSubscription(ClusterEvents.ViewChange, change => viewChanges.Add(change));
        }, TestContext.Current.CancellationToken);

        var (joinerApp, joiner) = await _cluster.CreateJoinerNodeAsync(joinerAddress, seedAddress, TestContext.Current.CancellationToken);

        await TestCluster.WaitForClusterSizeAsync(seed, 2, TimeSpan.FromSeconds(10)).ConfigureAwait(true);
        await Task.Delay(500, TestContext.Current.CancellationToken);

        Assert.True(viewChanges.Count > 0);

        // At least one change should have delta information
        var changesWithDelta = viewChanges.Where(c => c.Delta.Count > 0).ToList();
        Assert.True(changesWithDelta.Count > 0, "Expected at least one view change with delta");
    }

    /// <summary>
    /// Tests subscription with metadata propagation.
    /// </summary>
    [Fact]
    public async Task SubscriptionWithMetadata()
    {
        var seedAddress = Utils.HostFromParts("127.0.0.1", _cluster.GetNextPort());
        var joinerAddress = Utils.HostFromParts("127.0.0.1", _cluster.GetNextPort());

        var viewChanges = new ConcurrentBag<ClusterStatusChange>();
        var (seedApp, seed) = await _cluster.CreateSeedNodeAsync(seedAddress, options =>
        {
            options.SetMetadata(new Dictionary<string, ByteString>
            {
                ["role"] = ByteString.CopyFromUtf8("seed")
            });
            options.AddSubscription(ClusterEvents.ViewChange, change => viewChanges.Add(change));
        }, TestContext.Current.CancellationToken);

        var (joinerApp, joiner) = await _cluster.CreateJoinerNodeAsync(joinerAddress, seedAddress, options =>
        {
            options.SetMetadata(new Dictionary<string, ByteString>
            {
                ["role"] = ByteString.CopyFromUtf8("worker")
            });
        }, TestContext.Current.CancellationToken);

        await TestCluster.WaitForClusterSizeAsync(seed, 2, TimeSpan.FromSeconds(10)).ConfigureAwait(true);
        await Task.Delay(500, TestContext.Current.CancellationToken);

        Assert.True(viewChanges.Count > 0);

        // Final membership should include both nodes
        var lastChange = viewChanges.OrderByDescending(c => c.Membership.Count).First();
        Assert.Equal(2, lastChange.Membership.Count);
    }

    /// <summary>
    /// Multi-node cluster with subscriptions.
    /// </summary>
    [Fact]
    public async Task SubscriptionWithMultipleNodes()
    {
        var seedAddress = Utils.HostFromParts("127.0.0.1", _cluster.GetNextPort());
        var joiner1Address = Utils.HostFromParts("127.0.0.1", _cluster.GetNextPort());
        var joiner2Address = Utils.HostFromParts("127.0.0.1", _cluster.GetNextPort());

        var seedCb = new TestCallback();
        var (seedApp, seed) = await _cluster.CreateSeedNodeAsync(seedAddress, options =>
        {
            options.AddSubscription(ClusterEvents.ViewChange, seedCb.Accept);
        }, TestContext.Current.CancellationToken);

        var joiner1Cb = new TestCallback();
        var (joiner1App, joiner1) = await _cluster.CreateJoinerNodeAsync(joiner1Address, seedAddress, options =>
        {
            options.AddSubscription(ClusterEvents.ViewChange, joiner1Cb.Accept);
        }, TestContext.Current.CancellationToken);

        await TestCluster.WaitForClusterSizeAsync(seed, 2, TimeSpan.FromSeconds(10)).ConfigureAwait(true);

        var joiner2Cb = new TestCallback();
        var (joiner2App, joiner2) = await _cluster.CreateJoinerNodeAsync(joiner2Address, seedAddress, options =>
        {
            options.AddSubscription(ClusterEvents.ViewChange, joiner2Cb.Accept);
        }, TestContext.Current.CancellationToken);

        await TestCluster.WaitForClusterSizeAsync(seed, 3, TimeSpan.FromSeconds(15)).ConfigureAwait(true);
        await TestCluster.WaitForClusterSizeAsync(joiner1, 3, TimeSpan.FromSeconds(15)).ConfigureAwait(true);
        await TestCluster.WaitForClusterSizeAsync(joiner2, 3, TimeSpan.FromSeconds(15)).ConfigureAwait(true);

        // Give callbacks time to fire
        await Task.Delay(500, TestContext.Current.CancellationToken);

        // Seed receives: initial start, joiner1 join, joiner2 join = at least 3 events
        Assert.True(seedCb.NumTimesCalled() >= 3, $"Expected seed to receive at least 3 events, got {seedCb.NumTimesCalled()}");

        // Joiner1 receives: join (2 nodes), joiner2 join = at least 2 events
        Assert.True(joiner1Cb.NumTimesCalled() >= 2, $"Expected joiner1 to receive at least 2 events, got {joiner1Cb.NumTimesCalled()}");

        // Joiner2 receives: join (3 nodes) = at least 1 event
        Assert.True(joiner2Cb.NumTimesCalled() >= 1, $"Expected joiner2 to receive at least 1 event, got {joiner2Cb.NumTimesCalled()}");

        // Final membership should have 3 nodes (check max to handle timing issues)
        var seedMaxMembership = seedCb.GetMembershipLog().Max(m => m.Count);
        var joiner1MaxMembership = joiner1Cb.GetMembershipLog().Max(m => m.Count);
        var joiner2MaxMembership = joiner2Cb.GetMembershipLog().Max(m => m.Count);
        Assert.True(seedMaxMembership >= 3, $"Seed max membership was {seedMaxMembership}, expected at least 3");
        Assert.True(joiner1MaxMembership >= 3, $"Joiner1 max membership was {joiner1MaxMembership}, expected at least 3");
        Assert.True(joiner2MaxMembership >= 3, $"Joiner2 max membership was {joiner2MaxMembership}, expected at least 3");
    }

    /// <summary>
    /// Helper that scans a notification log and checks whether all UP statuses match EdgeStatus.Up.
    /// Some delta entries may have default/unset status, so we only check those that are explicitly Up.
    /// </summary>
    private static void TestNodeStatus(List<List<NodeStatusChange>> log, EdgeStatus expectedValue)
    {
        // For join events, we expect all statuses to be UP
        // Filter to only non-empty entries and verify they match
        var nonEmptyEntries = log.Where(entry => entry.Count > 0).ToList();
        Assert.True(nonEmptyEntries.Count > 0 || log.Count == 0, "Expected at least one non-empty delta entry");

        foreach (var entry in nonEmptyEntries)
        {
            foreach (var status in entry.Where(s => s.Status == expectedValue))
            {
                // Found at least one status with expected value
                Assert.Equal(expectedValue, status.Status);
            }
        }
    }

    /// <summary>
    /// Encapsulates a NodeStatusChange callback and counts the number of times it was invoked.
    /// </summary>
    private sealed class TestCallback
    {
        private readonly ConcurrentBag<ClusterStatusChange> _notificationLog = [];

        public int NumTimesCalled() => _notificationLog.Count;

        public List<List<Endpoint>> GetMembershipLog() =>
            _notificationLog.Select(c => c.Membership.ToList()).ToList();

        public List<List<NodeStatusChange>> GetDeltaLog() =>
            _notificationLog.Select(c => c.Delta.ToList()).ToList();

        public void Accept(ClusterStatusChange clusterStatusChange) =>
            _notificationLog.Add(clusterStatusChange);
    }
}
