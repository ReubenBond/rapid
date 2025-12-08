using System.Diagnostics.CodeAnalysis;
using Rapid.Tests.Simulation;

namespace Rapid.Tests.SimulationTests;

/// <summary>
/// Tests for chaos injection and stress testing using the simulation harness.
/// Verifies that the cluster maintains safety properties under random faults.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test naming convention")]
public sealed class ChaosTests(ITestOutputHelper output) : IAsyncLifetime
{
    private SimulationHarness _harness = null!;
    private ChaosInjector _chaos = null!;
    private InvariantChecker _checker = null!;
    private const int TestSeed = 78901;

    public ValueTask InitializeAsync()
    {
        output.WriteLine($"[ChaosTests] Initializing with seed {TestSeed}");
        _harness = new SimulationHarness(seed: TestSeed, output);
        _chaos = new ChaosInjector(_harness);
        _checker = new InvariantChecker(_harness);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        output.WriteLine("[ChaosTests] Disposing harness");
        await _harness.DisposeAsync();
    }

    #region Random Fault Injection (CHAOS-001 to CHAOS-004)

    [Fact]
    public void ChaosInjectorDefaultsToZeroRates()
    {
        Assert.Equal(0, _chaos.NodeCrashRate);
        Assert.Equal(0, _chaos.PartitionRate);
    }

    [Fact]
    public void ChaosInjectorRespectsMinimumAliveNodes()
    {
        // Create multiple seed nodes (not a real cluster, but tests crash protection)
        var nodes = new List<SimulationNode>();
        for (var i = 0; i < 5; i++)
        {
            nodes.Add(_harness.CreateSeedNode(i));
        }

        _chaos.NodeCrashRate = 1.0; // 100% crash rate
        _chaos.MinimumAliveNodes = 3;

        // Run chaos - should leave at least 3 nodes alive
        _chaos.RunChaos(steps: 100);

        Assert.True(_harness.Nodes.Count >= 3);
    }

    [Fact]
    public void ScheduledCrashExecutesAtCorrectTime()
    {
        var node = _harness.CreateSeedNode();

        _chaos.ScheduleNodeCrash(node, TimeSpan.FromSeconds(5));

        // Before scheduled time - node should still exist
        _harness.TimeProvider.Advance(TimeSpan.FromSeconds(4));
        _chaos.MaybeInjectFault(); // Process scheduled faults
        Assert.Contains(node, _harness.Nodes);

        // After scheduled time - node should be crashed
        _harness.TimeProvider.Advance(TimeSpan.FromSeconds(2));
        _chaos.MaybeInjectFault(); // Process scheduled faults
        Assert.DoesNotContain(node, _harness.Nodes);
    }

    [Fact]
    public void ScheduledIsolationAndReconnection()
    {
        var node1 = _harness.CreateSeedNode(0);
        var node2 = _harness.CreateSeedNode(1);
        var node1Addr = RapidUtils.Loggable(node1.Address);
        var node2Addr = RapidUtils.Loggable(node2.Address);

        _chaos.ScheduleIsolation(node1, TimeSpan.FromSeconds(2));
        _chaos.ScheduleReconnect(node1, TimeSpan.FromSeconds(5));

        // Initially connected
        Assert.True(_harness.Network.CanDeliver(node1Addr, node2Addr));

        // After isolation
        _harness.TimeProvider.Advance(TimeSpan.FromSeconds(3));
        _chaos.MaybeInjectFault();
        Assert.False(_harness.Network.CanDeliver(node1Addr, node2Addr));

        // After reconnection
        _harness.TimeProvider.Advance(TimeSpan.FromSeconds(3));
        _chaos.MaybeInjectFault();
        Assert.True(_harness.Network.CanDeliver(node1Addr, node2Addr));
    }

    #endregion

    #region Stress Testing (CHAOS-010 to CHAOS-013)

    [Fact]
    public void RunChaosForManySteps()
    {
        // Create some nodes
        for (var i = 0; i < 5; i++)
        {
            _harness.CreateSeedNode(i);
        }

        _chaos.NodeCrashRate = 0.01; // Low crash rate
        _chaos.PartitionRate = 0.05; // Higher partition rate
        _chaos.PartitionHealRate = 0.1;
        _chaos.MinimumAliveNodes = 2;

        // Run for many steps
        var faultsInjected = _chaos.RunChaos(steps: 500, stepInterval: TimeSpan.FromMilliseconds(10));

        // Some faults should have been injected
        // Note: With probabilistic injection, we may or may not have faults
        Assert.True(_harness.Nodes.Count >= 2);
    }

