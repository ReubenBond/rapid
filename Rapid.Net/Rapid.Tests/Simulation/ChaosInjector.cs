namespace Rapid.Tests.Simulation;

/// <summary>
/// Injects random faults into the simulation for chaos testing.
/// </summary>
/// <remarks>
/// Creates a new chaos injector.
/// </remarks>
internal sealed class ChaosInjector(DeterministicSimulationHarness harness)
{
    private readonly DeterministicSimulationHarness _harness = harness ?? throw new ArgumentNullException(nameof(harness));
    private readonly DeterministicRandom _random = harness.Random.Fork();
    private readonly List<ScheduledFault> _scheduledFaults = [];
    private readonly Lock _lock = new();

    /// <summary>
    /// Gets or sets the probability of a random node crash per step (0.0 to 1.0).
    /// </summary>
    public double NodeCrashRate { get; set; }

    /// <summary>
    /// Gets or sets the probability of a random partition per step (0.0 to 1.0).
    /// </summary>
    public double PartitionRate { get; set; }

    /// <summary>
    /// Gets or sets the probability of healing a random partition per step (0.0 to 1.0).
    /// </summary>
    public double PartitionHealRate { get; set; } = 0.1;

    /// <summary>
    /// Gets or sets the minimum number of nodes to keep alive during chaos.
    /// </summary>
    public int MinimumAliveNodes { get; set; } = 1;

    /// <summary>
    /// Possibly injects a fault based on configured rates.
    /// Call this once per simulation step.
    /// </summary>
    /// <returns>True if a fault was injected.</returns>
    public bool MaybeInjectFault()
    {
        // Process scheduled faults first
        ProcessScheduledFaults();

        // Maybe crash a node
        if (NodeCrashRate > 0 && _random.Chance(NodeCrashRate))
        {
            if (TryCrashRandomNode())
            {
                return true;
            }
        }

        // Maybe create a partition
        if (PartitionRate > 0 && _random.Chance(PartitionRate))
        {
            if (TryCreateRandomPartition())
            {
                return true;
            }
        }

        // Maybe heal a partition
        if (PartitionHealRate > 0 && _random.Chance(PartitionHealRate))
        {
            _harness.Network.HealAllPartitions();
        }

        return false;
    }

    /// <summary>
    /// Schedules a node crash at a future time.
    /// </summary>
    public void ScheduleNodeCrash(SimulationNode node, TimeSpan delay)
    {
        ArgumentNullException.ThrowIfNull(node);
        var executeAt = _harness.TimeProvider.GetUtcNow() + delay;

        lock (_lock)
        {
            _scheduledFaults.Add(new ScheduledFault(FaultType.NodeCrash, executeAt, node, null));
        }
    }

    /// <summary>
    /// Schedules a network partition at a future time.
    /// </summary>
    public void SchedulePartition(SimulationNode node1, SimulationNode node2, TimeSpan delay)
    {
        ArgumentNullException.ThrowIfNull(node1);
        ArgumentNullException.ThrowIfNull(node2);
        var executeAt = _harness.TimeProvider.GetUtcNow() + delay;

        lock (_lock)
        {
            _scheduledFaults.Add(new ScheduledFault(FaultType.Partition, executeAt, node1, node2));
        }
    }

    /// <summary>
    /// Schedules healing of a network partition at a future time.
    /// </summary>
    public void SchedulePartitionHeal(SimulationNode node1, SimulationNode node2, TimeSpan delay)
    {
        ArgumentNullException.ThrowIfNull(node1);
        ArgumentNullException.ThrowIfNull(node2);
        var executeAt = _harness.TimeProvider.GetUtcNow() + delay;

        lock (_lock)
        {
            _scheduledFaults.Add(new ScheduledFault(FaultType.PartitionHeal, executeAt, node1, node2));
        }
    }

    /// <summary>
    /// Schedules node isolation at a future time.
    /// </summary>
    public void ScheduleIsolation(SimulationNode node, TimeSpan delay)
    {
        ArgumentNullException.ThrowIfNull(node);
        var executeAt = _harness.TimeProvider.GetUtcNow() + delay;

        lock (_lock)
        {
            _scheduledFaults.Add(new ScheduledFault(FaultType.Isolation, executeAt, node, null));
        }
    }

