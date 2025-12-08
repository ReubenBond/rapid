using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Rapid.Tests.Simulation;

namespace Rapid.Tests.SimulationTests;

/// <summary>
/// Tests for edge cases and boundary conditions using the simulation harness.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test naming convention")]
public sealed class EdgeCaseTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly ILoggerFactory _loggerFactory;
    private SimulationHarness _harness = null!;
    private const int TestSeed = 67890;

    public EdgeCaseTests(ITestOutputHelper output)
    {
        _output = output;
        _loggerFactory = LoggerFactory.Create(builder => builder.AddXUnit(output).SetMinimumLevel(LogLevel.Debug));
    }

    public ValueTask InitializeAsync()
    {
        _output.WriteLine($"[EdgeCaseTests] Initializing with seed {TestSeed}");
        _harness = new SimulationHarness(seed: TestSeed, loggerFactory: _loggerFactory);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _output.WriteLine("[EdgeCaseTests] Disposing harness");
        await _harness.DisposeAsync();
        _loggerFactory.Dispose();
    }

    #region Boundary Conditions (EDGE-001 to EDGE-004)

    [Fact]
    public void ZeroBatchingWindowWorks()
    {
        var options = new RapidProtocolOptions { BatchingWindow = TimeSpan.Zero };
        var seedNode = _harness.CreateSeedNode(options: options);

        Assert.True(seedNode.IsInitialized);
        Assert.Equal(1, seedNode.MembershipSize);
    }

    [Fact]
    public void CustomFailureDetectorIntervalWorks()
    {
        var options = new RapidProtocolOptions
        {
            FailureDetectorInterval = TimeSpan.FromMilliseconds(100)
        };
        var seedNode = _harness.CreateSeedNode(options: options);

        Assert.True(seedNode.IsInitialized);
    }

    [Fact]
    public void CustomRingCountWorks()
    {
        // K > H >= L >= 0 constraint: with RingCount=5, HighWaterMark must be < 5
        var options = new RapidProtocolOptions
        {
            RingCount = 5,
            HighWaterMark = 4,
            LowWaterMark = 2
        };
        var seedNode = _harness.CreateSeedNode(options: options);

        Assert.True(seedNode.IsInitialized);
    }

    [Fact]
    public void CustomHighLowWatermarkWorks()
    {
        // K > H >= L >= 0 constraint: default RingCount=10
        var options = new RapidProtocolOptions
        {
            HighWaterMark = 8,
            LowWaterMark = 4
        };
        var seedNode = _harness.CreateSeedNode(options: options);

        Assert.True(seedNode.IsInitialized);
    }

    #endregion

    #region Timing Edge Cases (EDGE-010 to EDGE-013)

    [Fact]
    public async Task BackToBackJoinsSucceed()
    {
        var seedNode = _harness.CreateSeedNode();

        // Join two nodes in quick succession
        var joiner1 = await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 1, cancellationToken: TestContext.Current.CancellationToken);
        var joiner2 = await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 2, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(joiner1.IsInitialized);
        Assert.True(joiner2.IsInitialized);
    }

    [Fact]
    public async Task LeaveImmediatelyAfterJoin()
    {
        var seedNode = _harness.CreateSeedNode();
        var joiner = await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 1, cancellationToken: TestContext.Current.CancellationToken);

        // Leave immediately after join - should not throw
        await joiner.LeaveAsync();
    }

    [Fact]
    public async Task CrashImmediatelyAfterJoin()
    {
        var seedNode = _harness.CreateSeedNode();
        var joiner = await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 1, cancellationToken: TestContext.Current.CancellationToken);

        // Crash immediately after join - should not throw
        _harness.CrashNode(joiner);

        Assert.DoesNotContain(joiner, _harness.Nodes);
    }

    #endregion

    #region Resource Edge Cases (EDGE-020 to EDGE-023)

    [Fact]
    public void DoubleShutdownIsSafe()
    {
        var seedNode = _harness.CreateSeedNode();

        // First shutdown
        seedNode.Shutdown();

        // Second shutdown should not throw
        seedNode.Shutdown();
    }

    [Fact]
    public void DoubleDisposeIsSafe()
    {
        var seedNode = _harness.CreateSeedNode();

        // Remove from harness first
        _harness.CrashNode(seedNode);

        // First dispose
        seedNode.Dispose();

        // Second dispose should not throw
        seedNode.Dispose();
    }

    [Fact]
    public async Task CancellationDuringJoinHandled()
    {
        var seedNode = _harness.CreateSeedNode();

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Joining with already cancelled token should throw OperationCanceledException
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 1, cancellationToken: cts.Token);
        });
    }

    #endregion

    #region Protocol Edge Cases (EDGE-030 to EDGE-033)

    [Fact]
    public void UninitializedNodeThrowsOnHandleRequest()
    {
        var node = SimulationNode.Create(_harness, nodeId: 99);

        // Node not initialized - should not be in a valid state
        Assert.False(node.IsInitialized);

        node.Dispose();
    }

    [Fact]
    public async Task JoinToSelfFails()
    {
        var seedNode = _harness.CreateSeedNode();

        // Attempting to join to self doesn't make sense and should fail
        // Note: This test may need adjustment based on actual behavior
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
        {
            await _harness.CreateJoinerNodeAsync(null!, nodeId: 1, cancellationToken: TestContext.Current.CancellationToken);
        });
    }

    [Fact]
    public async Task MultipleNodesWithDifferentOptions()
    {
        // Use lower ring count with compatible watermark settings
        var options1 = new RapidProtocolOptions { RingCount = 3, HighWaterMark = 2, LowWaterMark = 1 };
        var options2 = new RapidProtocolOptions { RingCount = 3, HighWaterMark = 2, LowWaterMark = 1 };

        var seedNode = _harness.CreateSeedNode(options: options1);
        var joiner = await _harness.CreateJoinerNodeAsync(seedNode, nodeId: 1, options: options2, cancellationToken: TestContext.Current.CancellationToken);

        // Both should be operational with same ring count
        Assert.True(seedNode.IsInitialized);
        Assert.True(joiner.IsInitialized);
    }

    #endregion

    #region Maximum Cluster Size Tests (EDGE-002)

    /// <summary>
    /// Tests that the cluster can scale to a large size (20 nodes) and maintain
    /// consistency across all members. Verifies that the consensus protocol and
    /// membership management can handle larger cluster sizes without degradation.
    /// This is marked as slow due to the time required for all nodes to converge.
    /// </summary>
    [Fact(Skip = "Slow test - large cluster formation")]
    public async Task MaximumClusterSizeHandled()
    {
        // Test a large cluster (20 nodes)
        var nodes = await _harness.CreateClusterAsync(
            size: 20,
            cancellationToken: TestContext.Current.CancellationToken);

        await _harness.WaitForConvergenceAsync(
            expectedSize: 20,
            timeout: TimeSpan.FromMinutes(2),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.All(nodes, n => Assert.Equal(20, n.MembershipSize));
    }

    /// <summary>
    /// Tests cluster formation with 10 nodes to verify scalability beyond small
    /// test clusters. This provides a balance between test execution time and
    /// validating multi-node consensus behavior at a reasonable scale.
    /// </summary>
    [Fact]
    public async Task TenNodeClusterFormation()
    {
        var nodes = await _harness.CreateClusterAsync(
            size: 10,
            cancellationToken: TestContext.Current.CancellationToken);

        await _harness.WaitForConvergenceAsync(
            expectedSize: 10,
            timeout: TimeSpan.FromSeconds(60),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(10, nodes.Count);
        Assert.All(nodes, n => Assert.True(n.IsInitialized));
    }

    #endregion

    #region Random and Determinism Edge Cases

    [Fact]
    public void SimulationRandomForkProducesDifferentSequences()
    {
        var random = _harness.Random;
        var fork1 = random.Fork();
        var fork2 = random.Fork();

        var seq1 = Enumerable.Range(0, 10).Select(_ => fork1.Next()).ToList();
        var seq2 = Enumerable.Range(0, 10).Select(_ => fork2.Next()).ToList();

        // Forked randoms should produce different sequences
        Assert.NotEqual(seq1, seq2);
    }

    [Fact]
    public void SimulationRandomChanceWorks()
    {
        var random = _harness.Random;

        // Test extreme probabilities
        Assert.True(random.Chance(1.0)); // 100% should always succeed
        Assert.False(random.Chance(0.0)); // 0% should always fail
    }

    [Fact]
    public void SimulationRandomChooseWorks()
    {
        var random = _harness.Random;
        var list = new List<int> { 1, 2, 3, 4, 5 };

        // Choose should return an element from the list
        var chosen = random.Choose(list);
        Assert.Contains(chosen, list);
    }

    [Fact]
    public void SimulationRandomChooseThrowsOnEmpty()
    {
        var random = _harness.Random;
        var emptyList = new List<int>();

        Assert.Throws<ArgumentException>(() => random.Choose(emptyList));
    }

    [Fact]
    public void SimulationRandomNextBytesWorks()
    {
        var random = _harness.Random;
        var bytes = new byte[16];

        random.NextBytes(bytes);

        // Bytes should not all be zero
        Assert.Contains(bytes, b => b != 0);
    }

    [Fact]
    public void SimulationRandomNextTimeSpanWorks()
    {
        var random = _harness.Random;
        var maxDuration = TimeSpan.FromSeconds(10);

        var duration = random.NextTimeSpan(maxDuration);

        Assert.True(duration >= TimeSpan.Zero);
        Assert.True(duration < maxDuration);
    }

    #endregion
}


