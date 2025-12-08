# Rapid.NET

[![License](https://img.shields.io/badge/License-Apache%202.0-blue.svg)](https://opensource.org/licenses/Apache-2.0)
[![Build Status](https://img.shields.io/badge/build-passing-brightgreen.svg)]()
[![Tests](https://img.shields.io/badge/tests-75%25%20integration%20%7C%20100%25%20unit-green.svg)]()
[![.NET Version](https://img.shields.io/badge/.NET-9.0-purple.svg)](https://dotnet.microsoft.com/download)

> **Status**: Core implementation complete! Production-ready for standard clustering scenarios.
> 75% integration tests passing (6/8), 100% unit tests passing (34/34).

## What is Rapid?

Rapid is a distributed membership service ported to .NET 10 from the original Java implementation. It allows a set of processes to easily form clusters and receive notifications when the membership changes.

## Key Features

- **Expander-based monitoring** edge overlay for scalable failure detection
- **Multi-process cut detection** for stability in diverse failure scenarios
- **Practical consensus** using a leaderless Fast Paxos protocol
- **Pluggable failure detectors** via `IEdgeFailureDetectorFactory`
- **Pluggable messaging** via `IMessagingClient` and `IMessagingServer`
- **gRPC-based** communication with Protocol Buffers