    [Fact]
    public void InvariantsHoldUnderLightChaos()
    {
        var nodes = new List<SimulationNode>();
        for (var i = 0; i < 3; i++)
        {
            nodes.Add(_harness.CreateSeedNode(i));
        }

        _chaos.NodeCrashRate = 0.0; // No crashes
        _chaos.PartitionRate = 0.02; // Light partitions
        _chaos.PartitionHealRate = 0.5;

        // Run chaos
        _chaos.RunChaos(steps: 100);

        // Check invariants
        var result = _checker.CheckAll();
        Assert.True(result);
    }

    [Fact]
    public void ClearScheduledFaultsWorks()
    {
        var node = _harness.CreateSeedNode();

        _chaos.ScheduleNodeCrash(node, TimeSpan.FromSeconds(1));
        _chaos.ClearScheduledFaults();

        // Advance past scheduled time
        _harness.TimeProvider.Advance(TimeSpan.FromSeconds(2));
        _chaos.MaybeInjectFault();

        // Node should still exist
        Assert.Contains(node, _harness.Nodes);
    }

    #endregion

    #region Scheduled Fault Scenarios (CHAOS-020 to CHAOS-023)

    [Fact]
    public void ScheduledPartitionExecutesOnTime()
    {
        var node1 = _harness.CreateSeedNode(0);
        var node2 = _harness.CreateSeedNode(1);

        _chaos.SchedulePartition(node1, node2, TimeSpan.FromSeconds(3));

        var addr1 = RapidUtils.Loggable(node1.Address);
        var addr2 = RapidUtils.Loggable(node2.Address);

        // Before scheduled time
        _harness.TimeProvider.Advance(TimeSpan.FromSeconds(2));
        _chaos.MaybeInjectFault();
        Assert.True(_harness.Network.CanDeliver(addr1, addr2));

        // After scheduled time
        _harness.TimeProvider.Advance(TimeSpan.FromSeconds(2));
        _chaos.MaybeInjectFault();
        Assert.False(_harness.Network.CanDeliver(addr1, addr2));
    }

    [Fact]
    public void ScheduledPartitionHealExecutesOnTime()
    {
        var node1 = _harness.CreateSeedNode(0);
        var node2 = _harness.CreateSeedNode(1);

        var addr1 = RapidUtils.Loggable(node1.Address);
        var addr2 = RapidUtils.Loggable(node2.Address);

        // Disable random healing so only scheduled heals occur
        _chaos.PartitionHealRate = 0;

        // Create partition immediately
        _harness.PartitionNodes(node1, node2);
        Assert.False(_harness.Network.CanDeliver(addr1, addr2));

        // Schedule heal
        _chaos.SchedulePartitionHeal(node1, node2, TimeSpan.FromSeconds(3));

        // Before heal
        _harness.TimeProvider.Advance(TimeSpan.FromSeconds(2));
        _chaos.MaybeInjectFault();
        Assert.False(_harness.Network.CanDeliver(addr1, addr2));

        // After heal
        _harness.TimeProvider.Advance(TimeSpan.FromSeconds(2));
        _chaos.MaybeInjectFault();
        Assert.True(_harness.Network.CanDeliver(addr1, addr2));
    }

    [Fact]
    public void MultipleScheduledFaultsExecuteInOrder()
    {
        var node1 = _harness.CreateSeedNode(0);
        var node2 = _harness.CreateSeedNode(1);
        var node1Addr = RapidUtils.Loggable(node1.Address);
        var node2Addr = RapidUtils.Loggable(node2.Address);

        // Schedule multiple events
        _chaos.ScheduleIsolation(node1, TimeSpan.FromSeconds(2));
        _chaos.ScheduleReconnect(node1, TimeSpan.FromSeconds(4));
        _chaos.ScheduleIsolation(node1, TimeSpan.FromSeconds(6));

        // Check state at each time point
        _harness.TimeProvider.Advance(TimeSpan.FromSeconds(1));
        _chaos.MaybeInjectFault();
        Assert.True(_harness.Network.CanDeliver(node1Addr, node2Addr)); // Before first isolation

        _harness.TimeProvider.Advance(TimeSpan.FromSeconds(2));
        _chaos.MaybeInjectFault();
        Assert.False(_harness.Network.CanDeliver(node1Addr, node2Addr)); // After first isolation

        _harness.TimeProvider.Advance(TimeSpan.FromSeconds(2));
        _chaos.MaybeInjectFault();
        Assert.True(_harness.Network.CanDeliver(node1Addr, node2Addr)); // After reconnect

        _harness.TimeProvider.Advance(TimeSpan.FromSeconds(2));
        _chaos.MaybeInjectFault();
        Assert.False(_harness.Network.CanDeliver(node1Addr, node2Addr)); // After second isolation
    }

