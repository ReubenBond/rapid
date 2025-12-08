# Rapid.NET

## What is Rapid?

Rapid is a distributed membership service. It allows a set of processes to easily form clusters and receive notifications when the membership changes.

## Key Features

- **Expander-based monitoring** edge overlay for scalable failure detection
- **Multi-process cut detection** for stability in diverse failure scenarios
- **Practical consensus** using a leaderless Fast Paxos protocol
- **Pluggable failure detectors** via `IEdgeFailureDetectorFactory`
- **Pluggable messaging** via `IMessagingClient` and `IMessagingServer`
- **gRPC-based** communication with Protocol Buffers

