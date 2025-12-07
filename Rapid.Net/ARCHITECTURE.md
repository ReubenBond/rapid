# Rapid.NET Architecture

## Overview

Rapid.NET is a distributed membership service that allows processes to form clusters and receive notifications when membership changes. It uses an expander-based monitoring overlay and Fast Paxos consensus to handle failures efficiently.

## Core Components

### 1. MembershipView (Immutable)
**Purpose**: Public immutable snapshot of cluster membership  
**Key Responsibilities**:
- Expose current member list
- Provide configuration ID
- Support membership queries

**Public API**:
```csharp
public sealed class MembershipView
{
    public int K { get; }                           // Number of rings
    public long ConfigurationId { get; }            // Configuration version
    public IReadOnlyList<Endpoint> Members { get; } // Member list
    public int Size { get; }                        // Member count
    public bool IsMember(Endpoint endpoint);        // Membership check
    public MembershipViewConfiguration Configuration { get; } // Bootstrap config
}
```

### 2. MutableMembershipView (Internal)
**Purpose**: Internal mutable K-ring consistent hash topology  
**Key Responsibilities**:
- Add/remove nodes from K rings
- Track observer/subject relationships
- Maintain configuration IDs
- Detect UUID collisions
- Generate immutable snapshots

**Data Structures**:
```csharp
// K independent hash rings
List<SortedSet<Endpoint>> Rings { get; }

// Node identifier tracking
Dictionary<Endpoint, NodeId> IdentifiersSeen { get; }

// Configuration version
long CurrentConfigurationId { get; }

// Snapshot creation
MembershipView ToImmutableView();
```

### 3. MembershipService
**Purpose**: Core Rapid protocol implementation  
**Key Responsibilities**:
- Handle join/leave messages
- Batch alerts for efficiency
- Coordinate consensus via Paxos
- Trigger view change events
- Manage metadata
- Publish immutable views via `IAsyncEnumerable<MembershipView>`

**Public API for View Access**:
```csharp
// Get current snapshot
MembershipView GetCurrentView();

// Subscribe to subsequent view changes
IAsyncEnumerable<MembershipView> SubscribeToViewChangesAsync(CancellationToken ct);
```

**Message Flow**:
```
PreJoin → SafeToJoin check → JoinMessage → Consensus → ViewChange
Leave → EdgeFailure notification → Consensus → ViewChange
Probe → PingPong → EdgeFailure (on timeout) → Consensus → ViewChange
```

### 3. MultiNodeCutDetector
**Purpose**: Aggregate failure reports into proposals  
**Key Responsibilities**:
- Track reports by (reporter, subject) edge
- Apply H-threshold (high watermark)
- Apply L-threshold (low watermark)
- Generate cluster change proposals

**Algorithm**:
```
H = K/2 + 1  (proposal threshold)
L = K       (invalidation threshold)

For each subject node:
  If reports >= H: Propose removal
  If reports >= L: Invalidate edge
```

### 4. FastPaxos
**Purpose**: Leaderless consensus protocol  
**Key Responsibilities**:
- Coordinate on membership changes
- Fast round: Direct proposal acceptance
- Classic round: Paxos fallback if collision
- Value selection rules

**Phases**:
```
Fast Round:
  Phase 1: Implicit (no prepare needed)
  Phase 2: Any node proposes, quorum accepts
  
Classic Round (if fast round fails):
  Phase 1a: Prepare with round number
  Phase 1b: Promise and report highest accepted
  Phase 2a: Propose value
  Phase 2b: Accept value
```

### 5. RapidClusterService
**Purpose**: ASP.NET Core BackgroundService integration  
**Key Responsibilities**:
- Manage cluster lifecycle
- Bootstrap or join cluster
- Handle graceful shutdown
- Expose IRapidCluster interface

**Lifecycle**:
```
StartAsync() → Initialize → Bootstrap/Join → Running
StopAsync() → Leave (if configured) → Cleanup
```

