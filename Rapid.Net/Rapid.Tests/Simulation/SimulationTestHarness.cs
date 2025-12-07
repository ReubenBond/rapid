using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Rapid.Tests.Simulation;

/// <summary>
/// Test harness for deterministic simulation testing of Rapid clusters.
/// Provides a controlled environment without WebApplication or gRPC dependencies.
/// </summary>
internal sealed class SimulationTestHarness : IAsyncDisposable
{
    private readonly List<SimulationNode> _nodes = [];

    /// <summary>
    /// Creates a new simulation test harness with the specified seed.
    /// </summary>
    /// <param name="seed">The seed for deterministic random number generation.</param>
    /// <param name="loggerFactory">Optional logger factory for logging simulation events.</param>
    /// <param name="useFakeTime">Whether to use fake time provider for deterministic time control. Default is false.</param>
    public SimulationTestHarness(int seed, ILoggerFactory? loggerFactory = null, bool useFakeTime = false)
    {
        Environment = new SimulationEnvironment(seed, loggerFactory, useFakeTime);
    }

    /// <summary>
    /// Creates a new simulation test harness with a random seed.
    /// The seed is printed to standard output for reproducibility.
    /// </summary>
    public static SimulationTestHarness CreateWithRandomSeed(ILoggerFactory? loggerFactory = null, bool useFakeTime = false)
    {
        var seed = System.Environment.TickCount;
        return new SimulationTestHarness(seed, loggerFactory, useFakeTime);
    }

    /// <summary>
    /// Gets the simulation environment.
    /// </summary>
    public SimulationEnvironment Environment { get; }

    /// <summary>
    /// Gets the deterministic random number generator.
    /// </summary>
    public DeterministicRandom Random => Environment.Random;

    /// <summary>
    /// Gets the controllable time provider. Only available when useFakeTime is true.
    /// </summary>
    public FakeTimeProvider? FakeTimeProvider => Environment.FakeTimeProvider;

    /// <summary>
    /// Gets the simulated network.
    /// </summary>
    public SimulationNetwork Network => Environment.Network;

    /// <summary>
    /// Gets all nodes in the simulation.
    /// </summary>
    public IReadOnlyList<SimulationNode> Nodes => _nodes;

    /// <summary>
    /// Gets the seed used to initialize this harness.
    /// </summary>
    public int Seed => Environment.Seed;

    /// <summary>
    /// Creates and starts a new seed node.
    /// </summary>
    public SimulationNode CreateSeedNode(
        int nodeId = 0,
        RapidProtocolOptions? options = null)
    {
        // Use zero batching window for immediate processing in simulation
        var opts = options ?? new RapidProtocolOptions();
        opts.BatchingWindow = TimeSpan.Zero;
        opts.FailureDetectorInterval = TimeSpan.FromSeconds(1);

        var node = SimulationNode.Create(Environment, nodeId, opts, Environment.LoggerFactory);
        node.StartCluster();
        _nodes.Add(node);
        return node;
    }

    /// <summary>
    /// Creates and joins a new node to the cluster through the specified seed.
    /// </summary>
    public async Task<SimulationNode> CreateJoinerNodeAsync(
        SimulationNode seedNode,
        int nodeId,
        RapidProtocolOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // Use zero batching window for immediate processing in simulation
        var opts = options ?? new RapidProtocolOptions();
        opts.BatchingWindow = TimeSpan.Zero;
        opts.FailureDetectorInterval = TimeSpan.FromSeconds(1);

        var node = SimulationNode.Create(Environment, nodeId, opts, Environment.LoggerFactory);
        await node.JoinClusterAsync(seedNode, cancellationToken: cancellationToken).ConfigureAwait(true);
        _nodes.Add(node);
        return node;
    }

    /// <summary>
    /// Creates a cluster of the specified size.
    /// Returns the seed node and all joiner nodes.
    /// </summary>
    public async Task<IReadOnlyList<SimulationNode>> CreateClusterAsync(
        int size,
        RapidProtocolOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (size < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "Cluster size must be at least 1");
        }

        var result = new List<SimulationNode>(size);

        // Create seed node
        var seedNode = CreateSeedNode(0, options);
        result.Add(seedNode);

        // Create joiner nodes
        for (var i = 1; i < size; i++)
        {
            var joiner = await CreateJoinerNodeAsync(seedNode, i, options, cancellationToken).ConfigureAwait(true);
            result.Add(joiner);
        }

