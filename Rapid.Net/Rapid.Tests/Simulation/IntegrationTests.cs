using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Rapid.Tests.Simulation;

namespace Rapid.Tests.SimulationTests;

/// <summary>
/// Integration tests for complete cluster lifecycle scenarios.
/// These tests verify end-to-end behavior and recovery scenarios.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test naming convention")]
public sealed class IntegrationTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly ILoggerFactory _loggerFactory;
    private SimulationTestHarness _harness = null!;
    private const int TestSeed = 90123;

    public IntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _loggerFactory = LoggerFactory.Create(builder => builder.AddXUnit(output).SetMinimumLevel(LogLevel.Debug));
    }

    public ValueTask InitializeAsync()
    {
        _output.WriteLine($"[IntegrationTests] Initializing with seed {TestSeed}");
        _harness = new SimulationTestHarness(seed: TestSeed, loggerFactory: _loggerFactory);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _output.WriteLine("[IntegrationTests] Disposing harness");
        await _harness.DisposeAsync();
        _loggerFactory.Dispose();
    }

    #region Complete Cluster Lifecycle (INT-001 to INT-004)

    [Fact]
    public async Task FullClusterLifecycle()
    {
        // Create seed node
        var seedNode = _harness.CreateSeedNode();
        Assert.True(seedNode.IsInitialized);
        Assert.Equal(1, seedNode.MembershipSize);

        // Join additional nodes
        var joiner1 = await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 1, cancellationToken: TestContext.Current.CancellationToken);
        var joiner2 = await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 2, cancellationToken: TestContext.Current.CancellationToken);

        await _harness.WaitForConvergenceAsync(expectedSize: 3, timeout: TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);

        // All nodes operational
        Assert.All(_harness.Nodes, n => Assert.True(n.IsInitialized));
        Assert.All(_harness.Nodes, n => Assert.Equal(3, n.MembershipSize));

        // Shutdown all nodes
        foreach (var node in _harness.Nodes.ToList())
        {
            _harness.CrashNode(node);
        }

        Assert.Empty(_harness.Nodes);
    }

    [Fact]
    public async Task ClusterScaleUpAndDown()
    {
        // Start with seed
        var seedNode = _harness.CreateSeedNode();

        // Scale up to 4 nodes
        var joiner1 = await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 1, cancellationToken: TestContext.Current.CancellationToken);
        var joiner2 = await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 2, cancellationToken: TestContext.Current.CancellationToken);
        var joiner3 = await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 3, cancellationToken: TestContext.Current.CancellationToken);

        await _harness.WaitForConvergenceAsync(expectedSize: 4, timeout: TimeSpan.FromSeconds(15), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(4, _harness.Nodes.Count);

        // Scale down by removing nodes
        _harness.CrashNode(joiner3);
        _harness.CrashNode(joiner2);

        Assert.Equal(2, _harness.Nodes.Count);
    }

    [Fact]
    public void EmergencyShutdownAllNodes()
    {
        // Create several seed nodes (each is independent)
        for (var i = 0; i < 5; i++)
        {
            _harness.CreateSeedNode(i);
        }

        Assert.Equal(5, _harness.Nodes.Count);

        // Emergency shutdown all nodes at once
        var nodesToCrash = _harness.Nodes.ToList();
        foreach (var node in nodesToCrash)
        {
            _harness.CrashNode(node);
        }

        Assert.Empty(_harness.Nodes);
    }

    [Fact]
    public async Task JoinThroughDifferentSeeds()
    {
        // Create initial two-node cluster
        var seedNode = _harness.CreateSeedNode();
        var joiner1 = await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 1, cancellationToken: TestContext.Current.CancellationToken);

        await _harness.WaitForConvergenceAsync(expectedSize: 2, timeout: TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        // Join through joiner1 instead of original seed
        var joiner2 = await _harness.CreateJoinerNodeAsync(joiner1, nodeId: 2, cancellationToken: TestContext.Current.CancellationToken);

        await _harness.WaitForConvergenceAsync(expectedSize: 3, timeout: TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(joiner2.IsInitialized);
        Assert.All(_harness.Nodes, n => Assert.Equal(3, n.MembershipSize));
    }

    #endregion

    #region Recovery Scenarios (INT-010 to INT-013)

    [Fact]
    public async Task RecoveryFromPartitionedState()
    {
        var seedNode = _harness.CreateSeedNode();
        var joiner = await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 1, cancellationToken: TestContext.Current.CancellationToken);

        await _harness.WaitForConvergenceAsync(expectedSize: 2, timeout: TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        // Create partition
        _harness.PartitionNodes(seedNode, joiner);

        // Heal partition (before failure detection)
        _harness.HealPartition(seedNode, joiner);

        // Both nodes should still be operational
        Assert.True(seedNode.IsInitialized);
        Assert.True(joiner.IsInitialized);
        Assert.Equal(2, seedNode.MembershipSize);
        Assert.Equal(2, joiner.MembershipSize);
    }

    [Fact]
    public async Task ClusterContinuesAfterSeedNodeRemoval()
    {
        var seedNode = _harness.CreateSeedNode();
        var joiner1 = await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 1, cancellationToken: TestContext.Current.CancellationToken);
        var joiner2 = await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 2, cancellationToken: TestContext.Current.CancellationToken);

        await _harness.WaitForConvergenceAsync(expectedSize: 3, timeout: TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);

        // Remove the original seed
        _harness.CrashNode(seedNode);

        // Remaining nodes should continue operating
        Assert.True(joiner1.IsInitialized);
        Assert.True(joiner2.IsInitialized);
        Assert.Equal(2, _harness.Nodes.Count);
    }

    [Fact]
    public async Task NewJoinsWorkAfterMembershipChange()
    {
        var seedNode = _harness.CreateSeedNode();
        var joiner1 = await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 1, cancellationToken: TestContext.Current.CancellationToken);

        await _harness.WaitForConvergenceAsync(expectedSize: 2, timeout: TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        // Remove joiner1
        _harness.CrashNode(joiner1);

        // Add a new joiner through seed
        var joiner2 = await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 2, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(joiner2.IsInitialized);
    }

    #endregion

    #region View Change Notification (INT-020 to INT-023)

    [Fact]
    public void ViewAccessorInitiallyHasView()
    {
        var seedNode = _harness.CreateSeedNode();

        Assert.NotNull(seedNode.ViewAccessor);
        Assert.NotNull(seedNode.ViewAccessor.CurrentView);
        Assert.Equal(1, seedNode.ViewAccessor.CurrentView.Size);
    }

    [Fact]
    public async Task ViewAccessorUpdatesOnJoin()
    {
        var seedNode = _harness.CreateSeedNode();
        var initialConfigId = seedNode.ViewAccessor.CurrentView.ConfigurationId;

        var joiner = await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 1, cancellationToken: TestContext.Current.CancellationToken);

        await _harness.WaitForConvergenceAsync(expectedSize: 2, timeout: TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        // View should have been updated
        Assert.True(seedNode.ViewAccessor.CurrentView.ConfigurationId > initialConfigId);
        Assert.Equal(2, seedNode.ViewAccessor.CurrentView.Size);
    }

    [Fact]
    public async Task JoinerViewAccessorHasCorrectView()
    {
        var seedNode = _harness.CreateSeedNode();
        var joiner = await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 1, cancellationToken: TestContext.Current.CancellationToken);

        // Joiner's view accessor should have correct view
        Assert.NotNull(joiner.ViewAccessor);
        Assert.Equal(2, joiner.ViewAccessor.CurrentView.Size);
    }

    #endregion

    #region Network Simulation Integration

    [Fact]
    public async Task NetworkSimulationBasics()
    {
        var seedNode = _harness.CreateSeedNode();
        var joiner = await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 1, cancellationToken: TestContext.Current.CancellationToken);

        var seedAddr = RapidUtils.Loggable(seedNode.Address);
        var joinerAddr = RapidUtils.Loggable(joiner.Address);

        // Initially, network allows all communication
        Assert.True(_harness.Network.CanDeliver(seedAddr, joinerAddr));

        // Create partition
        _harness.Network.CreateBidirectionalPartition(seedAddr, joinerAddr);
        Assert.False(_harness.Network.CanDeliver(seedAddr, joinerAddr));

        // Heal partition
        _harness.Network.HealBidirectionalPartition(seedAddr, joinerAddr);
        Assert.True(_harness.Network.CanDeliver(seedAddr, joinerAddr));
    }

    [Fact]
    public void NetworkHealAllPartitionsWorks()
    {
        _harness.CreateSeedNode(0);
        _harness.CreateSeedNode(1);
        _harness.CreateSeedNode(2);

        // Create multiple partitions
        _harness.Network.CreatePartition("node:0", "node:1");
        _harness.Network.CreatePartition("node:1", "node:2");
        _harness.Network.CreatePartition("node:0", "node:2");

        Assert.False(_harness.Network.CanDeliver("node:0", "node:1"));

        // Heal all at once
        _harness.Network.HealAllPartitions();

        Assert.True(_harness.Network.CanDeliver("node:0", "node:1"));
        Assert.True(_harness.Network.CanDeliver("node:1", "node:2"));
        Assert.True(_harness.Network.CanDeliver("node:0", "node:2"));
    }

    #endregion

    #region Harness Utilities

    [Fact]
    public void HarnessEnvironmentAccessible()
    {
        Assert.NotNull(_harness.Environment);
        Assert.NotNull(_harness.Random);
        Assert.NotNull(_harness.Network);
    }

    [Fact]
    public void HarnessNodesListIsUpToDate()
    {
        Assert.Empty(_harness.Nodes);

        var node1 = _harness.CreateSeedNode(0);
        Assert.Single(_harness.Nodes);

        var node2 = _harness.CreateSeedNode(1);
        Assert.Equal(2, _harness.Nodes.Count);

        _harness.CrashNode(node1);
        Assert.Single(_harness.Nodes);
        Assert.Contains(node2, _harness.Nodes);
    }

    [Fact]
    public async Task WaitForConvergenceTimesOutCorrectly()
    {
        var seedNode = _harness.CreateSeedNode();

        // Waiting for size 5 when only 1 node exists should timeout
        await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            await _harness.WaitForConvergenceAsync(expectedSize: 5, timeout: TimeSpan.FromMilliseconds(100), cancellationToken: TestContext.Current.CancellationToken);
        });
    }

    [Fact]
    public async Task WaitForNodeSizeTimesOutCorrectly()
    {
        var seedNode = _harness.CreateSeedNode();

        // Waiting for size 5 when only 1 node exists should timeout
        await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            await _harness.WaitForNodeSizeAsync(seedNode, expectedSize: 5, timeout: TimeSpan.FromMilliseconds(100), cancellationToken: TestContext.Current.CancellationToken);
        });
    }

    #endregion
}


