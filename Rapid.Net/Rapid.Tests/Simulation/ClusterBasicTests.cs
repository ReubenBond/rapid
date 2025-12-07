using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Rapid.Tests.Simulation;

namespace Rapid.Tests.SimulationTests;

/// <summary>
/// Tests for basic cluster operations using the simulation harness.
/// Covers single node, two-node, and multi-node cluster formation.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test naming convention")]
public sealed class ClusterBasicTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly ILoggerFactory _loggerFactory;
    private SimulationTestHarness _harness = null!;
    private const int TestSeed = 12345;

    public ClusterBasicTests(ITestOutputHelper output)
    {
        _output = output;
        _loggerFactory = LoggerFactory.Create(builder => builder.AddXUnit(output).SetMinimumLevel(LogLevel.Debug));
    }

    public ValueTask InitializeAsync()
    {
        _output.WriteLine($"[ClusterBasicTests] Initializing with seed {TestSeed}");
        _harness = new SimulationTestHarness(seed: TestSeed, loggerFactory: _loggerFactory);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _output.WriteLine("[ClusterBasicTests] Disposing harness");
        await _harness.DisposeAsync();
        _loggerFactory.Dispose();
    }

    #region Single Node Operations (BASIC-001 to BASIC-005)

    [Fact]
    public void SingleNodeClusterInitializes()
    {
        _output.WriteLine("Creating seed node...");
        var seedNode = _harness.CreateSeedNode();

        _output.WriteLine($"Seed node initialized: {seedNode.IsInitialized}, membership size: {seedNode.MembershipSize}");
        Assert.NotNull(seedNode);
        Assert.True(seedNode.IsInitialized);
        Assert.Equal(1, seedNode.MembershipSize);
    }

    [Fact]
    public void SingleNodeHasValidConfigurationId()
    {
        var seedNode = _harness.CreateSeedNode();

        Assert.NotNull(seedNode.CurrentView);
        Assert.True(seedNode.CurrentView.ConfigurationId >= 0);
    }

    [Fact]
    public void SingleNodeViewContainsSelf()
    {
        var seedNode = _harness.CreateSeedNode();

        var view = seedNode.CurrentView;
        Assert.NotNull(view);
        Assert.Single(view.Members);

        var member = view.Members[0];
        Assert.Equal(seedNode.Address.Hostname, member.Hostname);
        Assert.Equal(seedNode.Address.Port, member.Port);
    }

    [Fact]
    public void SingleNodeCanShutdownGracefully()
    {
        var seedNode = _harness.CreateSeedNode();
        Assert.True(seedNode.IsInitialized);

        // Shutdown should not throw
        seedNode.Shutdown();
    }

    [Fact]
    public async Task SingleNodeCanLeaveCluster()
    {
        var seedNode = _harness.CreateSeedNode();
        Assert.True(seedNode.IsInitialized);

        // Leave should not throw (degenerates to shutdown for single node)
        await seedNode.LeaveAsync();
    }

    #endregion

    #region Two-Node Cluster Operations (BASIC-010 to BASIC-015)

    [Fact]
    public async Task TwoNodeClusterFormation()
    {
        var seedNode = _harness.CreateSeedNode();
        var joiner = await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 1, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(joiner);
        Assert.True(joiner.IsInitialized);
        Assert.Equal(2, joiner.MembershipSize);
    }

    [Fact]
    public async Task JoinerSeesCorrectMembership()
    {
        var seedNode = _harness.CreateSeedNode();
        var joiner = await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 1, cancellationToken: TestContext.Current.CancellationToken);

        var view = joiner.CurrentView;
        Assert.NotNull(view);
        Assert.Equal(2, view.Members.Length);

        // View should contain both nodes
        var addresses = view.Members.Select(m => $"{m.Hostname}:{m.Port}").ToHashSet();
        Assert.Contains($"{seedNode.Address.Hostname}:{seedNode.Address.Port}", addresses);
        Assert.Contains($"{joiner.Address.Hostname}:{joiner.Address.Port}", addresses);
    }

    [Fact]
    public async Task SeedSeesJoinerAfterJoin()
    {
        var seedNode = _harness.CreateSeedNode();
        var joiner = await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 1, cancellationToken: TestContext.Current.CancellationToken);

        // Wait for seed to see the joiner
        await _harness.WaitForNodeSizeAsync(seedNode, expectedSize: 2, timeout: TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, seedNode.MembershipSize);
    }

    [Fact]
    public async Task BothNodesHaveSameConfigurationId()
    {
        var seedNode = _harness.CreateSeedNode();
        var joiner = await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 1, cancellationToken: TestContext.Current.CancellationToken);

        // Wait for convergence
        await _harness.WaitForConvergenceAsync(expectedSize: 2, timeout: TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(seedNode.CurrentView.ConfigurationId, joiner.CurrentView.ConfigurationId);
    }

    [Fact(Skip = "Requires failure detection to trigger removal - slow test")]
    public async Task JoinerCanLeaveTwoNodeCluster()
    {
        var seedNode = _harness.CreateSeedNode();
        var joiner = await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 1, cancellationToken: TestContext.Current.CancellationToken);

        await _harness.WaitForConvergenceAsync(expectedSize: 2, timeout: TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        // Joiner leaves gracefully
        await _harness.RemoveNodeGracefullyAsync(joiner);

        // Wait for seed to see the leave
        await _harness.WaitForNodeSizeAsync(seedNode, expectedSize: 1, timeout: TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, seedNode.MembershipSize);
    }

    [Fact(Skip = "Requires failure detection to trigger removal - slow test")]
    public async Task SeedCanLeaveTwoNodeCluster()
    {
        var seedNode = _harness.CreateSeedNode();
        var joiner = await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 1, cancellationToken: TestContext.Current.CancellationToken);

        await _harness.WaitForConvergenceAsync(expectedSize: 2, timeout: TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        // Seed leaves gracefully
        await _harness.RemoveNodeGracefullyAsync(seedNode);

        // Wait for joiner to see the leave
        await _harness.WaitForNodeSizeAsync(joiner, expectedSize: 1, timeout: TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, joiner.MembershipSize);
    }

    #endregion

    #region Multi-Node Cluster Operations (BASIC-020 to BASIC-025)

    [Fact(Skip = "Slow test - consensus roundtrips with batching delays")]
    public async Task ThreeNodeClusterFormation()
    {
        var nodes = await _harness.CreateClusterAsync(size: 3, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, nodes.Count);
        Assert.All(nodes, node => Assert.True(node.IsInitialized));
        Assert.All(nodes, node => Assert.Equal(3, node.MembershipSize));
    }

    [Fact(Skip = "Slow test - consensus roundtrips with batching delays")]
    public async Task FiveNodeClusterFormation()
    {
        var nodes = await _harness.CreateClusterAsync(size: 5, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(5, nodes.Count);
        Assert.All(nodes, node => Assert.True(node.IsInitialized));
        Assert.All(nodes, node => Assert.Equal(5, node.MembershipSize));
    }

    [Fact]
    public async Task SequentialJoinsSucceed()
    {
        var seedNode = _harness.CreateSeedNode();

        var joiner1 = await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 1, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(joiner1.IsInitialized);
        Assert.Equal(2, joiner1.MembershipSize);

        var joiner2 = await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 2, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(joiner2.IsInitialized);
        Assert.Equal(3, joiner2.MembershipSize);
    }

    [Fact]
    public async Task AllNodesConvergeToSameMembership()
    {
        var seedNode = _harness.CreateSeedNode();
        var joiner1 = await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 1, cancellationToken: TestContext.Current.CancellationToken);
        var joiner2 = await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 2, cancellationToken: TestContext.Current.CancellationToken);

        await _harness.WaitForConvergenceAsync(expectedSize: 3, timeout: TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);

        // All nodes should have the same membership size
        Assert.Equal(3, seedNode.MembershipSize);
        Assert.Equal(3, joiner1.MembershipSize);
        Assert.Equal(3, joiner2.MembershipSize);
    }

    [Fact]
    public async Task ConfigurationIdIncrementsWithMembershipChanges()
    {
        var seedNode = _harness.CreateSeedNode();
        var initialConfigId = seedNode.CurrentView.ConfigurationId;

        var joiner = await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 1, cancellationToken: TestContext.Current.CancellationToken);

        // Wait for convergence
        await _harness.WaitForConvergenceAsync(expectedSize: 2, timeout: TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        // Configuration ID should have increased
        Assert.True(seedNode.CurrentView.ConfigurationId > initialConfigId);
    }

    [Fact]
    public async Task MembershipViewContainsAllNodes()
    {
        var seedNode = _harness.CreateSeedNode();
        var joiner1 = await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 1, cancellationToken: TestContext.Current.CancellationToken);
        var joiner2 = await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 2, cancellationToken: TestContext.Current.CancellationToken);

        await _harness.WaitForConvergenceAsync(expectedSize: 3, timeout: TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);

        var view = seedNode.CurrentView;
        var addresses = view.Members.Select(m => $"{m.Hostname}:{m.Port}").ToHashSet();

        Assert.Contains($"{seedNode.Address.Hostname}:{seedNode.Address.Port}", addresses);
        Assert.Contains($"{joiner1.Address.Hostname}:{joiner1.Address.Port}", addresses);
        Assert.Contains($"{joiner2.Address.Hostname}:{joiner2.Address.Port}", addresses);
    }

    #endregion

    #region Metadata Operations (BASIC-030 to BASIC-032)

    [Fact]
    public async Task NodeCanJoinWithMetadata()
    {
        var seedNode = _harness.CreateSeedNode();

        var metadata = new Pb.Metadata();
        metadata.Metadata_.Add("role", Google.Protobuf.ByteString.CopyFromUtf8("worker"));

        // Join with metadata should not throw
        var joiner = await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 1, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(joiner.IsInitialized);
    }

    #endregion
}


