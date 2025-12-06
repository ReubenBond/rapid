# Rapid.NET

[![License](https://img.shields.io/badge/License-Apache%202.0-blue.svg)](https://opensource.org/licenses/Apache-2.0)

## What is Rapid?

Rapid is a distributed membership service ported to .NET 10 from the original Java implementation. It allows a set of processes to easily form clusters and receive notifications when the membership changes.

## Key Features

- **Expander-based monitoring** edge overlay for scalable failure detection
- **Multi-process cut detection** for stability in diverse failure scenarios
- **Practical consensus** using a leaderless Fast Paxos protocol
- **Pluggable failure detectors** via `IEdgeFailureDetectorFactory`
- **Pluggable messaging** via `IMessagingClient` and `IMessagingServer`
- **gRPC-based** communication with Protocol Buffers

## Quick Start

### Installation

Add the Rapid.Core package to your project:

```bash
dotnet add package Rapid.Core
```

### Basic Usage

```csharp
using Rapid;
using Rapid.Pb;

// Start a seed node
var seedAddress = Utils.HostFromParts("127.0.0.1", 1234);
var cluster = await new ClusterBuilder(seedAddress)
    .StartAsync();

// Join from another node
var joinerAddress = Utils.HostFromParts("127.0.0.1", 1235);
var cluster2 = await new ClusterBuilder(joinerAddress)
    .JoinAsync(seedAddress);

// Subscribe to membership changes
cluster.RegisterSubscription(ClusterEvents.ViewChange, change =>
{
    Console.WriteLine($"Cluster changed: {change.ConfigurationId}");
    Console.WriteLine($"Current members: {change.Membership.Count}");
});

// Get current membership
var members = cluster.GetMemberlist();
Console.WriteLine($"Cluster size: {cluster.GetMembershipSize()}");

// Gracefully leave
await cluster.LeaveGracefullyAsync();
```

## Architecture

### Core Components

- **Cluster**: Main API for creating and managing cluster membership
- **MembershipService**: Implements the Rapid protocol
- **MembershipView**: Manages K-ring topology for monitoring relationships
- **MultiNodeCutDetector**: Implements the cut detection algorithm
- **FastPaxos**: Consensus protocol implementation

### Messaging Layer

- **IMessagingClient**: Interface for sending messages
- **IMessagingServer**: Interface for receiving messages
- **GrpcClient/GrpcServer**: gRPC implementation (default)

### Monitoring

- **IEdgeFailureDetectorFactory**: Create custom failure detectors
- **PingPongFailureDetector**: Simple probe-based detector (default)

## Configuration

```csharp
var settings = new Settings
{
    GrpcTimeoutMs = 1000,
    FailureDetectorIntervalMs = 1000,
    BatchingWindowMs = 100,
    ConsensusFallbackTimeoutBaseDelayMs = 500
};

var cluster = await new ClusterBuilder(listenAddress)
    .UseSettings(settings)
    .StartAsync();
```

## Differences from Java Version

1. **Async/await**: All I/O operations use async patterns
2. **Task-based**: Uses `Task<T>` instead of `ListenableFuture<T>`
3. **ILogger**: Uses Microsoft.Extensions.Logging
4. **Channels**: Uses `System.Threading.Channels` for message queuing
5. **Nullable reference types**: Enabled for better null safety

## Building from Source

```bash
git clone https://github.com/yourusername/rapid-dotnet.git
cd rapid-dotnet/Rapid.Net
dotnet restore
dotnet build
dotnet test
```

## Running Examples

```bash
# Terminal 1 - Start seed node
dotnet run --project Rapid.Examples -- --listen 127.0.0.1:1234 --seed 127.0.0.1:1234

# Terminal 2 - Join cluster  
dotnet run --project Rapid.Examples -- --listen 127.0.0.1:1235 --seed 127.0.0.1:1234

# Terminal 3 - Another node
dotnet run --project Rapid.Examples -- --listen 127.0.0.1:1236 --seed 127.0.0.1:1234
```

## Documentation

For more details, see:
- [Original Rapid Paper (USENIX ATC 2018)](https://www.usenix.org/conference/atc18/presentation/suresh)
- [API Documentation](docs/api.md)
- [Architecture Guide](docs/architecture.md)

## License

Copyright © 2016-2025 VMware, Inc. All Rights Reserved.

Licensed under the Apache License, Version 2.0. See LICENSE file for details.

## Acknowledgments

This is a C# port of the original Java implementation by Lalith Suresh and team at VMware.

Original repository: https://github.com/lalithsuresh/rapid
