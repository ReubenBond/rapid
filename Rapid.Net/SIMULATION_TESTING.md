# Simulation Testing Framework

This document describes the deterministic simulation testing framework for Rapid.NET, which allows testing cluster behavior without network I/O or WebApplication instances.

## Overview

The simulation testing framework provides:

- **In-memory transport** - No gRPC or network sockets
- **Deterministic random** - Seeded random for reproducible test runs
- **Controllable time** - Optional `FakeTimeProvider` integration
- **Network fault injection** - Partitions, message drops, delays
- **Lightweight nodes** - No ASP.NET Core hosting overhead

## Quick Start

```csharp
using Rapid.Tests.Simulation;

// Create a harness with a fixed seed for reproducibility
await using var harness = new SimulationTestHarness(seed: 12345);

// Create a seed node (starts a new single-node cluster)
var seedNode = harness.CreateSeedNode();

// Join another node to the cluster
var joiner = await harness.CreateJoinerNodeAsync(seedNode, nodeId: 1);

// Check membership
Assert.Equal(2, seedNode.MembershipSize);
Assert.Equal(2, joiner.MembershipSize);
```

## Core Components

### SimulationTestHarness

The main entry point for simulation tests. Manages nodes and provides helper methods.

```csharp
// Create with explicit seed
var harness = new SimulationTestHarness(seed: 42);

// Create with random seed (logged for reproduction)
var harness = SimulationTestHarness.CreateWithRandomSeed();

// Create with fake time enabled (for time-sensitive tests)
var harness = new SimulationTestHarness(seed: 42, useFakeTime: true);
```

**Key Properties:**
- `Seed` - The random seed used (save this to reproduce failures)
- `Random` - The deterministic random instance
- `Network` - Access to network fault injection
- `Nodes` - All nodes in the simulation
- `FakeTimeProvider` - The fake time provider (if enabled)

### SimulationNode

Represents a node in the simulated cluster.

```csharp
// Create and start a seed node
var seed = harness.CreateSeedNode(nodeId: 0);

// Create and join a node
var joiner = await harness.CreateJoinerNodeAsync(seed, nodeId: 1);

// Check node state
bool initialized = node.IsInitialized;
int size = node.MembershipSize;
MembershipView view = node.CurrentView;

// Subscribe to view changes
node.RegisterSubscription(ClusterEvents.ViewChange, change => {
    Console.WriteLine($"View changed: {change.Membership.Count} members");
});

// Leave the cluster gracefully
await node.LeaveAsync();
```

### SimulationNetwork

Controls network behavior between nodes.

```csharp
// Access via harness
var network = harness.Network;

// Configure delays
network.EnableDelays = true;
network.BaseMessageDelay = TimeSpan.FromMilliseconds(5);
network.MaxJitter = TimeSpan.FromMilliseconds(10);

// Configure message drops
network.MessageDropRate = 0.1; // 10% drop rate
```

### DeterministicRandom

Seeded random number generator for reproducible tests.

```csharp
var random = new DeterministicRandom(seed: 42);

// Standard random operations
int n = random.Next();
int bounded = random.Next(100);
double d = random.NextDouble();
bool b = random.NextBool();

// Helper methods
random.Shuffle(list);           // Shuffle in place
var item = random.Choose(list); // Pick random element
bool hit = random.Chance(0.5);  // 50% probability

// Time spans
var delay = random.NextTimeSpan(TimeSpan.FromSeconds(1));

// Create independent stream
var derived = random.Fork();
```

## Network Fault Injection

### Partitions

```csharp
// Partition between two specific nodes (bidirectional)
harness.PartitionNodes(node1, node2);

// Heal the partition
harness.HealPartition(node1, node2);

// Isolate a node from all others
harness.IsolateNode(node);

// Reconnect an isolated node
harness.ReconnectNode(node);

// Lower-level API via network
harness.Network.CreatePartition("node:0", "node:1");      // One-way
harness.Network.CreateBidirectionalPartition("node:0", "node:1");
harness.Network.HealAllPartitions();
```

### Message Loss

```csharp
// Set probability of random message drops
harness.Network.MessageDropRate = 0.05; // 5% loss
```

### Delays

```csharp
harness.Network.EnableDelays = true;
harness.Network.BaseMessageDelay = TimeSpan.FromMilliseconds(10);
harness.Network.MaxJitter = TimeSpan.FromMilliseconds(5);
// Each message delayed by 10-15ms
```

## Time Control

For tests that need deterministic time control, enable fake time:

