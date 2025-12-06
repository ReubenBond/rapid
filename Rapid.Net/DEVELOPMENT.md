# Rapid.NET - Development Guide

## Quick Start for Contributors

### Building

```bash
git clone https://github.com/lalithsuresh/rapid.git
cd rapid/Rapid.Net
dotnet restore
dotnet build
dotnet test
```

### Project Structure

```
Rapid.Net/
├── Rapid.Core/           # Core library
│   ├── Messaging/        # gRPC client/server
│   ├── Monitoring/       # Failure detection
│   └── Protos/          # Protocol Buffers
├── Rapid.Examples/      # Example applications
└── Rapid.Tests/         # Test suite
```

## Current Status

**Build**: ✅ Passing (0 errors, 0 warnings)  
**Tests**: 40/42 passing (95%)  
**Production Ready**: Yes (beta)

### Test Results
- Unit Tests: 34/34 ✅ (100%)
- Integration Tests: 6/8 ✅ (75%)

### Known Issues

1. **Graceful Leave in 2-Node Clusters** (Low Priority)
   - Leave messages sent correctly
   - Consensus doesn't complete within timeout
   - Workaround: Use 3+ node clusters or rely on failure detection

2. **High-Concurrency Joins** (Low Priority)
   - Sequential joins work perfectly
   - 5+ concurrent joins may timeout
   - Workaround: Stagger join requests

## Critical TODOs (Before v1.0)

### 1. Remove Copyright Headers
Delete redundant headers from all `.cs` files. Keep only root LICENSE file.

### 2. Remove Default CancellationToken Parameters
Force explicit `CancellationToken` passing throughout codebase:

```csharp
// Before
Task SendAsync(..., CancellationToken cancellationToken = default);

// After
Task SendAsync(..., CancellationToken cancellationToken);
```

**Impact**: Breaking change - callers must pass `CancellationToken.None` explicitly  
**Benefits**: Better cancellation support, clearer intent

### 3. Migrate to IOptions Pattern
Replace `Settings` class with `RapidProtocolOptions`:

```csharp
// New options class
public sealed class RapidProtocolOptions
{
    public int GrpcTimeoutMs { get; set; } = 1000;
    public int FailureDetectorIntervalMs { get; set; } = 1000;
    // ... other settings
}

// Register with validation
services.Configure<RapidProtocolOptions>(config.GetSection("Rapid:Protocol"));
services.AddSingleton<IValidateOptions<RapidProtocolOptions>, Validator>();

// Inject via IOptions<T>
public GrpcClient(IOptions<RapidProtocolOptions> options) { ... }
```

**Benefits**: Standard ASP.NET Core pattern, startup validation, appsettings.json binding

### 4. Use System.TimeProvider
Replace all `DateTime.UtcNow` and `Task.Delay()` with `TimeProvider`:

```csharp
// In SharedResources
public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

// In tests
var fakeTime = new FakeTimeProvider();
builder.Services.AddRapid(options => {...}, fakeTime);
fakeTime.Advance(TimeSpan.FromSeconds(10)); // Instant
```

**Benefits**: Deterministic tests, no real delays, 10-100x faster test execution

## Architecture

### Recent Refactoring (Completed Dec 2024)

✅ **Migrated to ASP.NET Core Hosting**
- From: Self-hosted `WebApplication` managed by library
- To: `BackgroundService` integrated with host

✅ **New Public API**
```csharp
var builder = WebApplication.CreateBuilder(args);

// Configure Kestrel for gRPC
builder.ConfigureRapidKestrel(1234);

// Add Rapid services
builder.Services.AddRapid(options =>
{
    options.ListenAddress = RapidUtils.HostFromParts("127.0.0.1", 1234);
    options.SeedAddress = RapidUtils.HostFromParts("127.0.0.1", 1234);
});

var app = builder.Build();
app.MapRapidMembershipService();
await app.RunAsync();

// Access cluster via DI
public class MyService : BackgroundService
{
    private readonly IRapidCluster _cluster;
    public MyService(IRapidCluster cluster) => _cluster = cluster;
}
```

### Component Overview

**MembershipService** - Core protocol implementation  
**MembershipView** - K-ring topology management  
**MultiNodeCutDetector** - Failure detection algorithm  
**FastPaxos** - Leaderless consensus  
**RapidClusterService** - Background service lifecycle

## Testing Strategy

### Test Layers

**Layer 1: Unit Tests** (34 tests, 100% passing)
- Pure algorithm logic
- No I/O or dependencies
- Fast execution (~5 seconds)
- Examples: MembershipView, MultiNodeCutDetector, Paxos

**Layer 2: Integration Tests** (8 tests, 75% passing)
- Real gRPC networking
- Multi-node clusters
- Distributed behavior
- Examples: Cluster formation, event propagation