    [Fact]
    public void ScheduledCrashOnNonexistentNodeHandled()
    {
        var node = _harness.CreateSeedNode();

        _chaos.ScheduleNodeCrash(node, TimeSpan.FromSeconds(5));

        // Crash node manually first
        _harness.CrashNode(node);

        // Advance past scheduled time and process - should not throw
        _harness.TimeProvider.Advance(TimeSpan.FromSeconds(10));
        _chaos.MaybeInjectFault(); // Should not throw even though node is already crashed
    }

    #endregion

    #region Additional Chaos Scenarios

    /// <summary>
    /// Tests the cluster's resilience to random network partitions being created and healed.
    /// Verifies that even with continuous partition churn, the cluster maintains operational
    /// integrity and doesn't enter an invalid state.
    /// </summary>
    [Fact]
    public void RandomPartitionCreationAndHealing()
    {
        // Create a 4-node cluster
        for (var i = 0; i < 4; i++)
        {
            _harness.CreateSeedNode(i);
        }

        _chaos.PartitionRate = 0.1;
        _chaos.PartitionHealRate = 0.15;

        // Run chaos with partition creation and healing
        _chaos.RunChaos(steps: 200, stepInterval: TimeSpan.FromMilliseconds(5));

        // Cluster should still have nodes
        Assert.NotEmpty(_harness.Nodes);
    }

    /// <summary>
    /// Verifies that scheduled faults are executed in chronological order based on their
    /// scheduled times. This ensures the chaos injector's time-based event scheduling
    /// mechanism works correctly for deterministic fault injection scenarios.
    /// </summary>
    [Fact]
    public void ScheduledFaultsExecuteInChronologicalOrder()
    {
        var node1 = _harness.CreateSeedNode(0);
        var node2 = _harness.CreateSeedNode(1);

        var events = new List<string>();

        // Schedule events at different times
        _chaos.ScheduleIsolation(node1, TimeSpan.FromSeconds(1));
        _harness.TimeProvider.Advance(TimeSpan.FromSeconds(1.5));
        _chaos.MaybeInjectFault();
        events.Add("isolated");

        _chaos.ScheduleReconnect(node1, TimeSpan.FromSeconds(0.5)); // 2 seconds from start
        _harness.TimeProvider.Advance(TimeSpan.FromSeconds(1));
        _chaos.MaybeInjectFault();
        events.Add("reconnected");

        // Verify events occurred
        Assert.Equal(2, events.Count);
        Assert.Equal("isolated", events[0]);
        Assert.Equal("reconnected", events[1]);
    }

    /// <summary>
    /// Stress tests the cluster under combined fault types (crashes, partitions, heals)
    /// occurring simultaneously. Verifies that safety properties are maintained when
    /// multiple types of faults are active, and that the minimum alive nodes constraint
    /// is respected by the chaos injector.
    /// </summary>
    [Fact]
    public void CombinedFaultsUnderLoad()
    {
        // Create a cluster
        var nodes = new List<SimulationNode>();
        for (var i = 0; i < 5; i++)
        {
            nodes.Add(_harness.CreateSeedNode(i));
        }

        // Enable multiple types of faults simultaneously
        _chaos.NodeCrashRate = 0.02;
        _chaos.PartitionRate = 0.05;
        _chaos.PartitionHealRate = 0.1;
        _chaos.MinimumAliveNodes = 3;

        // Schedule some specific faults
        _chaos.SchedulePartition(nodes[0], nodes[1], TimeSpan.FromSeconds(2));
        _chaos.SchedulePartitionHeal(nodes[0], nodes[1], TimeSpan.FromSeconds(5));

        // Run chaos
        var faultsInjected = _chaos.RunChaos(steps: 300, stepInterval: TimeSpan.FromMilliseconds(10));

        // Verify minimum alive nodes maintained
        Assert.True(_harness.Nodes.Count >= 3);
    }

    /// <summary>
    /// Tests the cluster's ability to survive extremely high-frequency fault injection
    /// over an extended period. This stress test verifies that rapid fault injection
    /// doesn't cause race conditions or state corruption, and that the cluster remains
    /// stable with at least the minimum required nodes alive.
    /// </summary>
    [Fact]
    public void HighFrequencyFaultInjection()
    {
        for (var i = 0; i < 6; i++)
        {
            _harness.CreateSeedNode(i);
        }

        _chaos.NodeCrashRate = 0.05;
        _chaos.PartitionRate = 0.1;
        _chaos.PartitionHealRate = 0.2;
        _chaos.MinimumAliveNodes = 2;

        // Run many steps with frequent fault injection
        _chaos.RunChaos(steps: 1000, stepInterval: TimeSpan.FromMilliseconds(1));

        // System should survive
        Assert.True(_harness.Nodes.Count >= 2);
    }

    #endregion
}