### 6. IRapidCluster (Public API)
**Purpose**: Public interface for cluster interaction  
**Key Methods**:
```csharp
public interface IRapidCluster
{
    // Legacy API
    IReadOnlyList<Endpoint> GetMemberlist();
    int GetMembershipSize();
    void RegisterSubscription(ClusterEvents eventType, Action<ClusterStatusChange> callback);
    
    // New immutable view API
    MembershipView GetCurrentView();  // Get current snapshot
    IAsyncEnumerable<MembershipView> SubscribeToViewChangesAsync(CancellationToken ct);
    
    // Lifecycle
    Task LeaveGracefullyAsync();
}
```

## Messaging Layer

### IMessagingClient
Abstraction for sending messages to remote nodes.

**Default Implementation**: `GrpcClient`
- Connection pooling (1 channel per remote endpoint)
- Configurable timeouts and retries
- Automatic error handling

### IBroadcaster
Abstraction for broadcasting to multiple nodes.

**Default Implementation**: `UnicastToAllBroadcaster`
- Sends messages concurrently to all targets
- Collects successful responses
- Handles partial failures

### MembershipServiceImpl
gRPC service implementation that handles incoming protocol messages.

## Monitoring Layer

### IEdgeFailureDetector
Abstraction for detecting node failures.

**Default Implementation**: `PingPongFailureDetector`
- Periodic probing of monitored nodes
- Configurable probe interval
- Timeout-based failure detection
- Callback on failure

**Factory Pattern**:
```csharp
public interface IEdgeFailureDetectorFactory
{
    IEdgeFailureDetector CreateInstance(Endpoint subject);
}
```

## Data Flow

### Join Protocol

```
┌─────────────┐
│  New Node   │
└──────┬──────┘
       │ 1. PreJoinMessage
       v
┌─────────────┐
│  Seed Node  │
└──────┬──────┘
       │ 2. SafeToJoin (or error)
       v
┌─────────────┐
│  New Node   │
└──────┬──────┘
       │ 3. JoinMessage (with rings)
       v
┌─────────────┐
│  Observers  │
└──────┬──────┘
       │ 4. Consensus
       v
┌─────────────┐
│ ViewChange  │ → All nodes updated
└─────────────┘
```

### Failure Detection

```
┌─────────────┐
│  Monitor    │
└──────┬──────┘
       │ Periodic Probe
       v
┌─────────────┐
│  Subject    │ ─────X (timeout)
└─────────────┘
       │
       v
┌─────────────┐
│EdgeFailure  │
│Notification │
└──────┬──────┘
       │
       v
┌─────────────┐
│Alert Batcher│ (100ms window)
└──────┬──────┘
       │
       v
┌─────────────┐
│Cut Detector │ (H-threshold)
└──────┬──────┘
       │
       v
┌─────────────┐
│  Consensus  │ (FastPaxos)
└──────┬──────┘
       │
       v
┌─────────────┐
│ ViewChange  │ → Subject removed
└─────────────┘
```

## Configuration

### RapidOptions
User-facing configuration via `AddRapid()`:

```csharp
public class RapidOptions
{
    public Endpoint ListenAddress { get; set; }    // This node's address
    public Endpoint? SeedAddress { get; set; }     // Bootstrap seed (null = seed node)
    public Metadata? Metadata { get; set; }        // Node metadata
    public Dictionary<ClusterEvents, Action<ClusterStatusChange>> Subscriptions { get; set; }
}
```

### Settings (Internal)
Protocol-level configuration (will migrate to RapidProtocolOptions):

```csharp
public class Settings
{
    public int GrpcTimeoutMs { get; set; } = 1000;
    public int FailureDetectorIntervalMs { get; set; } = 1000;
    public int BatchingWindowMs { get; set; } = 100;
    // ... other protocol parameters
}
```

### SharedResources
Shared infrastructure for the cluster:

```csharp
public class SharedResources
{
    public Channel<Func<Task>> ProtocolExecutor { get; }  // Protocol message queue
    public CancellationToken ShutdownToken { get; }       // Shutdown coordination
    public ILoggerFactory LoggerFactory { get; }          // Logging
}
```

## Threading Model