```csharp
var harness = new SimulationTestHarness(seed: 42, useFakeTime: true);

// Get current simulated time
var now = harness.FakeTimeProvider!.GetUtcNow();

// Advance time
harness.AdvanceTime(TimeSpan.FromSeconds(5));

// Note: When using fake time, background tasks that use Task.Delay
// will not complete until time is advanced.
```

> **Warning**: Fake time mode requires careful handling. Background tasks in the Rapid protocol (alert batching, failure detection) use `Task.Delay` which blocks on fake time advancement. For most tests, use real time (the default).

## Waiting for Convergence

```csharp
// Wait for all nodes to see expected membership size
await harness.WaitForConvergenceAsync(
    expectedSize: 3,
    timeout: TimeSpan.FromSeconds(10));

// Wait for a specific node
await harness.WaitForNodeSizeAsync(
    node: seedNode,
    expectedSize: 5,
    timeout: TimeSpan.FromSeconds(10));
```

## Node Lifecycle

```csharp
// Simulate a crash (immediate shutdown)
harness.CrashNode(node);

// Graceful leave (notifies observers)
await harness.RemoveNodeGracefullyAsync(node);
```

## Example: Testing Network Partition Recovery

```csharp
[Fact]
public async Task ClusterRecoveryAfterPartitionHeal()
{
    await using var harness = new SimulationTestHarness(seed: 12345);

    // Create a 3-node cluster
    var seed = harness.CreateSeedNode(0);
    var node1 = await harness.CreateJoinerNodeAsync(seed, 1);
    var node2 = await harness.CreateJoinerNodeAsync(seed, 2);

    // Verify initial state
    Assert.Equal(3, seed.MembershipSize);

    // Partition node2 from the cluster
    harness.IsolateNode(node2);

    // ... trigger failure detection by advancing time or waiting ...

    // Heal the partition
    harness.ReconnectNode(node2);

    // Eventually cluster should recover
    // (depends on your test scenario)
}
```

## Reproducing Failures

When a test fails, note the seed:

```csharp
// In your test output
Console.WriteLine($"Test seed: {harness.Seed}");
```

Then reproduce with that exact seed:

```csharp
var harness = new SimulationTestHarness(seed: <failed_seed>);
```

## Architecture

```
┌─────────────────────────────────────────────────────────┐
│                  SimulationTestHarness                  │
│  - CreateSeedNode()                                     │
│  - CreateJoinerNodeAsync()                              │
│  - PartitionNodes() / HealPartition()                   │
└─────────────────────────────────────────────────────────┘
                            │
                            ▼
┌─────────────────────────────────────────────────────────┐
│                 SimulationEnvironment                   │
│  - DeterministicRandom                                  │
│  - TimeProvider (real or fake)                          │
│  - Node registry                                        │
└─────────────────────────────────────────────────────────┘
                            │
              ┌─────────────┼─────────────┐
              ▼             ▼             ▼
        ┌──────────┐  ┌──────────┐  ┌──────────┐
        │  Node 0  │  │  Node 1  │  │  Node 2  │
        │(SimNode) │  │(SimNode) │  │(SimNode) │
        └────┬─────┘  └────┬─────┘  └────┬─────┘
             │             │             │
             └─────────────┼─────────────┘
                           ▼
              ┌─────────────────────────┐
              │    SimulationNetwork    │
              │  - Message routing      │
              │  - Partition tracking   │
              │  - Delay simulation     │
              │  - Drop simulation      │
              └─────────────────────────┘
```

## Limitations

1. **Consensus delays**: Join operations require consensus, which involves alert batching delays. Tests involving multiple joins may take time even with zero batching window due to protocol round-trips.

2. **Fake time complexity**: When using fake time, you must manually advance time for any `Task.Delay` to complete. This includes internal protocol delays.

3. **No true deterministic scheduling**: While random numbers are deterministic, task scheduling is not fully controlled. For true deterministic simulation testing (like FoundationDB's), additional work is needed.

## Files

- `Rapid.Tests/Simulation/SimulationEnvironment.cs` - Core environment
- `Rapid.Tests/Simulation/SimulationTestHarness.cs` - Test harness API
- `Rapid.Tests/Simulation/SimulationNode.cs` - Simulated node
- `Rapid.Tests/Simulation/SimulationNetwork.cs` - Network simulation
- `Rapid.Tests/Simulation/InMemoryMessagingClient.cs` - In-memory transport
- `Rapid.Tests/Simulation/SimulationFailureDetector.cs` - Failure detector
- `Rapid.Tests/Simulation/DeterministicRandom.cs` - Seeded random

## Future Work

- [ ] Deterministic task scheduler for true simulation testing
- [ ] Event logging and replay
- [ ] Chaos testing helpers (random partitions, restarts)
- [ ] Performance profiling hooks
