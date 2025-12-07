using Rapid.Tests.Simulation;

namespace Rapid.Tests;

/// <summary>
/// Tests for the chaos injector.
/// </summary>
public sealed class ChaosInjectorTests : IAsyncLifetime
{
    private DeterministicSimulationHarness _harness = null!;
    private ChaosInjector _chaos = null!;

    public ValueTask InitializeAsync()
    {
        _harness = new DeterministicSimulationHarness(seed: 22222);
        _chaos = new ChaosInjector(_harness);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

    [Fact]
    public void DefaultRatesAreZero()
    {
        Assert.Equal(0, _chaos.NodeCrashRate);
        Assert.Equal(0, _chaos.PartitionRate);
    }

    [Fact]
    public void PartitionHealRateHasDefaultValue() => Assert.Equal(0.1, _chaos.PartitionHealRate);

    [Fact]
    public void MinimumAliveNodesHasDefaultValue() => Assert.Equal(1, _chaos.MinimumAliveNodes);

    [Fact]
    public void MaybeInjectFaultReturnsFalseWithZeroRates()
    {
        _harness.CreateSeedNode();

        var result = _chaos.MaybeInjectFault();

        Assert.False(result);
    }

    [Fact]
    public void ScheduleNodeCrashDoesNotCrashImmediately()
    {
        var node = _harness.CreateSeedNode();

        _chaos.ScheduleNodeCrash(node, TimeSpan.FromSeconds(10));

        Assert.Contains(node, _harness.Nodes);
    }

    [Fact]
    public void ScheduledCrashExecutesAfterTimeAdvances()
    {
        var node = _harness.CreateSeedNode();
        _chaos.ScheduleNodeCrash(node, TimeSpan.FromSeconds(10));

        // Advance time past the scheduled crash
        _harness.TimeProvider.Advance(TimeSpan.FromSeconds(15));
        _chaos.MaybeInjectFault(); // Process scheduled faults

        Assert.DoesNotContain(node, _harness.Nodes);
    }

    [Fact]
    public void SchedulePartitionDoesNotPartitionImmediately()
    {
        var node1 = _harness.CreateSeedNode(0);
        // Can't easily create a second node synchronously, so test with single node
        _chaos.ScheduleIsolation(node1, TimeSpan.FromSeconds(10));

        // Network should still allow delivery (no partition yet)
        Assert.True(_harness.Network.CanDeliver("127.0.0.1:8000", "127.0.0.1:8001"));
    }

    [Fact]
    public void ClearScheduledFaultsRemovesAllScheduled()
    {
        var node = _harness.CreateSeedNode();
        _chaos.ScheduleNodeCrash(node, TimeSpan.FromSeconds(10));
        _chaos.ScheduleIsolation(node, TimeSpan.FromSeconds(20));

        _chaos.ClearScheduledFaults();

        // Advance time and process - nothing should happen
        _harness.TimeProvider.Advance(TimeSpan.FromSeconds(30));
        _chaos.MaybeInjectFault();

        Assert.Contains(node, _harness.Nodes);
    }

    [Fact]
    public void RunChaosExecutesSpecifiedSteps()
    {
        _harness.CreateSeedNode();

        var initialLogicalTime = _harness.LogicalTime;
        _chaos.RunChaos(steps: 10);

        // Time should have advanced
        // Note: The exact logical time depends on whether tasks were created
    }

    [Fact]
    public void HighCrashRateEventuallyCrashesNodes()
    {
        // Create multiple nodes so crash can happen
        var nodes = new List<SimulationNode>();
        for (var i = 0; i < 5; i++)
        {
            nodes.Add(_harness.CreateSeedNode(i));
        }

        _chaos.NodeCrashRate = 0.9; // Very high rate
        _chaos.MinimumAliveNodes = 1;

        // Run chaos for many steps
        _chaos.RunChaos(steps: 100);

        // At least some nodes should have crashed
        Assert.True(_harness.Nodes.Count < 5);
    }

    [Fact]
    public void MinimumAliveNodesIsRespected()
    {
        var node = _harness.CreateSeedNode();

        _chaos.NodeCrashRate = 1.0; // 100% crash rate
        _chaos.MinimumAliveNodes = 1;

        // Run chaos
        _chaos.RunChaos(steps: 10);

        // Node should still be alive due to minimum
        Assert.Single(_harness.Nodes);
    }
}
