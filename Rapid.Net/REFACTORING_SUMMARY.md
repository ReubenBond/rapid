# Rapid.NET Architecture Refactoring Summary

## Overview

The Rapid.NET library has been completely refactored to follow modern ASP.NET Core hosting patterns using Microsoft.Extensions.* libraries for dependency injection, options, and hosting. The architecture now integrates seamlessly with existing applications rather than managing its own WebApplication lifecycle.

## Key Changes

### 1. Removed Legacy API

**Deleted Classes:**
- `Cluster` and `Cluster.ClusterBuilder` - Legacy API for creating clusters
- `GrpcServer` - Standalone server that managed its own WebApplication
- `IMessagingServer` - Interface for server lifecycle management
- `ClusterHost` - Internal host wrapper

These classes were removed entirely since the project hasn't been publicly released yet.

### 2. New Hosting Integration

**New Core Classes:**

- **`RapidClusterService`** (BackgroundService)
  - Manages cluster lifecycle as a hosted service
  - Handles cluster startup (seed or join)
  - Runs in the background as part of the application host
  
- **`RapidOptions`** 
  - Configuration class for Rapid settings
  - Integrated with `IOptions<T>` pattern
  - Supports metadata, subscriptions, and cluster addresses

- **`IRapidCluster`** / **`RapidCluster`**
  - Public interface for accessing cluster state
  - Injectable via DI
  - Provides membership info and event subscription

- **`MembershipServiceImpl`**
  - gRPC service implementation (extracted from GrpcServer)
  - Registered as a singleton service

### 3. Extension Methods

**`RapidServiceCollectionExtensions`:**
```csharp
services.AddRapid(options => {
    options.ListenAddress = ...;
    options.SeedAddress = ...;
});
```

**`RapidHostingExtensions`:**
```csharp
builder.ConfigureRapidKestrel(port);
app.MapRapidMembershipService();
```

## Usage Pattern

### Old API (Removed)
```csharp
var cluster = await new Cluster.ClusterBuilder(listenAddress)
    .UseLoggerFactory(loggerFactory)
    .StartAsync(); // or JoinAsync(seed)

cluster.RegisterSubscription(ClusterEvents.ViewChange, handler);
await cluster.LeaveGracefullyAsync();
```

### New API (Current)
```csharp
var builder = WebApplication.CreateBuilder(args);

// Configure Kestrel for gRPC
builder.ConfigureRapidKestrel(1234);

// Add Rapid services
builder.Services.AddRapid(options =>
{
    options.ListenAddress = RapidUtils.HostFromParts("127.0.0.1", 1234);
    options.SeedAddress = RapidUtils.HostFromParts("127.0.0.1", 1234);
    
    // Optional: Event subscriptions
    options.AddSubscription(ClusterEvents.ViewChange, change => {
        // Handle event
    });
});

var app = builder.Build();

// Map Rapid gRPC endpoints
app.MapRapidMembershipService();

await app.RunAsync();
```

### Accessing Cluster from Services
```csharp
public class MyService : BackgroundService
{
    private readonly IRapidCluster _cluster;

    public MyService(IRapidCluster cluster)
    {
        _cluster = cluster;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var size = _cluster.GetMembershipSize();
        await _cluster.LeaveGracefullyAsync();
    }
}
```

## Architecture Benefits

1. **Idiomatic ASP.NET Core**: Follows Microsoft's recommended patterns
2. **Better Testability**: Easy to mock `IRapidCluster` for unit testing
3. **Lifecycle Management**: Integrated with ASP.NET Core host lifecycle
4. **Dependency Injection**: Full DI support throughout the stack
5. **Options Pattern**: Configuration via `IOptions<RapidOptions>`
6. **No Manual Host Management**: Application controls the host, not Rapid
7. **Multi-Service Integration**: Can coexist with other hosted services and HTTP endpoints

## Files Changed

### Added:
- `RapidClusterService.cs` - BackgroundService implementation
- `RapidOptions.cs` - Configuration options class
- `IRapidCluster.cs` - Public interface and implementation
- `RapidServiceCollectionExtensions.cs` - DI registration
- `RapidHostingExtensions.cs` - ASP.NET Core integration
- `MembershipServiceImpl.cs` - gRPC service (extracted)
- `IMembershipServiceHandler.cs` - Handler interface (extracted)
- `MIGRATION_GUIDE.md` - User migration documentation

### Removed:
- `Cluster.cs` - Legacy cluster API
- `GrpcServer.cs` - Standalone server
- `IMessagingServer.cs` - Server interface
- `ClusterHost.cs` - Internal wrapper
- `RapidEndpointRouteBuilderExtensions.cs` - Obsolete extensions
- `LeaveProtocolDebugTest.cs` - Test using old API

### Modified:
- `ClusterIntegrationTests.cs` - Updated to use new API
- `Program.cs` (Examples) - Modernized to use WebApplication pattern

## Implementation Details

### Service Registration Flow

1. `AddRapid()` registers:
   - `RapidOptions` via `IOptions<T>`
   - `SharedResources` (singleton)
   - `IMessagingClient` (GrpcClient)
   - `IEdgeFailureDetectorFactory`
   - `RapidClusterService` as hosted service
   - `IMembershipServiceHandler` (lazy, from cluster service)
   - `MembershipServiceImpl` (gRPC service)
   - `IRapidCluster` interface

2. On startup, `RapidClusterService.ExecuteAsync()`:
   - Reads configuration from `RapidOptions`
   - Creates `MembershipService`
   - Either starts new cluster or joins existing one
   - Runs indefinitely until cancellation

3. gRPC requests are handled by:
   - ASP.NET Core Kestrel server
   - `MembershipServiceImpl` (mapped via `MapRapidMembershipService()`)
   - Delegates to `IMembershipServiceHandler` (MembershipService)

### Dependency Graph

```
WebApplication (managed by user)
  └─> Kestrel (HTTP/2 on configured port)
       └─> MembershipServiceImpl (gRPC service)
            └─> IMembershipServiceHandler
                 └─> MembershipService (created by RapidClusterService)
  
  └─> IHostedService (managed by ASP.NET Core)
       └─> RapidClusterService
            └─> MembershipService (lifecycle owner)
  
  └─> IRapidCluster (DI injectable)
       └─> RapidCluster (wraps RapidClusterService.MembershipService)
```

## Testing

All integration tests have been updated to use the new API pattern. Tests now:
- Create `WebApplication` instances
- Use `IRapidCluster` interface
- Properly manage application lifecycle
- Use `TestContext.Current.CancellationToken` for responsive cancellation

## Future Enhancements

Possible improvements for future versions:
1. Support for `HostApplicationBuilder` (console apps without Kestrel)
2. Health check integration
3. Metrics/telemetry via `IMetrics` 
4. Configuration from `appsettings.json`
5. Keyed services for multiple clusters in one app
