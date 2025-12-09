using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Rapid.Tests.Simulation;

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
    private readonly Lock _partitionLock = new();
    private ILogger<SimulationNetwork> _logger;

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

    /// <summary>
    /// Gets or sets the default timeout for message delivery.
    /// This is applied to all nodes created after setting this value.
    /// Default is 30 seconds.
    /// </summary>
    public TimeSpan DefaultMessageTimeout { get; set; } = TimeSpan.FromSeconds(30);

    internal SimulationNetwork(SimulationHarness harness, SimulationRandom random)
    {
        _harness = harness;
        _random = random;
        _logger = NullLogger<SimulationNetwork>.Instance;
    }

    internal void SetLogger(ILogger<SimulationNetwork> logger) => _logger = logger;

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
            _logger.LogInformation("Created partition: {Source} -> {Target}", sourceAddress, targetAddress);
        }
    }

    /// <summary>
    /// Creates a bidirectional network partition between two nodes.
    /// </summary>
    public void CreateBidirectionalPartition(string node1, string node2)
    {
        _logger.LogInformation("Creating bidirectional partition between {Node1} and {Node2}", node1, node2);
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
                _logger.LogInformation("Healed partition: {Source} -> {Target}", sourceAddress, targetAddress);
            }
        }
    }

    /// <summary>
    /// Removes a bidirectional network partition between two nodes.
    /// </summary>
    public void HealBidirectionalPartition(string node1, string node2)
    {
        _logger.LogInformation("Healing bidirectional partition between {Node1} and {Node2}", node1, node2);
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
            var count = _partitions.Count;
            _partitions.Clear();
            _logger.LogInformation("Healed all {Count} partitions", count);
        }
    }

    /// <summary>
    /// Isolates a node from all other nodes (bidirectional).
    /// </summary>
    public void IsolateNode(string nodeAddress)
    {
        _logger.LogInformation("Isolating node {Node}", nodeAddress);
        foreach (var node in _harness.Nodes)
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
        _logger.LogInformation("Reconnecting isolated node {Node}", nodeAddress);
        foreach (var node in _harness.Nodes)
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
    /// Returns a status indicating success or reason for failure.
    /// </summary>
    internal DeliveryStatus CheckDelivery(string sourceAddress, string targetAddress)
    {
        // Check for network partition first (persistent)
        lock (_partitionLock)
        {
            if (_partitions.TryGetValue(sourceAddress, out var blocked) && blocked.Contains(targetAddress))
            {
                _logger.LogTrace("Message from {Source} to {Target} blocked (partition)", sourceAddress, targetAddress);
                return DeliveryStatus.Partitioned;
            }
        }

        // Check for random message drop (transient)
        if (MessageDropRate > 0 && _random.Chance(MessageDropRate))
        {
            _logger.LogTrace("Message from {Source} to {Target} dropped (random)", sourceAddress, targetAddress);
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