        return result;
    }

    /// <summary>
    /// Advances simulation time by the specified duration.
    /// Only works when useFakeTime is enabled.
    /// </summary>
    public void AdvanceTime(TimeSpan duration) => Environment.AdvanceTime(duration);

    /// <summary>
    /// Waits for all nodes to converge to the same membership size.
    /// When using fake time, advances time as needed to allow convergence.
    /// When using real time, polls with delays.
    /// </summary>
    public async Task WaitForConvergenceAsync(
        int expectedSize,
        TimeSpan timeout,
        TimeSpan? stepSize = null,
        CancellationToken cancellationToken = default)
    {
        var step = stepSize ?? TimeSpan.FromMilliseconds(100);
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Check if all nodes have converged
            var allConverged = _nodes.All(n => n.MembershipSize == expectedSize);
            if (allConverged)
            {
                return;
            }

            // Advance time (for fake time) or delay (for real time)
            if (Environment.UseFakeTime)
            {
                Environment.AdvanceTime(step);
            }
            else
            {
                await Task.Delay(step, cancellationToken).ConfigureAwait(true);
            }
        }

        throw new TimeoutException($"Nodes did not converge to size {expectedSize} within {timeout}. " +
            $"Current sizes: [{string.Join(", ", _nodes.Select(n => n.MembershipSize))}]");
    }

    /// <summary>
    /// Waits for a specific node to reach the expected membership size.
    /// </summary>
    public async Task WaitForNodeSizeAsync(
        SimulationNode node,
        int expectedSize,
        TimeSpan timeout,
        TimeSpan? stepSize = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(node);

        var step = stepSize ?? TimeSpan.FromMilliseconds(100);
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (node.MembershipSize == expectedSize)
            {
                return;
            }

            if (Environment.UseFakeTime)
            {
                Environment.AdvanceTime(step);
            }
            else
            {
                await Task.Delay(step, cancellationToken).ConfigureAwait(true);
            }
        }

        throw new TimeoutException($"Node did not reach size {expectedSize} within {timeout}. " +
            $"Current size: {node.MembershipSize}");
    }

    /// <summary>
    /// Removes a node from the simulation (simulates crash).
    /// </summary>
    public void CrashNode(SimulationNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        node.Shutdown();
        _nodes.Remove(node);
    }

    /// <summary>
    /// Gracefully removes a node from the cluster.
    /// </summary>
    public async Task RemoveNodeGracefullyAsync(SimulationNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        await node.LeaveAsync().ConfigureAwait(true);
        node.Shutdown();
        _nodes.Remove(node);
    }

    /// <summary>
    /// Creates a network partition that isolates the specified node.
    /// </summary>
    public void IsolateNode(SimulationNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var addr = RapidUtils.Loggable(node.Address);
        Environment.Network.IsolateNode(addr);
    }

    /// <summary>
    /// Heals the network partition for the specified node.
    /// </summary>
    public void ReconnectNode(SimulationNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var addr = RapidUtils.Loggable(node.Address);
        Environment.Network.ReconnectNode(addr);
    }

    /// <summary>
    /// Creates a partition between two nodes.
    /// </summary>
    public void PartitionNodes(SimulationNode node1, SimulationNode node2)
    {
        ArgumentNullException.ThrowIfNull(node1);
        ArgumentNullException.ThrowIfNull(node2);
        var addr1 = RapidUtils.Loggable(node1.Address);
        var addr2 = RapidUtils.Loggable(node2.Address);
        Environment.Network.CreateBidirectionalPartition(addr1, addr2);
    }

    /// <summary>
    /// Heals a partition between two nodes.
    /// </summary>
    public void HealPartition(SimulationNode node1, SimulationNode node2)
    {
        ArgumentNullException.ThrowIfNull(node1);
        ArgumentNullException.ThrowIfNull(node2);
        var addr1 = RapidUtils.Loggable(node1.Address);
        var addr2 = RapidUtils.Loggable(node2.Address);
        Environment.Network.HealBidirectionalPartition(addr1, addr2);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var node in _nodes)
        {
            node.Shutdown();
            node.Dispose();
        }
        _nodes.Clear();
        Environment.Dispose();
        await Task.CompletedTask.ConfigureAwait(true);
    }
}
