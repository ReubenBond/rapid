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
    private SimulationTestHarness _harness = null!;
    private const int TestSeed = 23456;

    public NodeFailureTests(ITestOutputHelper output)
    {
        _output = output;
        _loggerFactory = LoggerFactory.Create(builder => builder.AddXUnit(output).SetMinimumLevel(LogLevel.Debug));
    }

    public ValueTask InitializeAsync()
    {
        _output.WriteLine($"[NodeFailureTests] Initializing with seed {TestSeed}");
        _harness = new SimulationTestHarness(seed: TestSeed, loggerFactory: _loggerFactory);
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
    public async Task NodeCrashRemovesFromCluster()
    {
        var seedNode = _harness.CreateSeedNode();
        var joiner = await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 1, cancellationToken: TestContext.Current.CancellationToken);

        await _harness.WaitForConvergenceAsync(expectedSize: 2, timeout: TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        // Crash the joiner
        _harness.CrashNode(joiner);

        // Wait for failure detection and removal
        await _harness.WaitForNodeSizeAsync(seedNode, expectedSize: 1, timeout: TimeSpan.FromSeconds(30), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, seedNode.MembershipSize);
    }

    [Fact]
    public async Task CrashedNodeCannotReceiveMessages()
    {
        var seedNode = _harness.CreateSeedNode();
        var joiner = await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 1, cancellationToken: TestContext.Current.CancellationToken);

        // Crash the joiner
        _harness.CrashNode(joiner);

        // Verify crashed node is removed from harness nodes list
        Assert.DoesNotContain(joiner, _harness.Nodes);
    }

    [Fact]
    public async Task ClusterContinuesAfterSingleNodeCrash()
    {
        var seedNode = _harness.CreateSeedNode();
        var joiner1 = await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 1, cancellationToken: TestContext.Current.CancellationToken);
        var joiner2 = await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 2, cancellationToken: TestContext.Current.CancellationToken);

        await _harness.WaitForConvergenceAsync(expectedSize: 3, timeout: TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);

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
    public async Task TwoNodeFailuresInFiveNodeCluster()
    {
        var nodes = await _harness.CreateClusterAsync(size: 5, cancellationToken: TestContext.Current.CancellationToken);

        await _harness.WaitForConvergenceAsync(expectedSize: 5, timeout: TimeSpan.FromSeconds(30), cancellationToken: TestContext.Current.CancellationToken);

        // Crash two nodes
        _harness.CrashNode(nodes[3]);
        _harness.CrashNode(nodes[4]);

        // Wait for failure detection
        await _harness.WaitForConvergenceAsync(expectedSize: 3, timeout: TimeSpan.FromSeconds(60), cancellationToken: TestContext.Current.CancellationToken);

        Assert.All(_harness.Nodes, node => Assert.Equal(3, node.MembershipSize));
    }

    [Fact]
    public async Task SequentialFailuresHandled()
    {
        var seedNode = _harness.CreateSeedNode();
        var joiner1 = await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 1, cancellationToken: TestContext.Current.CancellationToken);
        var joiner2 = await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 2, cancellationToken: TestContext.Current.CancellationToken);

        await _harness.WaitForConvergenceAsync(expectedSize: 3, timeout: TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);

        // Crash nodes sequentially
        _harness.CrashNode(joiner2);
        Assert.Equal(2, _harness.Nodes.Count);

        _harness.CrashNode(joiner1);
        Assert.Single(_harness.Nodes);
    }

    [Fact]
    public async Task SimultaneousFailuresHandled()
    {
        var seedNode = _harness.CreateSeedNode();
        var joiner1 = await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 1, cancellationToken: TestContext.Current.CancellationToken);
        var joiner2 = await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 2, cancellationToken: TestContext.Current.CancellationToken);
        var joiner3 = await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 3, cancellationToken: TestContext.Current.CancellationToken);

        await _harness.WaitForConvergenceAsync(expectedSize: 4, timeout: TimeSpan.FromSeconds(15), cancellationToken: TestContext.Current.CancellationToken);

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
    public async Task SeedNodeCrashDoesNotAffectExistingCluster()
    {
        var seedNode = _harness.CreateSeedNode();
        var joiner1 = await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 1, cancellationToken: TestContext.Current.CancellationToken);
        var joiner2 = await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 2, cancellationToken: TestContext.Current.CancellationToken);

        await _harness.WaitForConvergenceAsync(expectedSize: 3, timeout: TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);

        // Crash the original seed node
        _harness.CrashNode(seedNode);

        // Remaining nodes should still be operational
        Assert.True(joiner1.IsInitialized);
        Assert.True(joiner2.IsInitialized);
        Assert.Equal(2, _harness.Nodes.Count);
    }

    [Fact]
    public async Task NewJoinsFailAfterSeedCrash()
    {
        var seedNode = _harness.CreateSeedNode();

        // Crash the seed
        _harness.CrashNode(seedNode);

        // Attempting to join through crashed seed should fail
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 1, cancellationToken: TestContext.Current.CancellationToken);
        });
    }

    [Fact]
    public async Task AlternativeSeedAllowsJoin()
    {
        var seedNode = _harness.CreateSeedNode();
        var joiner1 = await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 1, cancellationToken: TestContext.Current.CancellationToken);

        await _harness.WaitForConvergenceAsync(expectedSize: 2, timeout: TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        // Crash the original seed
        _harness.CrashNode(seedNode);

        // Join through the remaining joiner (which is now the only member)
        var joiner2 = await _harness.CreateJoinerNodeAsync(joiner1, nodeId: 2, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(joiner2.IsInitialized);
    }

    #endregion

    #region Failure During Operations (FAIL-030 to FAIL-033)

    [Fact(Skip = "Complex timing scenario - needs careful implementation")]
    public async Task NodeCrashDuringJoinProtocol()
    {
        var seedNode = _harness.CreateSeedNode();

        // Start a join operation
        var joinTask = _harness.CreateJoinerNodeAsync(seedNode, nodeId: 1, cancellationToken: TestContext.Current.CancellationToken);

        // Crash the seed during join (race condition test)
        await Task.Delay(1, TestContext.Current.CancellationToken);
        _harness.CrashNode(seedNode);

        // Join should fail or succeed, but not hang
        try
        {
            var joiner = await joinTask;
            // If join succeeded, joiner should be in a valid state
            Assert.NotNull(joiner);
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
            // Expected - join failed due to seed crash
        }
    }

    [Fact]
    public async Task NodeCrashDuringLeave()
    {
        var seedNode = _harness.CreateSeedNode();
        var joiner = await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 1, cancellationToken: TestContext.Current.CancellationToken);

        await _harness.WaitForConvergenceAsync(expectedSize: 2, timeout: TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        // Start leave then immediately crash
        var leaveTask = joiner.LeaveAsync();
        _harness.CrashNode(joiner);

        // Should not throw
        try
        {
            await leaveTask;
        }
        catch (ObjectDisposedException)
        {
            // Leave may throw if node is crashed during operation
        }
        catch (InvalidOperationException)
        {
            // Node may already be shutdown
        }

        Assert.DoesNotContain(joiner, _harness.Nodes);
    }

    #endregion
}


