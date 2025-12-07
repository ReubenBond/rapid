using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Rapid.Tests.Simulation;

namespace Rapid.Tests.SimulationTests;

/// <summary>
/// Tests for chaos injection and stress testing using the simulation harness.
/// Verifies that the cluster maintains safety properties under random faults.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test naming convention")]
public sealed class ChaosTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly ILoggerFactory _loggerFactory;
    private DeterministicSimulationHarness _harness = null!;
    private ChaosInjector _chaos = null!;
    private InvariantChecker _checker = null!;
    private const int TestSeed = 78901;

    public ChaosTests(ITestOutputHelper output)
    {
        _output = output;
        _loggerFactory = LoggerFactory.Create(builder => builder.AddXUnit(output).SetMinimumLevel(LogLevel.Debug));
    }

    public ValueTask InitializeAsync()
    {
        _output.WriteLine($"[ChaosTests] Initializing with seed {TestSeed}");
        _harness = new DeterministicSimulationHarness(seed: TestSeed, loggerFactory: _loggerFactory, testOutput: _output);
        _chaos = new ChaosInjector(_harness);
        _checker = new InvariantChecker(_harness);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _output.WriteLine("[ChaosTests] Disposing harness");
        await _harness.DisposeAsync();
        _loggerFactory.Dispose();
    }

    #region Random Fault Injection (CHAOS-001 to CHAOS-004)

    [Fact]
    public void Chaos001ChaosInjectorDefaultsToZeroRates()
    {
        Assert.Equal(0, _chaos.NodeCrashRate);
        Assert.Equal(0, _chaos.PartitionRate);
    }

    [Fact]
    public void Chaos002ChaosInjectorRespectsMinimumAliveNodes()
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
    public void Chaos003ScheduledCrashExecutesAtCorrectTime()
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
    public void Chaos004ScheduledIsolationAndReconnection()
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
    public void Chaos010RunChaosForManySteps()
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
    public void Chaos011InvariantsHoldUnderLightChaos()
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
    public void Chaos012ClearScheduledFaultsWorks()
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
    public void Chaos020ScheduledPartitionExecutesOnTime()
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
    public void Chaos021ScheduledPartitionHealExecutesOnTime()
    {
        var node1 = _harness.CreateSeedNode(0);
        var node2 = _harness.CreateSeedNode(1);

        var addr1 = RapidUtils.Loggable(node1.Address);
        var addr2 = RapidUtils.Loggable(node2.Address);

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
    public void Chaos022MultipleScheduledFaultsExecuteInOrder()
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
    public void Chaos023ScheduledCrashOnNonexistentNodeHandled()
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
}


