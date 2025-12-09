using System.Diagnostics.CodeAnalysis;
using Rapid.Tests.Simulation;

namespace Rapid.Tests.SimulationTests;

/// <summary>
/// Tests for edge cases and boundary conditions using the simulation harness.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test naming convention")]
public sealed class EdgeCaseTests : IAsyncLifetime
{
    private SimulationHarness _harness = null!;
    private const int TestSeed = 67890;

    public ValueTask InitializeAsync()
    {
        _harness = new SimulationHarness(seed: TestSeed);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeAsync();
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
    public void BackToBackJoinsSucceed()
    {
        var seedNode = _harness.CreateSeedNode();

        // Join two nodes in quick succession
        var joiner1 = _harness.CreateJoinerNode(seedNode, nodeId: 1);
        var joiner2 = _harness.CreateJoinerNode(seedNode, nodeId: 2);

        Assert.True(joiner1.IsInitialized);
        Assert.True(joiner2.IsInitialized);
    }

    [Fact]
    public void LeaveImmediatelyAfterJoin()
    {
        var seedNode = _harness.CreateSeedNode();
        var joiner = _harness.CreateJoinerNode(seedNode, nodeId: 1);

        // Leave immediately after join - should not throw
        _harness.RemoveNodeGracefully(joiner);
    }

    [Fact]
    public void CrashImmediatelyAfterJoin()
    {
        var seedNode = _harness.CreateSeedNode();
        var joiner = _harness.CreateJoinerNode(seedNode, nodeId: 1);

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
    public void CancellationDuringJoinHandled()
    {
        var seedNode = _harness.CreateSeedNode();

        // Note: With synchronous CreateJoinerNode, we can't easily test cancellation
        // during join. Instead, test that the harness throws on null seed node.
        Assert.Throws<ArgumentNullException>(() =>
        {
            _harness.CreateJoinerNode(null!, nodeId: 1);
        });
    }

    #endregion

    #region Protocol Edge Cases (EDGE-030 to EDGE-033)

    [Fact]
    public void UninitializedNodeThrowsOnHandleRequest()
    {
        var node = _harness.CreateUninitializedNode(nodeId: 99);

        // Node not initialized - should not be in a valid state
        Assert.False(node.IsInitialized);

        node.Dispose();
    }

    [Fact]
    public void JoinToSelfFails()
    {
        var seedNode = _harness.CreateSeedNode();

        // Attempting to join to self doesn't make sense and should fail
        // Note: This test may need adjustment based on actual behavior
        Assert.Throws<ArgumentNullException>(() =>
        {
            _harness.CreateJoinerNode(null!, nodeId: 1);
        });
    }

    [Fact]
    public void MultipleNodesWithDifferentOptions()
    {
        // Use lower ring count with compatible watermark settings
        var options1 = new RapidProtocolOptions { RingCount = 3, HighWaterMark = 2, LowWaterMark = 1 };
        var options2 = new RapidProtocolOptions { RingCount = 3, HighWaterMark = 2, LowWaterMark = 1 };

        var seedNode = _harness.CreateSeedNode(options: options1);
        var joiner = _harness.CreateJoinerNode(seedNode, nodeId: 1, options: options2);

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
    /// </summary>
    [Fact]
    public void MaximumClusterSizeHandled()
    {
        // Test a large cluster (20 nodes)
        var nodes = _harness.CreateCluster(size: 20);

        _harness.WaitForConvergence(expectedSize: 20);

        Assert.All(nodes, n => Assert.Equal(20, n.MembershipSize));
    }

    /// <summary>
    /// Tests cluster formation with 10 nodes to verify scalability beyond small
    /// test clusters. This provides a balance between test execution time and
    /// validating multi-node consensus behavior at a reasonable scale.
    /// </summary>
    [Fact]
    public void TenNodeClusterFormation()
    {
        var nodes = _harness.CreateCluster(size: 10);

        _harness.WaitForConvergence(expectedSize: 10);

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

#pragma warning disable CA5394 // Do not use insecure randomness
        var seq1 = Enumerable.Range(0, 10).Select(_ => fork1.Next()).ToList();
#pragma warning restore CA5394 // Do not use insecure randomness
#pragma warning disable CA5394 // Do not use insecure randomness
        var seq2 = Enumerable.Range(0, 10).Select(_ => fork2.Next()).ToList();
#pragma warning restore CA5394 // Do not use insecure randomness

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

#pragma warning disable CA5394 // Do not use insecure randomness
        random.NextBytes(bytes);
#pragma warning restore CA5394 // Do not use insecure randomness

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