    /// <summary>
    /// Schedules node reconnection at a future time.
    /// </summary>
    public void ScheduleReconnect(SimulationNode node, TimeSpan delay)
    {
        ArgumentNullException.ThrowIfNull(node);
        var executeAt = _harness.TimeProvider.GetUtcNow() + delay;

        lock (_lock)
        {
            _scheduledFaults.Add(new ScheduledFault(FaultType.Reconnect, executeAt, node, null));
        }
    }

    /// <summary>
    /// Runs chaos for the specified number of steps.
    /// </summary>
    /// <param name="steps">Number of simulation steps to run.</param>
    /// <param name="stepInterval">Time to advance between steps.</param>
    /// <returns>The number of faults injected.</returns>
    public int RunChaos(int steps, TimeSpan? stepInterval = null)
    {
        var interval = stepInterval ?? TimeSpan.FromMilliseconds(100);
        var faultsInjected = 0;

        for (var i = 0; i < steps; i++)
        {
            // Execute pending tasks
            _harness.StepAll();

            // Maybe inject a fault
            if (MaybeInjectFault())
            {
                faultsInjected++;
            }

            // Advance time
            _harness.TimeProvider.Advance(interval);
        }

        return faultsInjected;
    }

    /// <summary>
    /// Clears all scheduled faults.
    /// </summary>
    public void ClearScheduledFaults()
    {
        lock (_lock)
        {
            _scheduledFaults.Clear();
        }
    }

    private void ProcessScheduledFaults()
    {
        var now = _harness.TimeProvider.GetUtcNow();
        List<ScheduledFault>? toExecute = null;

        lock (_lock)
        {
            toExecute = _scheduledFaults.Where(f => f.ExecuteAt <= now).ToList();
            foreach (var fault in toExecute)
            {
                _scheduledFaults.Remove(fault);
            }
        }

        foreach (var fault in toExecute)
        {
            ExecuteFault(fault);
        }
    }

    private void ExecuteFault(ScheduledFault fault)
    {
        switch (fault.Type)
        {
            case FaultType.NodeCrash:
                if (fault.Node1 != null && _harness.Nodes.Contains(fault.Node1))
                {
                    _harness.CrashNode(fault.Node1);
                }
                break;

            case FaultType.Partition:
                if (fault.Node1 != null && fault.Node2 != null)
                {
                    _harness.PartitionNodes(fault.Node1, fault.Node2);
                }
                break;

            case FaultType.PartitionHeal:
                if (fault.Node1 != null && fault.Node2 != null)
                {
                    _harness.HealPartition(fault.Node1, fault.Node2);
                }
                break;

            case FaultType.Isolation:
                if (fault.Node1 != null && _harness.Nodes.Contains(fault.Node1))
                {
                    _harness.IsolateNode(fault.Node1);
                }
                break;

            case FaultType.Reconnect:
                if (fault.Node1 != null && _harness.Nodes.Contains(fault.Node1))
                {
                    _harness.ReconnectNode(fault.Node1);
                }
                break;
        }
    }

    private bool TryCrashRandomNode()
    {
        var nodes = _harness.Nodes;
        if (nodes.Count <= MinimumAliveNodes)
        {
            return false;
        }

        var node = _random.Choose(nodes.ToList());
        _harness.CrashNode(node);
        return true;
    }

    private bool TryCreateRandomPartition()
    {
        var nodes = _harness.Nodes;
        if (nodes.Count < 2)
        {
            return false;
        }

        var nodeList = nodes.ToList();
        var node1 = _random.Choose(nodeList);
        nodeList.Remove(node1);
        var node2 = _random.Choose(nodeList);

        _harness.PartitionNodes(node1, node2);
        return true;
    }

    private enum FaultType
    {
        NodeCrash,
        Partition,
        PartitionHeal,
        Isolation,
        Reconnect
    }

    private readonly record struct ScheduledFault(
        FaultType Type,
        DateTimeOffset ExecuteAt,
        SimulationNode? Node1,
        SimulationNode? Node2);
}
