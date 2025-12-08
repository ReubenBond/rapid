using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Rapid.Tests.Simulation;

/// <summary>
/// Provides a controlled environment for deterministic simulation testing.
/// This includes a deterministic task scheduler, controllable time, and seeded random number generation.
/// </summary>
internal sealed class SimulationEnvironment : IDisposable
{
    private readonly ConcurrentDictionary<string, SimulationNode> _nodes = new();
    private readonly Lock _lock = new();
    private bool _disposed;

    /// <summary>
    /// Creates a new simulation environment with the specified seed.
    /// </summary>
    /// <param name="seed">The seed for deterministic random number generation.</param>
    /// <param name="loggerFactory">Optional logger factory for logging simulation events.</param>
    /// <param name="useFakeTime">Whether to use fake time provider. Default is false for basic tests.</param>
    /// <param name="taskScheduler">Optional task scheduler for deterministic task execution.</param>
    public SimulationEnvironment(int seed, ILoggerFactory? loggerFactory = null, bool useFakeTime = false, TaskScheduler? taskScheduler = null)
    {
        Seed = seed;
        Random = new DeterministicRandom(seed);
        LoggerFactory = loggerFactory;
        Network = new SimulationNetwork(this);
        UseFakeTime = useFakeTime;
        TaskScheduler = taskScheduler;

        if (useFakeTime)
        {
            var simTime = new SimulationTimeProvider(DateTimeOffset.UtcNow);
            TimeProvider = simTime;
            SimulationTimeProvider = simTime;
        }
        else
        {
            TimeProvider = TimeProvider.System;
            SimulationTimeProvider = null;
        }
    }

    /// <summary>
    /// Gets the deterministic random number generator.
    /// </summary>
    public DeterministicRandom Random { get; }

    /// <summary>
    /// Gets the time provider used by simulation nodes.
    /// </summary>
    public TimeProvider TimeProvider { get; }

    /// <summary>
    /// Gets the simulation time provider if useFakeTime was enabled, null otherwise.
    /// Provides access to pending timer information and precise time control.
    /// </summary>
    public SimulationTimeProvider? SimulationTimeProvider { get; }

    /// <summary>
    /// Gets whether this environment uses fake time.
    /// </summary>
    public bool UseFakeTime { get; }

    /// <summary>
    /// Gets the task scheduler used by simulation nodes, or null for default.
    /// </summary>
    public TaskScheduler? TaskScheduler { get; }

    /// <summary>
    /// Gets the logger factory used by simulation nodes.
    /// </summary>
    public ILoggerFactory? LoggerFactory { get; }

    /// <summary>
    /// Gets the simulated network for inter-node communication.
    /// </summary>
    public SimulationNetwork Network { get; }

    /// <summary>
    /// Gets the seed used to initialize this environment.
    /// </summary>
    public int Seed { get; }

    /// <summary>
    /// Gets all nodes currently in the simulation.
    /// </summary>
    public IReadOnlyCollection<SimulationNode> Nodes => _nodes.Values.ToList();

    /// <summary>
    /// Registers a node with the simulation environment.
    /// </summary>
    internal void RegisterNode(SimulationNode node)
    {
        var key = RapidUtils.Loggable(node.Address);
        if (!_nodes.TryAdd(key, node))
        {
            throw new InvalidOperationException($"Node with address {key} already exists");
        }
    }

    /// <summary>
    /// Unregisters a node from the simulation environment.
    /// </summary>
    internal void UnregisterNode(SimulationNode node)
    {
        var key = RapidUtils.Loggable(node.Address);
        _nodes.TryRemove(key, out _);
    }

    /// <summary>
    /// Gets a node by its address string.
    /// </summary>
    internal SimulationNode? GetNode(string address)
    {
        _nodes.TryGetValue(address, out var node);
        return node;
    }

    /// <summary>
    /// Advances the simulation time by the specified duration, firing any pending timers.
    /// Only works if useFakeTime was enabled.
    /// </summary>
    /// <param name="duration">The duration to advance time.</param>
    /// <returns>The number of timers that were fired.</returns>
    public int AdvanceTime(TimeSpan duration)
    {
        if (SimulationTimeProvider == null)
        {
            throw new InvalidOperationException("Cannot advance time when useFakeTime is false");
        }
        return SimulationTimeProvider.Advance(duration);
    }

    /// <summary>
    /// Advances the simulation time to a specific point, firing any pending timers.
    /// Only works if useFakeTime was enabled.
    /// </summary>
    /// <param name="targetTime">The target time to advance to.</param>
    /// <returns>The number of timers that were fired.</returns>
    public int AdvanceTimeTo(DateTimeOffset targetTime)
    {
        if (SimulationTimeProvider == null)
        {
            throw new InvalidOperationException("Cannot advance time when useFakeTime is false");
        }
        return SimulationTimeProvider.AdvanceTo(targetTime);
    }

    /// <summary>
    /// Advances time to fire the next pending timer.
    /// Only works if useFakeTime was enabled.
    /// </summary>
    /// <returns>True if a timer was fired, false if no timers were pending.</returns>
    public bool AdvanceToNextTimer()
    {
        if (SimulationTimeProvider == null)
        {
            throw new InvalidOperationException("Cannot advance time when useFakeTime is false");
        }
        return SimulationTimeProvider.AdvanceToNextTimer();
    }

    /// <summary>
    /// Gets information about all pending timers.
    /// Only available when useFakeTime is enabled.
    /// </summary>
    public IReadOnlyList<SimulationTimeProvider.TimerInfo> GetPendingTimers()
    {
        if (SimulationTimeProvider == null)
        {
            return [];
        }
        return SimulationTimeProvider.GetPendingTimers();
    }

    /// <summary>
    /// Creates a new deterministic random instance derived from the environment's random.
    /// Useful for giving each node its own independent random stream.
    /// </summary>
    public DeterministicRandom CreateDerivedRandom()
    {
        lock (_lock)
        {
            return new DeterministicRandom(Random.Next());
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var node in _nodes.Values)
        {
            node.Dispose();
        }
        _nodes.Clear();
    }
}
