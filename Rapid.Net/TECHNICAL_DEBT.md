# Rapid.NET Technical Debt and Future Improvements

## High Priority

### 1. Remove Default CancellationToken Parameters
**Status:** Not Started  
**Priority:** High  
**Impact:** Code Quality, Cancellation Responsiveness

**Description:**
Remove all `= default` from `CancellationToken` parameters throughout the codebase. Force callers to explicitly pass a CancellationToken.

**Current Issues:**
- Methods have `CancellationToken cancellationToken = default` which allows callers to omit it
- Leads to incomplete cancellation support
- Makes it unclear when cancellation is supported vs. ignored
- Testing cancellation scenarios is harder

**Proposed Solution:**
```csharp
// Before
Task<RapidResponse> SendMessageAsync(Endpoint remote, RapidRequest request,
    CancellationToken cancellationToken = default);

// After
Task<RapidResponse> SendMessageAsync(Endpoint remote, RapidRequest request,
    CancellationToken cancellationToken);
```

**Affected Code:**
- All async methods in `IMessagingClient`
- All async methods in `IMembershipServiceHandler`
- All async methods in `MembershipService`
- All async methods in `GrpcClient`
- All async methods in failure detectors
- All async methods in consensus protocols
- All public async APIs

**Migration for Callers:**
Callers without a meaningful token must explicitly pass `CancellationToken.None`:
```csharp
// Before: token could be omitted
await client.SendMessageAsync(endpoint, request);

// After: must be explicit
await client.SendMessageAsync(endpoint, request, CancellationToken.None);
```

**Benefits:**
- Forces consideration of cancellation at every call site
- Makes cancellation support explicit and intentional
- Improves responsiveness to cancellation
- Better testability of cancellation scenarios
- Clearer code intent

### 2. Migrate to IOptions Pattern for All Configuration
**Status:** Not Started  
**Priority:** High  
**Impact:** Configuration, Maintainability

**Description:**
Remove the `Settings` class and migrate all configuration to use the IOptions<T> pattern.

**Current Issues:**
- `Settings` class is used directly, not integrated with IOptions<T>
- Configuration is not validated
- Cannot bind from appsettings.json easily
- No support for named options
- Inconsistent with ASP.NET Core configuration patterns

**Proposed Solution:**
1. Create new options classes following naming conventions:
   ```csharp
   public sealed class RapidProtocolOptions
   {
       public int GrpcTimeoutMs { get; set; } = 1000;
       public int GrpcDefaultRetries { get; set; } = 5;
       public int GrpcJoinTimeoutMs { get; set; } = 5000;
       // ... other timing/protocol settings
   }
   ```

2. Register with validation:
   ```csharp
   services.Configure<RapidProtocolOptions>(configuration.GetSection("Rapid:Protocol"));
   services.AddSingleton<IValidateOptions<RapidProtocolOptions>, RapidProtocolOptionsValidator>();
   ```

3. Inject via IOptions<T>:
   ```csharp
   public class GrpcClient : IMessagingClient
   {
       private readonly RapidProtocolOptions _options;
       
       public GrpcClient(IOptions<RapidProtocolOptions> options)
       {
           _options = options.Value;
       }
   }
   ```

4. Support configuration from appsettings.json:
   ```json
   {
     "Rapid": {
       "Protocol": {
         "GrpcTimeoutMs": 1000,
         "GrpcDefaultRetries": 5
       }
     }
   }
   ```

**Files to Update:**
- Remove `Settings.cs`
- Create `RapidProtocolOptions.cs`
- Update `RapidOptions.cs` (currently has Settings property)
- Update all consumers: `GrpcClient`, `MembershipService`, etc.
- Update `RapidServiceCollectionExtensions.cs`
- Add `RapidProtocolOptionsValidator.cs`

**Benefits:**
- Standard ASP.NET Core configuration pattern
- Validation at startup
- Supports appsettings.json binding
- Supports named options for multiple configurations
- Better IntelliSense and documentation
- Easier to extend

### 3. Use System.TimeProvider Throughout Codebase
**Status:** Not Started  
**Priority:** High  
**Impact:** Testability, Code Quality

**Description:**
Replace all direct time-related calls with `System.TimeProvider` abstraction.

**Current Issues:**
- Direct `DateTime.UtcNow` calls throughout the codebase
- `Task.Delay()` calls that can't be controlled in tests
- `System.Diagnostics.Stopwatch` usage for timeouts
- Difficult to test time-dependent behavior

**Proposed Solution:**
1. Add `TimeProvider` property to `SharedResources`:
   ```csharp
   public sealed partial class SharedResources : IDisposable
   {
       public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
       // ...
   }
   ```

2. Make it configurable in `RapidServiceCollectionExtensions`:
   ```csharp
   public static IServiceCollection AddRapid(
       this IServiceCollection services,
       Action<RapidOptions> configure,
       TimeProvider? timeProvider = null)
   {
       var provider = timeProvider ?? TimeProvider.System;
       services.AddSingleton(provider);
       services.AddSingleton(sp => new SharedResources(
           sp.GetRequiredService<ILoggerFactory>(),
           sp.GetRequiredService<TimeProvider>()));
       // ...
   }
   ```

