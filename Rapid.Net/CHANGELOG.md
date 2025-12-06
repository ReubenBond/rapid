# Changelog

All notable changes to Rapid.NET will be documented in this file.

## [Unreleased]

### Critical TODOs
- Remove copyright headers from source files
- Remove default CancellationToken parameters (breaking change)
- Migrate Settings to IOptions pattern (breaking change)
- Integrate System.TimeProvider for testing

## [1.0.0-beta.1] - 2025-12-06

### Added
- Complete C# port of Rapid distributed membership protocol
- ASP.NET Core integration with BackgroundService pattern
- gRPC messaging layer with Protocol Buffers
- PingPong failure detector implementation
- Fast Paxos consensus protocol
- IRapidCluster interface for dependency injection
- RapidOptions configuration pattern
- Extension methods: AddRapid(), ConfigureRapidKestrel(), MapRapidMembershipService()
- Comprehensive XML documentation (>90% public API coverage)
- Multi-platform CI/CD pipeline (Ubuntu, Windows, macOS)
- NuGet package configuration
- 41 automated tests (34 unit, 8 integration)

### Architecture Changes
- Migrated from self-hosted WebApplication to ASP.NET Core hosting
- Removed deprecated Cluster.ClusterBuilder API
- Removed standalone GrpcServer class
- Added RapidClusterService BackgroundService
- Introduced IRapidCluster for testability

### Documentation
- Complete README with quick start guide
- Development guide for contributors
- Architecture documentation
- API reference (XML docs)

### Testing
- 34/34 unit tests passing (100%)
- 6/8 integration tests passing (75%)
- Known issues documented for 2-node graceful leave and high-concurrency joins

### Performance
- Clean build: 0 errors, 0 warnings
- Fast unit tests: ~5 seconds for 34 tests
- Integration tests: ~60 seconds for 8 tests

## [Initial Port] - 2024

### Java to C# Translation
- Ported MembershipView (K-ring topology)
- Ported MultiNodeCutDetector (failure detection)
- Ported Paxos and FastPaxos (consensus)
- Ported MembershipService (core protocol)
- Converted ListenableFuture to Task-based patterns
- Migrated from SLF4J to ILogger
- Replaced ExecutorService with Channels and Tasks

### Initial Features
- Basic cluster formation
- Join protocol
- Leave protocol
- View change events
- Metadata propagation
- Failure detection framework

---

## Version History

### Versioning Scheme
- **Major.Minor.Patch-PreRelease**
- Major: Breaking API changes
- Minor: New features, backward compatible
- Patch: Bug fixes
- PreRelease: alpha, beta, rc

### Planned Releases

**v1.0.0** - Stable Release
- All critical TODOs completed
- 100% integration test pass (stretch goal)
- Production deployment guide
- Performance benchmarks
- 24-hour stability test

**v1.1.0** - Enhancements
- TestServer-based service tests
- Health check integration
- Metrics/telemetry support
- Additional examples
- Performance optimizations

**v1.2.0** - Advanced Features
- HostApplicationBuilder support (console apps)
- Keyed services for multiple clusters
- Custom gRPC interceptors
- Enhanced structured logging

---

**Note**: This project follows [Semantic Versioning](https://semver.org/).
