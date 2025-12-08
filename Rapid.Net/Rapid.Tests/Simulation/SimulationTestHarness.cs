using Microsoft.Extensions.Logging;

namespace Rapid.Tests.Simulation;

/// <summary>
/// Test harness for deterministic simulation testing of Rapid clusters.
/// Provides a controlled environment without WebApplication or gRPC dependencies.
/// </summary>
/// <remarks>
/// Creates a new simulation test harness with the specified seed.
/// </remarks>
/// <param name="seed">The seed for deterministic random number generation.</param>
/// <param name="loggerFactory">Optional logger factory for logging simulation events.</param>
/// <param name="useFakeTime">Whether to use fake time provider for deterministic time control. Default is false.</param>
/// <param name="taskScheduler">Optional task scheduler for deterministic task execution.</param>
internal sealed class SimulationTestHarness(int seed, ILoggerFactory? loggerFactory = null, bool useFakeTime = false, TaskScheduler? taskScheduler = null) : IAsyncDisposable
{
    private readonly List<SimulationNode> _nodes = [];

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
    public SimulationEnvironment Environment { get; } = new SimulationEnvironment(seed, loggerFactory, useFakeTime, taskScheduler);

    /// <summary>
    /// Gets the deterministic random number generator.
    /// </summary>
    public DeterministicRandom Random => Environment.Random;

    /// <summary>
    /// Gets the simulation time provider. Only available when useFakeTime is true.
    /// Provides access to pending timer information and precise time control.
    /// </summary>
    public SimulationTimeProvider? TimeProvider => Environment.SimulationTimeProvider;

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
    /// When using fake time, automatically advances time during the join process
    /// to ensure message timeouts work correctly.
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
        
        // Use RunWithTimeAdvancementAsync to ensure timeouts fire if a node is unreachable
        await RunWithTimeAdvancementAsync(
            () => node.JoinClusterAsync(seedNode, cancellationToken: cancellationToken),
            cancellationToken: cancellationToken).ConfigureAwait(true);
        
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

        // Create joiner nodes with a small delay between each to allow
        // membership views to propagate through the cluster
        for (var i = 1; i < size; i++)
        {
            var joiner = await CreateJoinerNodeAsync(seedNode, i, options, cancellationToken).ConfigureAwait(true);
            result.Add(joiner);
            
            // Wait for the cluster to stabilize before adding next node
            // This gives time for consensus messages to propagate
            if (i < size - 1)
            {
                if (Environment.UseFakeTime)
                {
                    // With fake time, just advance the time
                    Environment.AdvanceTime(TimeSpan.FromMilliseconds(50));
                }
                else
                {
                    await Task.Delay(50, cancellationToken).ConfigureAwait(true);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Advances simulation time by the specified duration.
    /// Only works when useFakeTime is enabled.
    /// </summary>
    public void AdvanceTime(TimeSpan duration) => Environment.AdvanceTime(duration);

    /// <summary>
    /// Runs a task while advancing fake time in the background.
    /// When using real time, simply runs the task directly.
    /// This is useful for operations that depend on timeouts (like joins to unreachable nodes).
    /// </summary>
    /// <param name="taskFactory">Factory that creates the task to run.</param>
    /// <param name="stepSize">How much fake time to advance per iteration. Default is 100ms.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<T> RunWithTimeAdvancementAsync<T>(
        Func<Task<T>> taskFactory,
        TimeSpan? stepSize = null,
        CancellationToken cancellationToken = default)
    {
        if (!Environment.UseFakeTime)
        {
            return await taskFactory().ConfigureAwait(true);
        }

        var step = stepSize ?? TimeSpan.FromMilliseconds(100);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Start the time advancement loop in background
        var timeAdvancementTask = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                // Small real delay to allow other tasks to run
                await Task.Delay(10, CancellationToken.None).ConfigureAwait(true);
                Environment.AdvanceTime(step);
            }
        }, CancellationToken.None);

        try
        {
            return await taskFactory().ConfigureAwait(true);
        }
        finally
        {
            await cts.CancelAsync().ConfigureAwait(true);
            try
            {
                await timeAdvancementTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(true);
            }
            catch (TimeoutException) { }
            catch (OperationCanceledException) { }
        }
    }

    /// <summary>
    /// Runs a task while advancing fake time in the background.
    /// When using real time, simply runs the task directly.
    /// </summary>
    public async Task RunWithTimeAdvancementAsync(
        Func<Task> taskFactory,
        TimeSpan? stepSize = null,
        CancellationToken cancellationToken = default)
    {
        await RunWithTimeAdvancementAsync(async () =>
        {
            await taskFactory().ConfigureAwait(true);
            return 0;
        }, stepSize, cancellationToken).ConfigureAwait(true);
    }

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
                // Give async continuations a chance to run after firing timers
                await Task.Yield();
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
                // Give async continuations a chance to run after firing timers
                await Task.Yield();
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
    /// The node is fully disposed, so messages to it will fail with "Target node not found".
    /// </summary>
    public void CrashNode(SimulationNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        node.Shutdown();
        node.Dispose(); // Unregister from environment so messages fail
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
        node.Dispose(); // Unregister from environment so messages fail
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
