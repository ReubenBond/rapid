# Rapid Java to C# Porting Guide

## Overview

This document describes the complete port of the Rapid distributed membership protocol from Java to C# targeting .NET 10.

## Project Structure

```
Rapid.Net/
├── Rapid.sln                          # Solution file
├── Rapid.Core/                        # Core library
│   ├── Rapid.Core.csproj             # Project file with gRPC/Protobuf support
│   ├── Protos/rapid.proto            # Protocol Buffer definitions
│   ├── Cluster.cs                    # Main API (was Cluster.java)
│   ├── ClusterEvents.cs              # Event types enum
│   ├── ClusterStatusChange.cs        # Status change data
│   ├── NodeStatusChange.cs           # Node-level change data
│   ├── Settings.cs                   # Configuration
│   ├── Utils.cs                      # Utility methods
│   ├── SharedResources.cs            # Shared thread/task resources
│   ├── MetadataManager.cs            # Metadata management
│   ├── Messaging/                    # Messaging layer
│   │   ├── IMessagingClient.cs
│   │   ├── IMessagingServer.cs
│   │   ├── IBroadcaster.cs
│   │   └── UnicastToAllBroadcaster.cs
│   └── Monitoring/                   # Failure detection
│       └── IEdgeFailureDetectorFactory.cs
├── Rapid.Examples/                   # Example applications
│   ├── Program.cs                    # StandaloneAgent equivalent
│   └── Rapid.Examples.csproj
└── Rapid.Tests/                      # Unit tests
    └── Rapid.Tests.csproj
```

## Key Porting Decisions

### 1. Async/Await Pattern

**Java:**
```java
ListenableFuture<RapidResponse> sendMessage(Endpoint remote, RapidRequest request);
```

**C#:**
```csharp
Task<RapidResponse> SendMessageAsync(Endpoint remote, RapidRequest request, 
    CancellationToken cancellationToken = default);
```

### 2. Threading Model

**Java (Guava):**
- `ExecutorService` with `ThreadPoolExecutor`
- `ScheduledExecutorService`
- `ListenableFuture` with callbacks

**C# (.NET 10):**
- `Task` and `Task<T>`
- `System.Threading.Channels` for message queuing
- `TaskScheduler` for scheduled tasks
- `async`/`await` throughout

### 3. Logging

**Java (SLF4J):**
```java
private static final Logger LOG = LoggerFactory.getLogger(Cluster.class);
LOG.info("Message");
```

**C# (Microsoft.Extensions.Logging):**
```csharp
private readonly ILogger<Cluster> _logger;
_logger.LogInformation("Message");
```

### 4. Collections

**Java → C# Mapping:**
- `List<T>` → `List<T>` or `IReadOnlyList<T>`
- `Set<T>` → `HashSet<T>` or `ISet<T>`
- `Map<K,V>` → `Dictionary<K,V>` or `IReadOnlyDictionary<K,V>`
- `NavigableSet<T>` → `SortedSet<T>` with custom comparers
- `ImmutableList<T>` → `ImmutableList<T>` (System.Collections.Immutable)

### 5. gRPC Integration

**Java (grpc-java):**
```xml
<dependency>
    <groupId>io.grpc</groupId>
    <artifactId>grpc-netty</artifactId>
</dependency>
```

**C# (Grpc.Net):**
```xml
<PackageReference Include="Grpc.Net.Client" Version="2.70.0" />
<PackageReference Include="Grpc.AspNetCore" Version="2.70.0" />
```

### 6. Protocol Buffers

**Proto file changes:**
```protobuf
option csharp_namespace = "Rapid.Pb";  // Added for C#
```

**Generated code:**
- Java: Multiple files (`JoinMessage.java`, `RapidRequest.java`, etc.)
- C#: Single file with nested classes

### 7. Null Safety

**Java:**
- `@Nullable` annotations
- Guava's `Optional<T>`

**C#:**
- Nullable reference types enabled (`<Nullable>enable</Nullable>`)
- `?` suffix for nullable types
- `!` null-forgiving operator where needed

### 8. Builder Pattern

**Java:**
```java
Cluster c = new Cluster.Builder(listenAddress)
    .setMetadata(metadata)
    .start();
```

**C#:**
```csharp
var cluster = await new Cluster.ClusterBuilder(listenAddress)
    .SetMetadata(metadata)
    .StartAsync();
```

## Files Ported

### Core Classes (com.vrg.rapid → Rapid namespace)

| Java File | C# File | Status | Notes |
|-----------|---------|--------|-------|
| Cluster.java | Cluster.cs | ✅ Complete | Async methods, builder pattern |
| ClusterEvents.java | ClusterEvents.cs | ✅ Complete | Enum naming conventions |
| ClusterStatusChange.java | ClusterStatusChange.cs | ✅ Complete | Record-like class |
| NodeStatusChange.java | NodeStatusChange.cs | ✅ Complete | Immutable properties |
| Settings.java | Settings.cs | ✅ Complete | Property-based configuration |
| Utils.java | Utils.cs | ✅ Complete | Static utility class |
| SharedResources.java | SharedResources.cs | ✅ Complete | Uses Channels instead of executors |
| MetadataManager.java | MetadataManager.cs | ✅ Complete | Thread-safe with lock |
| MembershipService.java | MembershipService.cs | 🔄 Partial | Core protocol logic (needs full implementation) |
| MembershipView.java | MembershipView.cs | 🔄 Partial | Ring management (needs full implementation) |
| MultiNodeCutDetector.java | MultiNodeCutDetector.cs | 🔄 Partial | Cut detection algorithm |
| FastPaxos.java | FastPaxos.cs | ⏳ Pending | Consensus protocol |
| Paxos.java | Paxos.cs | ⏳ Pending | Classic Paxos fallback |
| UnicastToAllBroadcaster.java | UnicastToAllBroadcaster.cs | ✅ Complete | Simple broadcaster |

