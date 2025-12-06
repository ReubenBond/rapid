# High Priority TODOs - Rapid.NET

This document highlights the most important technical improvements needed for Rapid.NET.

## 1. Remove Default CancellationToken Parameters (CRITICAL)

**Location:** All async methods throughout the codebase  
**See:** `IMessagingClient.cs` for detailed TODO comment

### Problem
Currently, all async methods have `CancellationToken cancellationToken = default`, which allows callers to omit the token. This leads to:
- Incomplete cancellation support
- Unclear when cancellation is respected
- Difficult to test cancellation scenarios
- Poor responsiveness to cancellation requests

### Solution
Remove all `= default` from CancellationToken parameters:

```csharp
// ❌ Before
Task<RapidResponse> SendMessageAsync(
    Endpoint remote, 
    RapidRequest request,
    CancellationToken cancellationToken = default);

// ✅ After  
Task<RapidResponse> SendMessageAsync(
    Endpoint remote, 
    RapidRequest request,
    CancellationToken cancellationToken);
```

### Impact
- **Breaking Change:** All callers must explicitly pass a CancellationToken
- Callers without a token must use `CancellationToken.None`
- Forces consideration of cancellation at every async call site
- Improves responsiveness and testability

### Files Affected
- `IMessagingClient.cs` ✓ (TODO added)
- `IMembershipServiceHandler.cs`
- `MembershipService.cs` (all async methods)
- `GrpcClient.cs`
- `PingPongFailureDetector.cs`
- `FastPaxos.cs`
- All other async methods

---

## 2. Migrate to IOptions Pattern

**Location:** `Settings.cs` and all configuration  
**See:** `Settings.cs` and `TECHNICAL_DEBT.md` for detailed TODO

### Problem
The current `Settings` class:
- Doesn't use IOptions<T> pattern
- Can't be validated at startup
- Difficult to bind from appsettings.json
- Not integrated with ASP.NET Core configuration system
- Inconsistent with modern .NET practices

### Solution
Replace `Settings` with proper options classes:

```csharp
// Create proper options class
public sealed class RapidProtocolOptions
{
    public int GrpcTimeoutMs { get; set; } = 1000;
    public int GrpcDefaultRetries { get; set; } = 5;
    // ...
}

// Register with validation
services.Configure<RapidProtocolOptions>(config.GetSection("Rapid:Protocol"));
services.AddSingleton<IValidateOptions<RapidProtocolOptions>, 
                      RapidProtocolOptionsValidator>();

// Inject via IOptions<T>
public GrpcClient(IOptions<RapidProtocolOptions> options)
{
    _options = options.Value;
}
```

### Benefits
- Standard ASP.NET Core pattern
- Startup validation
- Easy appsettings.json binding
- Named options support
- Better IntelliSense

### Files Affected
- Remove: `Settings.cs` ✓ (TODO added)
- Create: `RapidProtocolOptions.cs`
- Create: `RapidProtocolOptionsValidator.cs`
- Update: `RapidOptions.cs` (remove Settings property)
- Update: `GrpcClient.cs`, `MembershipService.cs`, etc.
- Update: `RapidServiceCollectionExtensions.cs`

---

## 3. Use System.TimeProvider Throughout

**Location:** All time-related code  
**See:** `SharedResources.cs` and `TECHNICAL_DEBT.md` for detailed TODO

### Problem
Direct usage of:
- `DateTime.UtcNow`
- `Task.Delay()`
- `Stopwatch.StartNew()`

Makes time-dependent code difficult to test.

### Solution
```csharp
// Add to SharedResources
public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

// Make configurable
services.AddRapid(options => {...}, timeProvider: fakeTime);

// Use in code
var now = _timeProvider.GetUtcNow();
await Task.Delay(timeout, _timeProvider, cancellationToken);
```

### Benefits
- Deterministic testing with `FakeTimeProvider`
- No `Task.Delay()` in tests
- Instant time passage simulation
- Faster, more reliable tests

### Files Affected
- `SharedResources.cs` ✓ (TODO added)
- `MembershipService.cs` (batching timeouts)
- `GrpcClient.cs` (request timeouts)
- `PingPongFailureDetector.cs` (probe intervals)
- `FastPaxos.cs` (consensus timeouts)
- All test files

---

## Implementation Priority

1. **First:** CancellationToken removal (highest impact on API design)
2. **Second:** IOptions migration (affects configuration architecture)
3. **Third:** TimeProvider integration (improves testability)

All three should be done before the first public release.

---

## Breaking Changes Expected

### CancellationToken Change
```csharp
// Code that currently works
await client.SendMessageAsync(endpoint, request);

// Must be updated to
await client.SendMessageAsync(endpoint, request, cancellationToken);
// or explicitly
await client.SendMessageAsync(endpoint, request, CancellationToken.None);
```

### Settings -> IOptions
```csharp
// Old way
var settings = new Settings { GrpcTimeoutMs = 2000 };
var client = new GrpcClient(settings, ...);

// New way
services.Configure<RapidProtocolOptions>(options => 
{
    options.GrpcTimeoutMs = 2000;
});
// GrpcClient gets IOptions<RapidProtocolOptions> via DI
```

### TimeProvider (Minimal Breaking Change)
Most changes are internal. External API stays the same, but tests can now inject `FakeTimeProvider`.

---

## Migration Checklist

- [ ] Add CancellationToken to all async methods (remove `= default`)
- [ ] Update all call sites to pass CancellationToken explicitly
- [ ] Create RapidProtocolOptions class
- [ ] Add IValidateOptions implementation
- [ ] Migrate all Settings usage to IOptions<T>
- [ ] Remove Settings class
- [ ] Add TimeProvider to SharedResources
- [ ] Replace DateTime.UtcNow with timeProvider.GetUtcNow()
- [ ] Replace Task.Delay with timeProvider delays
- [ ] Update all tests to use new patterns
- [ ] Update documentation and examples
- [ ] Verify all tests pass

---

**Last Updated:** December 6, 2024  
**Status:** TODOs documented, implementation pending