3. Replace all time-related calls:
   - `DateTime.UtcNow` → `timeProvider.GetUtcNow()`
   - `Task.Delay(ms)` → `Task.Delay(ms, timeProvider, cancellationToken)`
   - `Stopwatch.StartNew()` → `timeProvider.CreateTimer(...)`

4. Update tests to use `Microsoft.Extensions.TimeProvider.Testing.FakeTimeProvider`:
   ```csharp
   var fakeTime = new FakeTimeProvider();
   builder.Services.AddRapid(options => {...}, fakeTime);
   
   // Advance time in tests
   fakeTime.Advance(TimeSpan.FromSeconds(10));
   ```

**Files to Update:**
- `SharedResources.cs`
- `RapidServiceCollectionExtensions.cs`
- `MembershipService.cs` (batching timeouts)
- `GrpcClient.cs` (request timeouts)
- `PingPongFailureDetector.cs` (probe intervals)
- `FastPaxos.cs` (consensus timeouts)
- All test files

**Benefits:**
- Deterministic time-based testing
- No need for `Task.Delay()` in tests
- Can simulate time passage instantly
- Better test performance
- More reliable CI/CD builds

---

## Medium Priority

### 2. Health Check Integration
**Status:** Not Started  
**Priority:** Medium

Add ASP.NET Core health check support:
```csharp
builder.Services.AddHealthChecks()
    .AddRapidCluster();

app.MapHealthChecks("/health");
```

Health check should report:
- Cluster membership status
- Whether local node is reachable
- Membership size vs. expected
- Recent failure detector status

### 3. Metrics and Telemetry
**Status:** Not Started  
**Priority:** Medium

Add OpenTelemetry/metrics support:
- Membership size gauge
- Join/leave event counters
- Failure detection latency histogram
- Consensus round duration
- gRPC request duration

### 4. Configuration from appsettings.json
**Status:** Not Started  
**Priority:** Medium

Support configuration via `appsettings.json`:
```json
{
  "Rapid": {
    "ListenAddress": "127.0.0.1:1234",
    "SeedAddress": "127.0.0.1:1234",
    "Settings": {
      "GrpcTimeoutMs": 1000,
      "FailureDetectorIntervalMs": 1000
    }
  }
}
```

```csharp
builder.Services.AddRapid(builder.Configuration.GetSection("Rapid"));
```

### 5. Support for HostApplicationBuilder
**Status:** Partial  
**Priority:** Medium

Currently focused on `WebApplicationBuilder`. Add full support for console apps using `HostApplicationBuilder` without requiring Kestrel:
- Auto-detect whether HTTP/2 endpoint is needed
- Configure gRPC over Unix domain sockets for console scenarios
- Or use in-process transport for single-process testing

---

## Low Priority

### 6. Keyed Services for Multiple Clusters
**Status:** Not Started  
**Priority:** Low

Support multiple clusters in a single application using keyed services (.NET 8+):
```csharp
builder.Services.AddRapid("cluster1", options => {...});
builder.Services.AddRapid("cluster2", options => {...});

// Inject specific cluster
public MyService([FromKeyedServices("cluster1")] IRapidCluster cluster) {...}
```

### 7. Graceful Shutdown Improvements
**Status:** Not Started  
**Priority:** Low

- Add configurable shutdown timeout
- Ensure all messages are flushed before shutdown
- Coordinate graceful leave with `IHostApplicationLifetime.ApplicationStopping`

### 8. Structured Logging Enhancements
**Status:** Partial  
**Priority:** Low

- Add more structured logging with semantic properties
- Include trace correlation IDs for distributed tracing
- Add log scopes for better context

### 9. gRPC Interceptors
**Status:** Not Started  
**Priority:** Low

Allow users to register custom gRPC interceptors:
```csharp
builder.Services.AddRapid(options => 
{
    options.AddGrpcInterceptor<MyLoggingInterceptor>();
});
```

### 10. Configuration Validation
**Status:** Not Started  
**Priority:** Low

Add `IValidateOptions<RapidOptions>` to validate configuration at startup:
- Ensure ListenAddress is valid
- Ensure port is not in use
- Warn if SeedAddress == ListenAddress (seed node)

---

## Technical Debt

### Code Organization
- [ ] Consider splitting `MembershipService.cs` - it's very large
- [ ] Extract consensus logic into separate class
- [ ] Reduce cyclomatic complexity in join protocol

### Performance
- [ ] Profile memory allocations in hot paths
- [ ] Consider using ArrayPool<T> for message buffers
- [ ] Reduce allocations in membership view operations

### Testing
- [ ] Add more unit tests for edge cases
- [ ] Add chaos testing for failure scenarios
- [ ] Add performance benchmarks
- [ ] Add load tests for large clusters (100+ nodes)

---

## Completed

- ✅ Refactor to use modern ASP.NET Core hosting
- ✅ Remove IMessagingServer and manual WebApplication management
- ✅ Add IRapidCluster interface for DI
- ✅ Use BackgroundService for cluster lifecycle
- ✅ Integrate with Microsoft.Extensions.* patterns