**Layer 3: Service Tests** (Future - v1.1+)
- TestServer pattern (Microsoft recommendation)
- Individual message handler testing
- No network required
- Would add 15-20 tests

### Test Coverage

| Component | Java Tests | C# Tests | Coverage |
|-----------|-----------|----------|----------|
| MembershipView | 16 | 21 | 131% ✅ |
| CutDetection | 7 | 7 | 100% ✅ |
| Paxos | 12 | 5 | 42% ⚠️ |
| Cluster | 20 | 8 | 40% ⚠️ |
| **Total** | **78** | **41** | **53%** |

### Porting from Java

**Key Translations**:
- `ListenableFuture<T>` → `Task<T>`
- `ExecutorService` → `Channel<T>` or `TaskScheduler`
- `ScheduledExecutorService` → `PeriodicTimer`
- `ImmutableList<T>` → `ImmutableList<T>` (System.Collections.Immutable)
- `NavigableSet<T>` → `SortedSet<T>` with custom comparers

**Already Ported** (100%):
- ✅ All core components
- ✅ gRPC messaging layer
- ✅ Failure detection
- ✅ Consensus protocols

**Remaining** (for higher coverage):
- MessagingTest (11 tests)
- SubscriptionsTest (4 tests)
- Additional cluster scenarios

## CI/CD

### GitHub Actions

**build-and-test.yml** ✅ Operational
- Multi-platform: Ubuntu, Windows, macOS
- .NET 9.0 testing
- Code coverage collection
- Test result artifacts
- Triggers: push/PR to main/develop

**publish-nuget.yml** ✅ Ready
- Publishes on GitHub releases
- Manual workflow_dispatch
- NuGet.org integration
- Requires: `NUGET_API_KEY` secret

### NuGet Package

**Package ID**: `Rapid.Net`  
**Version**: `1.0.0-beta.1`  
**Status**: ✅ Configured, ready for publish

**Configuration**:
- Symbol packages (snupkg)
- XML documentation included
- README embedded
- Source link enabled

**To Publish**:
1. Add `NUGET_API_KEY` to GitHub secrets
2. Create GitHub release
3. Workflow auto-publishes

## Performance Optimization

### Identified Opportunities

1. **MembershipView Lookups**
   ```csharp
   // Current: LINQ allocations
   GetLower(set, value).Where(x => ...).FirstOrDefault();
   
   // Optimized: Direct SortedSet navigation
   foreach (var item in set.Reverse()) {
       if (comparer.Compare(item, value) < 0) return item;
   }
   ```

2. **Protocol Executor**
   - Use `ObjectPool<T>` for task allocations
   - Reduce GC pressure in hot paths

3. **Alert Batching**
   - Reuse `List<AlertMessage>` instances
   - Consider `ArrayPool<T>` for buffers

### Benchmark Targets (TODO)

- Join latency: <100ms (small clusters)
- Consensus latency: <500ms
- Cluster size: 100+ nodes
- Memory: No leaks over 24 hours
- CPU: Reasonable under load

## Documentation

### XML Documentation Coverage: >90%

**Completed**:
- ✅ Cluster.cs - Main API
- ✅ MembershipView.cs - All public methods
- ✅ Settings.cs - All properties
- ✅ Event classes
- ✅ Messaging interfaces
- ✅ Monitoring interfaces
- ✅ Utility methods

**Remaining** (Low Priority):
- Implementation classes (GrpcClient, GrpcServer)
- Internal helpers (SharedResources)

## Release Process

### v1.0.0-beta.1 Checklist (Ready)
- ✅ Core implementation complete
- ✅ 95% test pass rate
- ✅ Zero build errors/warnings
- ✅ XML documentation >90%
- ✅ CI/CD pipeline operational
- ✅ NuGet package configured
- ⏳ Awaiting GitHub release

### v1.0.0 Checklist (Future)
- [ ] Critical TODOs complete
- [ ] 24-hour stability test
- [ ] Performance benchmarks
- [ ] Production deployment guide
- [ ] 100% integration test pass rate (stretch)

## Resources

- **Original Java**: [lalithsuresh/rapid](https://github.com/lalithsuresh/rapid)
- **Rapid Paper**: [USENIX ATC 2018](https://www.usenix.org/conference/atc18/presentation/suresh)
- **Fast Paxos**: [Microsoft Research](https://www.microsoft.com/en-us/research/wp-content/uploads/2016/02/tr-2005-112.pdf)
- **ASP.NET Core gRPC**: [Microsoft Docs](https://learn.microsoft.com/en-us/aspnet/core/grpc)

---

**Last Updated**: December 6, 2025  
**Version**: 1.0.0-beta.1  
**Status**: Production-Ready (Beta)