### Async/Await Throughout
All I/O operations use async patterns:
- gRPC calls: `Task<RapidResponse> SendMessageAsync(...)`
- Consensus: `Task RunUntilPhase2B(...)`
- Background work: `BackgroundService.ExecuteAsync(...)`

### Channel-Based Message Processing
Protocol messages processed sequentially via `Channel<Func<Task>>`:

```csharp
// Enqueue protocol work
await _protocolExecutor.Writer.WriteAsync(async () => 
{
    await HandleJoinMessageAsync(message);
});

// Single background task processes sequentially
await foreach (var task in _protocolExecutor.Reader.ReadAllAsync())
{
    await task();
}
```

### Concurrency Characteristics
- **gRPC calls**: Concurrent by default (limited by connection pool)
- **Protocol messages**: Sequential (via channel)
- **Failure detectors**: Independent per-edge probing
- **Consensus**: Coordinated via message passing

## Event System

### ClusterEvents
Observable events:

```csharp
public enum ClusterEvents
{
    ViewChange,          // Final membership change
    ViewChangeProposal   // Proposed change (before consensus)
}
```

### Subscription Pattern

```csharp
builder.Services.AddRapid(options =>
{
    options.Subscriptions[ClusterEvents.ViewChange] = change =>
    {
        Console.WriteLine($"Cluster now has {change.Membership.Count} nodes");
        foreach (var member in change.Membership)
        {
            Console.WriteLine($"  - {member}");
        }
    };
});
```

## Extension Points

### Custom Failure Detector

```csharp
public class MyFailureDetector : IEdgeFailureDetector
{
    public void Start() { /* Custom monitoring logic */ }
    public void Stop() { /* Cleanup */ }
}

public class MyFactory : IEdgeFailureDetectorFactory
{
    public IEdgeFailureDetector CreateInstance(Endpoint subject)
    {
        return new MyFailureDetector(subject);
    }
}

// Register
builder.Services.AddSingleton<IEdgeFailureDetectorFactory, MyFactory>();
```

### Custom Messaging (Future)

Currently tightly coupled to gRPC, but interfaces allow future alternatives:
- In-process messaging for testing
- UDP-based messaging
- Custom serialization

## Design Decisions

### Why BackgroundService?
- Integrates with ASP.NET Core lifecycle
- Supports graceful shutdown
- Works with DI container
- Standard pattern for long-running tasks

### Why Channels?
- Type-safe message passing
- Back-pressure support
- Async enumeration
- Better than hand-rolled queues

### Why gRPC?
- Efficient binary protocol (Protobuf)
- HTTP/2 multiplexing
- Built-in connection management
- Cross-platform support

### Why Fast Paxos?
- Leaderless (no SPOF)
- Low latency (single round trip in common case)
- Proven correctness
- Handles network partitions

## Known Limitations

1. **2-Node Graceful Leave**
   - Monitoring relationships may not cover all pairs
   - Workaround: Use 3+ node clusters

2. **High-Concurrency Joins**
   - 5+ concurrent joins may timeout
   - Workaround: Stagger join requests

3. **Configuration Changes**
   - Requires restart (no hot reload)
   - Future: Support IOptionsMonitor

## Performance Characteristics

### Time Complexity
- Ring add/remove: O(K log N)
- Observer lookup: O(K log N)
- Cut detection: O(M) where M = number of reports

### Space Complexity
- Membership view: O(K × N)
- Cut detector: O(E) where E = edges reported

### Network Overhead
- Join: O(K) messages (one per observer)
- Failure: O(K) messages (one per monitor)
- Consensus: O(N) messages (broadcast to all)

## Future Architecture

### Planned Improvements

1. **IOptions Pattern**
   - Settings → RapidProtocolOptions
   - Validation at startup
   - appsettings.json binding

2. **TimeProvider Integration**
   - Deterministic testing
   - No real delays in tests
   - FakeTimeProvider support

3. **Health Checks**
   - ASP.NET Core health check integration
   - Cluster health reporting

4. **Metrics**
   - OpenTelemetry support
   - Membership gauges
   - Latency histograms

---

**Last Updated**: December 6, 2025  
**Version**: 1.0.0-beta.1