### Messaging (com.vrg.rapid.messaging → Rapid.Messaging)

| Java File | C# File | Status |
|-----------|---------|--------|
| IMessagingClient.java | IMessagingClient.cs | ✅ Complete |
| IMessagingServer.java | IMessagingServer.cs | ✅ Complete |
| IBroadcaster.java | IBroadcaster.cs | ✅ Complete |
| GrpcClient.java | GrpcClient.cs | ⏳ Pending |
| GrpcServer.java | GrpcServer.cs | ⏳ Pending |
| NettyClientServer.java | (Not ported) | ❌ N/A - Grpc.Net used instead |
| Retries.java | Retries.cs | ⏳ Pending |

### Monitoring (com.vrg.rapid.monitoring → Rapid.Monitoring)

| Java File | C# File | Status |
|-----------|---------|--------|
| IEdgeFailureDetectorFactory.java | IEdgeFailureDetectorFactory.cs | ✅ Complete |
| PingPongFailureDetector.java | PingPongFailureDetector.cs | ⏳ Pending |

### Examples (com.vrg.standalone → Rapid.Examples)

| Java File | C# File | Status |
|-----------|---------|--------|
| StandaloneAgent.java | Program.cs | ✅ Complete |
| AgentWithNettyMessaging.java | (Not needed) | ❌ Uses Grpc.Net by default |

## NuGet Dependencies

Equivalent dependencies from Maven to NuGet:

| Maven (Java) | NuGet (C#) | Purpose |
|--------------|------------|---------|
| io.grpc:grpc-netty | Grpc.Net.Client | gRPC client |
| io.grpc:grpc-protobuf | Google.Protobuf | Protocol Buffers |
| io.grpc:grpc-stub | Grpc.Tools | Code generation |
| com.google.guava | (Built-in) | Collections, utilities |
| org.slf4j:slf4j-log4j12 | Microsoft.Extensions.Logging | Logging abstraction |
| net.openhft:zero-allocation-hashing | System.IO.Hashing | Hash functions |
| junit | xUnit | Unit testing |

## Build and Test

### Java (Maven)
```bash
mvn clean install
mvn test
```

### C# (.NET)
```bash
dotnet restore
dotnet build
dotnet test
```

## API Comparison

### Starting a Cluster

**Java:**
```java
Cluster cluster = new Cluster.Builder(listenAddress).start();
```

**C#:**
```csharp
var cluster = await new Cluster.ClusterBuilder(listenAddress).StartAsync();
```

### Joining a Cluster

**Java:**
```java
Cluster cluster = new Cluster.Builder(listenAddress).join(seedAddress);
```

**C#:**
```csharp
var cluster = await new Cluster.ClusterBuilder(listenAddress).JoinAsync(seedAddress);
```

### Event Subscriptions

**Java:**
```java
cluster.registerSubscription(ClusterEvents.VIEW_CHANGE, 
    this::onViewChange);
```

**C#:**
```csharp
cluster.RegisterSubscription(ClusterEvents.ViewChange, 
    OnViewChange);
```

## Implementation Status

### ✅ Complete (Ready to Use)
- Project structure and build configuration
- Protocol Buffer definitions
- Core data models (ClusterEvents, NodeStatusChange, etc.)
- Basic Cluster API and builder
- Messaging interfaces
- Example application structure
- Documentation (README, this guide)

### 🔄 Partial (Needs Completion)
- MembershipService (protocol implementation)
- MembershipView (ring topology management)
- MultiNodeCutDetector (cut detection logic)
- GrpcClient/GrpcServer implementations

### ⏳ Pending (Not Started)
- FastPaxos consensus protocol
- Classic Paxos fallback
- PingPongFailureDetector implementation
- Retries logic
- Comprehensive unit tests
- Integration tests

## Next Steps for Full Implementation

1. **Implement MembershipView** - Complete ring topology management
2. **Implement MultiNodeCutDetector** - Port cut detection algorithm
3. **Implement GrpcClient/GrpcServer** - gRPC messaging layer
4. **Implement FastPaxos** - Consensus protocol
5. **Implement MembershipService** - Core protocol orchestration
6. **Add PingPongFailureDetector** - Default failure detector
7. **Port unit tests** - Convert JUnit tests to xUnit
8. **Add integration tests** - End-to-end testing
9. **Performance testing** - Benchmarks and optimization
10. **Documentation** - API docs, tutorials, examples

## Code Conventions

### Naming
- **PascalCase**: Public members, types, methods
- **camelCase**: Private fields (with `_` prefix), parameters, locals
- **UPPER_CASE**: Constants (but use PascalCase for const fields in C#)

### File Organization
- One class per file (C# convention)
- Namespace matches folder structure
- Internal classes for implementation details
- Public classes for API surface

## Testing

Port tests using these mappings:

**Java (JUnit):**
```java
@Test
public void testSomething() {
    assertEquals(expected, actual);
}
```

**C# (xUnit):**
```csharp
[Fact]
public void TestSomething()
{
    Assert.Equal(expected, actual);
}
```

## Running the Example

```bash
# Build
cd Rapid.Net
dotnet build

# Run seed node
dotnet run --project Rapid.Examples -- --listen 127.0.0.1:1234 --seed 127.0.0.1:1234

# Run joining node (in another terminal)
dotnet run --project Rapid.Examples -- --listen 127.0.0.1:1235 --seed 127.0.0.1:1234
```

## License

Copyright © 2016-2025 VMware, Inc. All Rights Reserved.

Licensed under the Apache License, Version 2.0.
