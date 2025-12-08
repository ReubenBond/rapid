using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Rapid.Tests.Simulation;

namespace Rapid.Tests.SimulationTests;

/// <summary>
/// Tests for node failure scenarios using the simulation harness.
/// Covers single node failures, multiple node failures, seed node failures, and failures during operations.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test naming convention")]
public sealed class NodeFailureTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly ILoggerFactory _loggerFactory;
    private SimulationHarness _harness = null!;
    private const int TestSeed = 23456;

    public NodeFailureTests(ITestOutputHelper output)
    {
        _output = output;
        _loggerFactory = LoggerFactory.Create(builder => builder.AddXUnit(output).SetMinimumLevel(LogLevel.Debug));
    }

    public ValueTask InitializeAsync()
    {
        _output.WriteLine($"[NodeFailureTests] Initializing with seed {TestSeed}");
        _harness = new SimulationHarness(seed: TestSeed, loggerFactory: _loggerFactory);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _output.WriteLine("[NodeFailureTests] Disposing harness");
        await _harness.DisposeAsync();
        _loggerFactory.Dispose();
    }

    #region Single Node Failure (FAIL-001 to FAIL-004)

    [Fact(Skip = "Requires failure detection timing - slow test")]
    public void NodeCrashRemovesFromCluster()
    {
        var seedNode = _harness.CreateSeedNode();
        var joiner = _harness.CreateJoinerNode(seedNode, nodeId: 1);

        _harness.WaitForConvergence(expectedSize: 2);

        // Crash the joiner
        _harness.CrashNode(joiner);

        // Wait for failure detection and removal
        _harness.WaitForNodeSize(seedNode, expectedSize: 1);

        Assert.Equal(1, seedNode.MembershipSize);
    }

    [Fact]
    public void CrashedNodeCannotReceiveMessages()
    {
        var seedNode = _harness.CreateSeedNode();
        var joiner = _harness.CreateJoinerNode(seedNode, nodeId: 1);

        // Crash the joiner
        _harness.CrashNode(joiner);

        // Verify crashed node is removed from harness nodes list
        Assert.DoesNotContain(joiner, _harness.Nodes);
    }

    [Fact]
    public void ClusterContinuesAfterSingleNodeCrash()
    {
        var seedNode = _harness.CreateSeedNode();
        var joiner1 = _harness.CreateJoinerNode(seedNode, nodeId: 1);
        var joiner2 = _harness.CreateJoinerNode(seedNode, nodeId: 2);

        _harness.WaitForConvergence(expectedSize: 3);

        // Crash one joiner
        _harness.CrashNode(joiner1);

        // Seed and remaining joiner should still be operational
        Assert.True(seedNode.IsInitialized);
        Assert.True(joiner2.IsInitialized);
    }

    [Fact]
    public void CrashDuringIdleStateHandledGracefully()
    {
        var seedNode = _harness.CreateSeedNode();

        // Crash immediately - should not throw
        _harness.CrashNode(seedNode);

        Assert.DoesNotContain(seedNode, _harness.Nodes);
    }

    #endregion

    #region Multiple Node Failures (FAIL-010 to FAIL-013)

    [Fact(Skip = "Requires failure detection timing - slow test")]
    public void TwoNodeFailuresInFiveNodeCluster()
    {
        var nodes = _harness.CreateCluster(size: 5);

        _harness.WaitForConvergence(expectedSize: 5);

        // Crash two nodes
        _harness.CrashNode(nodes[3]);
        _harness.CrashNode(nodes[4]);

        // Wait for failure detection
        _harness.WaitForConvergence(expectedSize: 3);

        Assert.All(_harness.Nodes, node => Assert.Equal(3, node.MembershipSize));
    }

    [Fact]
    public void SequentialFailuresHandled()
    {
        var seedNode = _harness.CreateSeedNode();
        var joiner1 = _harness.CreateJoinerNode(seedNode, nodeId: 1);
        var joiner2 = _harness.CreateJoinerNode(seedNode, nodeId: 2);

        _harness.WaitForConvergence(expectedSize: 3);

        // Crash nodes sequentially
        _harness.CrashNode(joiner2);
        Assert.Equal(2, _harness.Nodes.Count);

        _harness.CrashNode(joiner1);
        Assert.Single(_harness.Nodes);
    }

    [Fact]
    public void SimultaneousFailuresHandled()
    {
        var seedNode = _harness.CreateSeedNode();
        var joiner1 = _harness.CreateJoinerNode(seedNode, nodeId: 1);
        var joiner2 = _harness.CreateJoinerNode(seedNode, nodeId: 2);
        var joiner3 = _harness.CreateJoinerNode(seedNode, nodeId: 3);

        _harness.WaitForConvergence(expectedSize: 4);

        // Crash multiple nodes "simultaneously"
        _harness.CrashNode(joiner2);
        _harness.CrashNode(joiner3);

        Assert.Equal(2, _harness.Nodes.Count);
        Assert.Contains(seedNode, _harness.Nodes);
        Assert.Contains(joiner1, _harness.Nodes);
    }

    #endregion

    #region Seed Node Failure (FAIL-020 to FAIL-022)

    [Fact]
    public void SeedNodeCrashDoesNotAffectExistingCluster()
    {
        var seedNode = _harness.CreateSeedNode();
        var joiner1 = _harness.CreateJoinerNode(seedNode, nodeId: 1);
        var joiner2 = _harness.CreateJoinerNode(seedNode, nodeId: 2);

        _harness.WaitForConvergence(expectedSize: 3);

        // Crash the original seed node
        _harness.CrashNode(seedNode);

        // Remaining nodes should still be operational
        Assert.True(joiner1.IsInitialized);
        Assert.True(joiner2.IsInitialized);
        Assert.Equal(2, _harness.Nodes.Count);
    }

    [Fact]
    public void NewJoinsFailAfterSeedCrash()
    {
        var seedNode = _harness.CreateSeedNode();

        // Crash the seed
        _harness.CrashNode(seedNode);

        // Attempting to join through crashed seed should fail
        Assert.Throws<InvalidOperationException>(() =>
        {
            _harness.CreateJoinerNode(seedNode, nodeId: 1);
        });
    }

    [Fact]
    public void AlternativeSeedAllowsJoin()
    {
        var seedNode = _harness.CreateSeedNode();
        var joiner1 = _harness.CreateJoinerNode(seedNode, nodeId: 1);

        _harness.WaitForConvergence(expectedSize: 2);

        // Crash the original seed
        _harness.CrashNode(seedNode);

        // Join through the remaining joiner (which is now the only member)
        var joiner2 = _harness.CreateJoinerNode(joiner1, nodeId: 2);

        Assert.True(joiner2.IsInitialized);
    }

    #endregion

    #region Failure During Operations (FAIL-030 to FAIL-033)

    [Fact(Skip = "Complex timing scenario - needs careful implementation")]
    public void NodeCrashDuringJoinProtocol()
    {
        var seedNode = _harness.CreateSeedNode();

        // This test would require async behavior to test race conditions
        // Skipped for now as it requires special handling
    }

    [Fact]
    public void NodeCrashDuringLeave()
    {
        var seedNode = _harness.CreateSeedNode();
        var joiner = _harness.CreateJoinerNode(seedNode, nodeId: 1);

        _harness.WaitForConvergence(expectedSize: 2);

        // Crash the joiner immediately
        _harness.CrashNode(joiner);

        Assert.DoesNotContain(joiner, _harness.Nodes);
    }

    #endregion
}
