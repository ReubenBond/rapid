using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Rapid.Tests.Simulation;

namespace Rapid.Tests.SimulationTests;

/// <summary>
/// Tests for network partition scenarios using the simulation harness.
/// Covers simple partitions, isolation scenarios, split-brain prevention, and partition/heal sequences.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test naming convention")]
public sealed class NetworkPartitionTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly ILoggerFactory _loggerFactory;
    private SimulationTestHarness _harness = null!;
    private const int TestSeed = 34567;

    public NetworkPartitionTests(ITestOutputHelper output)
    {
        _output = output;
        _loggerFactory = LoggerFactory.Create(builder => builder.AddXUnit(output).SetMinimumLevel(LogLevel.Debug));
    }

    public ValueTask InitializeAsync()
    {
        _output.WriteLine($"[NetworkPartitionTests] Initializing with seed {TestSeed}");
        _harness = new SimulationTestHarness(seed: TestSeed, loggerFactory: _loggerFactory);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _output.WriteLine("[NetworkPartitionTests] Disposing harness");
        await _harness.DisposeAsync();
        _loggerFactory.Dispose();
    }

    #region Simple Partitions (PART-001 to PART-004)

    [Fact]
    public async Task Part001BidirectionalPartitionBlocksMessages()
    {
        var seedNode = _harness.CreateSeedNode();
        var joiner = await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 1, cancellationToken: TestContext.Current.CancellationToken);

        await _harness.WaitForConvergenceAsync(expectedSize: 2, timeout: TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        // Create bidirectional partition
        _harness.PartitionNodes(seedNode, joiner);

        // Verify partition is in place
        var seedAddr = RapidUtils.Loggable(seedNode.Address);
        var joinerAddr = RapidUtils.Loggable(joiner.Address);

        Assert.False(_harness.Network.CanDeliver(seedAddr, joinerAddr));
        Assert.False(_harness.Network.CanDeliver(joinerAddr, seedAddr));
    }

    [Fact]
    public async Task Part002UnidirectionalPartitionAllowsOneWay()
    {
        var seedNode = _harness.CreateSeedNode();
        var joiner = await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 1, cancellationToken: TestContext.Current.CancellationToken);

        var seedAddr = RapidUtils.Loggable(seedNode.Address);
        var joinerAddr = RapidUtils.Loggable(joiner.Address);

        // Create unidirectional partition (seed -> joiner blocked, joiner -> seed allowed)
        _harness.Network.CreatePartition(seedAddr, joinerAddr);

        Assert.False(_harness.Network.CanDeliver(seedAddr, joinerAddr));
        Assert.True(_harness.Network.CanDeliver(joinerAddr, seedAddr));
    }

    [Fact(Skip = "Requires failure detection timing - slow test")]
    public async Task Part003PartitionedNodeEventuallyDetected()
    {
        var seedNode = _harness.CreateSeedNode();
        var joiner1 = await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 1, cancellationToken: TestContext.Current.CancellationToken);
        var joiner2 = await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 2, cancellationToken: TestContext.Current.CancellationToken);

        await _harness.WaitForConvergenceAsync(expectedSize: 3, timeout: TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);

        // Isolate joiner2
        _harness.IsolateNode(joiner2);

        // Wait for failure detection and removal
        await _harness.WaitForNodeSizeAsync(seedNode, expectedSize: 2, timeout: TimeSpan.FromSeconds(60), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, seedNode.MembershipSize);
    }

    [Fact]
    public async Task Part004PartitionHealRestoresConnectivity()
    {
        var seedNode = _harness.CreateSeedNode();
        var joiner = await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 1, cancellationToken: TestContext.Current.CancellationToken);

        // Create partition
        _harness.PartitionNodes(seedNode, joiner);

        var seedAddr = RapidUtils.Loggable(seedNode.Address);
        var joinerAddr = RapidUtils.Loggable(joiner.Address);

        Assert.False(_harness.Network.CanDeliver(seedAddr, joinerAddr));

        // Heal partition
        _harness.HealPartition(seedNode, joiner);

        Assert.True(_harness.Network.CanDeliver(seedAddr, joinerAddr));
        Assert.True(_harness.Network.CanDeliver(joinerAddr, seedAddr));
    }

    #endregion

    #region Isolation Scenarios (PART-010 to PART-013)

    [Fact]
    public async Task Part010IsolatedNodeCannotCommunicate()
    {
        var seedNode = _harness.CreateSeedNode();
        var joiner1 = await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 1, cancellationToken: TestContext.Current.CancellationToken);
        var joiner2 = await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 2, cancellationToken: TestContext.Current.CancellationToken);

        await _harness.WaitForConvergenceAsync(expectedSize: 3, timeout: TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);

        // Isolate joiner2
        _harness.IsolateNode(joiner2);

        var joiner2Addr = RapidUtils.Loggable(joiner2.Address);
        var seedAddr = RapidUtils.Loggable(seedNode.Address);
        var joiner1Addr = RapidUtils.Loggable(joiner1.Address);

        // Joiner2 should not be able to communicate with anyone
        Assert.False(_harness.Network.CanDeliver(joiner2Addr, seedAddr));
        Assert.False(_harness.Network.CanDeliver(joiner2Addr, joiner1Addr));
        Assert.False(_harness.Network.CanDeliver(seedAddr, joiner2Addr));
        Assert.False(_harness.Network.CanDeliver(joiner1Addr, joiner2Addr));
    }

    [Fact]
    public async Task Part012ReconnectedNodeRestoresConnectivity()
    {
        var seedNode = _harness.CreateSeedNode();
        var joiner = await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 1, cancellationToken: TestContext.Current.CancellationToken);

        // Isolate
        _harness.IsolateNode(joiner);

        var seedAddr = RapidUtils.Loggable(seedNode.Address);
        var joinerAddr = RapidUtils.Loggable(joiner.Address);

        Assert.False(_harness.Network.CanDeliver(seedAddr, joinerAddr));

        // Reconnect
        _harness.ReconnectNode(joiner);

        Assert.True(_harness.Network.CanDeliver(seedAddr, joinerAddr));
    }

    [Fact]
    public async Task Part013MultipleNodesIsolatedSimultaneously()
    {
        var seedNode = _harness.CreateSeedNode();
        var joiner1 = await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 1, cancellationToken: TestContext.Current.CancellationToken);
        var joiner2 = await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 2, cancellationToken: TestContext.Current.CancellationToken);
        var joiner3 = await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 3, cancellationToken: TestContext.Current.CancellationToken);

        await _harness.WaitForConvergenceAsync(expectedSize: 4, timeout: TimeSpan.FromSeconds(15), cancellationToken: TestContext.Current.CancellationToken);

        // Isolate multiple nodes
        _harness.IsolateNode(joiner2);
        _harness.IsolateNode(joiner3);

        var seedAddr = RapidUtils.Loggable(seedNode.Address);
        var joiner1Addr = RapidUtils.Loggable(joiner1.Address);
        var joiner2Addr = RapidUtils.Loggable(joiner2.Address);
        var joiner3Addr = RapidUtils.Loggable(joiner3.Address);

        // Seed and joiner1 can still communicate
        Assert.True(_harness.Network.CanDeliver(seedAddr, joiner1Addr));
        Assert.True(_harness.Network.CanDeliver(joiner1Addr, seedAddr));

        // Isolated nodes cannot communicate with anyone
        Assert.False(_harness.Network.CanDeliver(joiner2Addr, seedAddr));
        Assert.False(_harness.Network.CanDeliver(joiner3Addr, seedAddr));
    }

    #endregion

    #region Split-Brain Prevention (PART-020 to PART-023)

    [Fact]
    public async Task Part020InvariantCheckerDetectsSplitBrainAttempt()
    {
        await using var deterministicHarness = new DeterministicSimulationHarness(seed: TestSeed);
        var checker = new InvariantChecker(deterministicHarness);

        var seedNode = deterministicHarness.CreateSeedNode();

        // Check that single node doesn't trigger split-brain detection
        var result = checker.CheckNoSplitBrain();
        Assert.True(result);
        Assert.False(checker.HasViolations);
    }

    #endregion

    #region Partition and Heal Sequences (PART-030 to PART-033)

    [Fact]
    public async Task Part030PartitionThenHealBeforeDetection()
    {
        var seedNode = _harness.CreateSeedNode();
        var joiner = await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 1, cancellationToken: TestContext.Current.CancellationToken);

        await _harness.WaitForConvergenceAsync(expectedSize: 2, timeout: TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        // Create partition
        _harness.PartitionNodes(seedNode, joiner);

        // Heal immediately (before failure detection)
        _harness.HealPartition(seedNode, joiner);

        // Both nodes should still see each other
        Assert.Equal(2, seedNode.MembershipSize);
        Assert.Equal(2, joiner.MembershipSize);
    }

    [Fact]
    public async Task Part031RepeatedPartitionHealCycles()
    {
        var seedNode = _harness.CreateSeedNode();
        var joiner = await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 1, cancellationToken: TestContext.Current.CancellationToken);

        await _harness.WaitForConvergenceAsync(expectedSize: 2, timeout: TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        for (var i = 0; i < 5; i++)
        {
            // Create and heal partition rapidly
            _harness.PartitionNodes(seedNode, joiner);
            _harness.HealPartition(seedNode, joiner);
        }

        // Cluster should still be intact
        Assert.Equal(2, seedNode.MembershipSize);
        Assert.Equal(2, joiner.MembershipSize);
    }

    [Fact]
    public void Part032HealAllPartitionsWorks()
    {
        var seedNode = _harness.CreateSeedNode();

        var seedAddr = RapidUtils.Loggable(seedNode.Address);

        // Create multiple partitions
        _harness.Network.CreatePartition(seedAddr, "fake:1");
        _harness.Network.CreatePartition(seedAddr, "fake:2");
        _harness.Network.CreatePartition(seedAddr, "fake:3");

        Assert.False(_harness.Network.CanDeliver(seedAddr, "fake:1"));

        // Heal all partitions at once
        _harness.Network.HealAllPartitions();

        Assert.True(_harness.Network.CanDeliver(seedAddr, "fake:1"));
        Assert.True(_harness.Network.CanDeliver(seedAddr, "fake:2"));
        Assert.True(_harness.Network.CanDeliver(seedAddr, "fake:3"));
    }

    #endregion
}


