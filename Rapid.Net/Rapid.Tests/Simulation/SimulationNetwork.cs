using System.Collections.Concurrent;
using Rapid.Messaging;
using Rapid.Pb;

namespace Rapid.Tests.Simulation;

/// <summary>
/// Simulates a network for in-memory transport between nodes.
/// Provides hooks for injecting network faults like delays, partitions, and message loss.
/// </summary>
internal sealed class SimulationNetwork
{
    private readonly SimulationEnvironment _environment;
    private readonly ConcurrentDictionary<string, HashSet<string>> _partitions = new();
    private readonly Lock _partitionLock = new();

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
    public bool EnableDelays { get; set; } = true;

    internal SimulationNetwork(SimulationEnvironment environment)
    {
        _environment = environment;
        // Disable delays by default for faster tests
        EnableDelays = false;
    }

    /// <summary>
    /// Creates a network partition between two nodes (unidirectional).
    /// Messages from sourceAddress will not reach targetAddress.
    /// </summary>
    public void CreatePartition(string sourceAddress, string targetAddress)
    {
        lock (_partitionLock)
        {
            var blocked = _partitions.GetOrAdd(sourceAddress, _ => []);
            blocked.Add(targetAddress);
        }
    }

    /// <summary>
    /// Creates a bidirectional network partition between two nodes.
    /// </summary>
    public void CreateBidirectionalPartition(string node1, string node2)
    {
        CreatePartition(node1, node2);
        CreatePartition(node2, node1);
    }

    /// <summary>
    /// Removes a network partition between two nodes (unidirectional).
    /// </summary>
    public void HealPartition(string sourceAddress, string targetAddress)
    {
        lock (_partitionLock)
        {
            if (_partitions.TryGetValue(sourceAddress, out var blocked))
            {
                blocked.Remove(targetAddress);
            }
        }
    }

    /// <summary>
    /// Removes a bidirectional network partition between two nodes.
    /// </summary>
    public void HealBidirectionalPartition(string node1, string node2)
    {
        HealPartition(node1, node2);
        HealPartition(node2, node1);
    }

    /// <summary>
    /// Removes all network partitions.
    /// </summary>
    public void HealAllPartitions()
    {
        lock (_partitionLock)
        {
            _partitions.Clear();
        }
    }

    /// <summary>
    /// Isolates a node from all other nodes (bidirectional).
    /// </summary>
    public void IsolateNode(string nodeAddress)
    {
        foreach (var node in _environment.Nodes)
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
        foreach (var node in _environment.Nodes)
        {
            var addr = RapidUtils.Loggable(node.Address);
            if (addr != nodeAddress)
            {
                HealBidirectionalPartition(nodeAddress, addr);
            }
        }
    }

    /// <summary>
    /// Checks if a message can be delivered from source to target.
    /// </summary>
    internal bool CanDeliver(string sourceAddress, string targetAddress)
    {
        // Check for message drop
        if (MessageDropRate > 0 && _environment.Random.Chance(MessageDropRate))
        {
            return false;
        }

        // Check for network partition
        lock (_partitionLock)
        {
            if (_partitions.TryGetValue(sourceAddress, out var blocked) && blocked.Contains(targetAddress))
            {
                return false;
            }
        }

        return true;
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

        var jitter = _environment.Random.NextTimeSpan(MaxJitter);
        return BaseMessageDelay + jitter;
    }
}
