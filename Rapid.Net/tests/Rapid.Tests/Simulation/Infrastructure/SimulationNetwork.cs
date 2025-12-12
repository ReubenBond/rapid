using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rapid.Tests.Simulation.Infrastructure.Logging;

namespace Rapid.Tests.Simulation.Infrastructure;

/// <summary>
/// Result of checking whether a message can be delivered.
/// </summary>
internal enum DeliveryStatus
{
    /// <summary>Message can be delivered normally.</summary>
    Success,
    /// <summary>Message was randomly dropped (transient failure, should retry).</summary>
    Dropped,
    /// <summary>Message blocked by network partition (persistent failure).</summary>
    Partitioned
}

/// <summary>
/// Simulates a network for in-memory transport between nodes.
/// Provides hooks for injecting network faults like delays, partitions, and message loss.
/// </summary>
internal sealed class SimulationNetwork
{
    private readonly SimulationHarness _harness;
    private readonly SimulationRandom _random;
    private readonly ConcurrentDictionary<string, HashSet<string>> _partitions = new();
    private readonly Lock _lock = new();
    private SimulationNetworkLogger _log;

    /// <summary>
    /// Gets or sets the base message delay for all messages.
    /// </summary>
    public TimeSpan BaseMessageDelay { get; set; } = TimeSpan.FromMilliseconds(1);

    /// <summary>
    /// Gets or sets the maximum additional random delay for messages.
    /// </summary>
    public TimeSpan MaxJitter { get; set; } = TimeSpan.FromMilliseconds(5);

    /// <summary>
    /// Gets or sets the probability of a message being dropped (0.0 to 1.0).
    /// </summary>
    public double MessageDropRate { get; set; }

    /// <summary>
    /// Gets or sets whether to simulate message delays.
    /// </summary>
    public bool EnableDelays { get; set; }

    internal SimulationNetwork(SimulationHarness harness, SimulationRandom random)
    {
        _harness = harness;
        _random = random;
        _log = new SimulationNetworkLogger(NullLogger<SimulationNetwork>.Instance);
    }

    internal void SetLogger(ILogger<SimulationNetwork> logger) => _log = new SimulationNetworkLogger(logger);

    /// <summary>
    /// Creates a network partition between two nodes (unidirectional).
    /// Messages from sourceAddress will not reach targetAddress.
    /// </summary>
    public void CreatePartition(string sourceAddress, string targetAddress)
    {
        lock (_lock)
        {
            var blocked = _partitions.GetOrAdd(sourceAddress, _ => []);
            blocked.Add(targetAddress);
        }
        _log.PartitionCreated(sourceAddress, targetAddress);
    }

    /// <summary>
    /// Creates a bidirectional network partition between two nodes.
    /// </summary>
    public void CreateBidirectionalPartition(string node1, string node2)
    {
        _log.BidirectionalPartitionCreating(node1, node2);
        CreatePartition(node1, node2);
        CreatePartition(node2, node1);
    }

    /// <summary>
    /// Removes a network partition between two nodes (unidirectional).
    /// </summary>
    public void HealPartition(string sourceAddress, string targetAddress)
    {
        lock (_lock)
        {
            if (_partitions.TryGetValue(sourceAddress, out var blocked))
            {
                blocked.Remove(targetAddress);
            }
        }
        _log.PartitionHealed(sourceAddress, targetAddress);
    }

    /// <summary>
    /// Removes a bidirectional network partition between two nodes.
    /// </summary>
    public void HealBidirectionalPartition(string node1, string node2)
    {
        _log.BidirectionalPartitionHealing(node1, node2);
        HealPartition(node1, node2);
        HealPartition(node2, node1);
    }

    /// <summary>
    /// Removes all network partitions.
    /// </summary>
    public void HealAllPartitions()
    {
        int count;
        lock (_lock)
        {
            count = _partitions.Count;
            _partitions.Clear();
        }
        _log.AllPartitionsHealed(count);
    }

    /// <summary>
    /// Isolates a node from all other nodes (bidirectional).
    /// </summary>
    public void IsolateNode(string nodeAddress)
    {
        _log.NodeIsolating(nodeAddress);
        foreach (var node in _harness.AllNodes)
        {
            var addr = RapidUtils.Loggable(node.Address);
            if (addr != nodeAddress)
            {
                CreateBidirectionalPartition(nodeAddress, addr);
            }
        }
    }

    /// <summary>
    /// Removes isolation from a node.
    /// </summary>
    public void ReconnectNode(string nodeAddress)
    {
        _log.NodeReconnecting(nodeAddress);
        foreach (var node in _harness.AllNodes)
        {
            var addr = RapidUtils.Loggable(node.Address);
            if (addr != nodeAddress)
            {
                HealBidirectionalPartition(nodeAddress, addr);
            }
        }
    }

    /// <summary>
    /// Checks if a node is isolated (has partitions with all other nodes).
    /// </summary>
    public bool IsNodeIsolated(string nodeAddress)
    {
        lock (_lock)
        {
            if (!_partitions.TryGetValue(nodeAddress, out var blocked))
            {
                return false;
            }

            // Check if node is partitioned from all other nodes
            foreach (var node in _harness.AllNodes)
            {
                var addr = RapidUtils.Loggable(node.Address);
                if (addr != nodeAddress && !blocked.Contains(addr))
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// Checks if a message can be delivered from source to target.
    /// Returns a status indicating success or reason for failure.
    /// </summary>
    internal DeliveryStatus CheckDelivery(string sourceAddress, string targetAddress)
    {
        // Self-messages (loopback) are always delivered reliably.
        // In real networks, loopback communication doesn't go through the network.
        if (string.Equals(sourceAddress, targetAddress, StringComparison.Ordinal))
        {
            return DeliveryStatus.Success;
        }

        // Check for network partition first (persistent)
        lock (_lock)
        {
            if (_partitions.TryGetValue(sourceAddress, out var blocked) && blocked.Contains(targetAddress))
            {
                _log.MessageBlockedByPartition(sourceAddress, targetAddress);
                return DeliveryStatus.Partitioned;
            }
        }

        // Check for random message drop (transient)
        if (MessageDropRate > 0 && _random.Chance(MessageDropRate))
        {
            _log.MessageDroppedRandom(sourceAddress, targetAddress);
            return DeliveryStatus.Dropped;
        }

        return DeliveryStatus.Success;
    }

    /// <summary>
    /// Checks if a message can be delivered from source to target.
    /// This is a convenience method that returns true only if delivery would succeed.
    /// </summary>
    internal bool CanDeliver(string sourceAddress, string targetAddress)
    {
        return CheckDelivery(sourceAddress, targetAddress) == DeliveryStatus.Success;
    }

    /// <summary>
    /// Gets the simulated delay for a message.
    /// </summary>
    internal TimeSpan GetMessageDelay()
    {
        if (!EnableDelays)
        {
            return TimeSpan.Zero;
        }

        var jitter = _random.NextTimeSpan(MaxJitter);
        return BaseMessageDelay + jitter;
    }
}
