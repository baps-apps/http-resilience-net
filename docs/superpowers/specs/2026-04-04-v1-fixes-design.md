# HttpResilience.NET v1.0 — Fixes & Improvements Design Spec

**Date**: 2026-04-04
**Status**: Draft
**Based on**: [Architectural Review](../../ARCHITECTURAL-REVIEW.md)

---

## Goal

Address all critical issues and improvements identified in the architectural review to bring the library from 7.5/10 to 9/10 resilience maturity before the v1.0.0 release.

## Scope

| # | Category | Change |
|---|----------|--------|
| 1 | Critical | Unified pipeline ordering — remove dual system |
| 2 | Critical | RateLimiter lifecycle — fix memory leak |
| 3 | Critical | Options snapshot divergence — use named options from DI |
| 4 | Critical | `HttpStandardHedgingHandlerConfig` visibility — `public` → `internal` |
| 5 | Improvement | Observability — structured logging for fallback, retry, circuit breaker |
| 6 | Improvement | `BaseDelaySeconds` — change `int` to `double`, allow sub-second |
| 7 | Improvement | Dead code removal — `IHttpPolicyEventArguments` |
| 8 | Improvement | Sample project — fix missing package versions |
| 9 | Tests | Update all tests for new API surface |

---

## 1. Unified Pipeline Ordering (Breaking Change)

### What changes

**Remove entirely:**
- `ResiliencePipelineType` enum (file: `Options/ResiliencePipelineType.cs`)
- `PipelineOrderType` enum (file: `Options/PipelineOrderType.cs`)
- `PipelineType` property from `HttpResilienceOptions`
- `PipelineOrder` property from `HttpResilienceOptions`
- `AddHandlersLegacyOrder()` private method in `ServiceCollectionExtensions.cs`

**Rename:**
- `PipelineStrategyOrder` property → `PipelineOrder` (the name is now free)
- `PipelineStrategyNames` internal class → `PipelineStrategyNames` (no rename needed, still accurate)

**New behavior:**
- `PipelineOrder` is `List<string>`, required when `Enabled = true`
- Allowed values: `Fallback`, `Bulkhead`, `RateLimiter`, `Standard`, `Hedging`
- Must contain exactly one of `Standard` or `Hedging`
- Order = outermost → innermost (unchanged from current `PipelineStrategyOrder`)
- Minimal config: `["Standard"]` — just core pipeline, no outer handlers

**Validation changes in `HttpResilienceOptionsValidator`:**
- When `Enabled = true` and `PipelineOrder` is null or empty → fail: `"PipelineOrder is required when Enabled is true. Example: [\"Standard\"] or [\"Fallback\", \"Bulkhead\", \"Standard\"]."`
- Remove `PipelineType` enum validation
- Remove `PipelineOrder` enum validation (the old `PipelineOrderType`)
- Hedging-specific validation: check if `"Hedging"` is in `PipelineOrder` list instead of `PipelineType == Hedging`

**`ShouldIgnoreFailureForDisabledSection` changes:**
- Hedging check becomes: `PipelineOrder` does not contain `"Hedging"` (case-insensitive) instead of `PipelineType != Hedging`

**`AddHandlersInOrder` becomes the only handler-wiring path.** The `AddHandlersLegacyOrder` method is deleted. The `AddHttpClientWithResilience` core method simplifies from:

```csharp
// BEFORE
if (options.PipelineStrategyOrder is { Count: > 0 } order)
    AddHandlersInOrder(builder, options, timeout, order, fallbackHandler);
else
    AddHandlersLegacyOrder(builder, options, timeout, fallbackHandler);

// AFTER
AddHandlersInOrder(builder, options, timeout, options.PipelineOrder!, fallbackHandler);
```

### Config migration

```json
// BEFORE
{
  "PipelineType": "Standard",
  "PipelineOrder": "FallbackThenConcurrency"
}

// AFTER
{
  "PipelineOrder": ["Fallback", "Bulkhead", "Standard"]
}
```

```json
// BEFORE (hedging)
{
  "PipelineType": "Hedging"
}

// AFTER
{
  "PipelineOrder": ["Hedging"]
}
```

```json
// BEFORE (minimal standard)
{
  "PipelineType": "Standard"
}

// AFTER
{
  "PipelineOrder": ["Standard"]
}
```

### Files affected
- `Options/HttpResilienceOptions.cs` — remove `PipelineType`, remove old `PipelineOrder`, rename `PipelineStrategyOrder` → `PipelineOrder`
- `Options/ResiliencePipelineType.cs` — delete file
- `Options/PipelineOrderType.cs` — delete file
- `Extensions/ServiceCollectionExtensions.cs` — remove `AddHandlersLegacyOrder`, simplify core method, update validator
- All test files referencing `PipelineType`, `PipelineOrder` enum, or `PipelineStrategyOrder`
- All sample/config files
- `CLAUDE.md` architecture section

---

## 2. RateLimiter Lifecycle Fix

### Problem
`RateLimiterFactory.CreateRateLimiter()` returns `RateLimiter` instances (which implement `IDisposable`) that are captured in closures and never disposed.

### Design

Register the `RateLimiter` as a singleton in the service collection so `ServiceProvider` disposal handles cleanup.

**In `AddRateLimitHandler`:**
```csharp
private static void AddRateLimitHandler(IHttpClientBuilder builder, RateLimiterOptions rateLimiterOptions)
{
    var limiter = RateLimiterFactory.CreateRateLimiter(rateLimiterOptions);
    builder.Services.AddSingleton(limiter);

    builder.AddResilienceHandler("rateLimit", resilienceBuilder =>
    {
        resilienceBuilder.AddRateLimiter(new RateLimiterStrategyOptions
        {
            RateLimiter = args => limiter.AcquireAsync(1, args.Context.CancellationToken)
        });
    });
}
```

**In `HttpStandardResilienceHandlerConfig.Create`:**

The rate limiter created inside the `Action<HttpStandardResilienceOptions>` closure also leaks. Fix: accept an `IServiceCollection` parameter, register there:

```csharp
public static Action<HttpStandardResilienceOptions> Create(
    HttpResilienceOptions options,
    int requestTimeoutSeconds,
    IServiceCollection services,
    bool rateLimiterHandledExternally = false)
{
    // ...
    if (rateLimit.Enabled && !rateLimiterHandledExternally)
    {
        RateLimiter limiter = RateLimiterFactory.CreateRateLimiter(rateLimit);
        services.AddSingleton(limiter);
        resilienceOptions.RateLimiter.RateLimiter = args =>
            limiter.AcquireAsync(1, args.Context.CancellationToken);
    }
}
```

**Multiple limiters**: If both external and internal rate limiters are created (shouldn't happen due to `rateLimiterHandledExternally` flag), each is still a unique singleton instance — DI supports multiple registrations of the same type.

### Files affected
- `Internal/HttpStandardResilienceHandlerConfig.cs` — add `IServiceCollection` parameter
- `Extensions/ServiceCollectionExtensions.cs` — pass `builder.Services` to config builder, update `AddRateLimitHandler`

---

## 3. Options Snapshot Divergence Fix

### Problem
`AddHttpClientWithResilience` creates a local `new HttpResilienceOptions()` + `section.Bind()` snapshot that diverges from the DI-registered `IOptions<HttpResilienceOptions>`.

### Design

**For the default-section overloads** (use `IConfiguration`):
- Resolve options from DI using `IOptions<HttpResilienceOptions>` at configuration time
- The `AddHttpResilienceOptions` step already registered these

**For the section-based overloads** (use `IConfigurationSection`):
- Register named options using the client name as the options name
- Resolve via `IOptionsSnapshot<HttpResilienceOptions>` with the client name

**Implementation approach:**

The core section-based overload changes from:
```csharp
// BEFORE
var options = new HttpResilienceOptions();
section.Bind(options);

// AFTER — register named options, resolve from DI at configure time
string optionsName = builder.Name;
builder.Services.AddOptions<HttpResilienceOptions>(optionsName)
    .Bind(section)
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<HttpResilienceOptions>, HttpResilienceOptionsValidator>();
```

However, we still need the options at registration time (to decide whether to add handlers). Two-phase approach:

1. **Bind locally** only to check `Enabled` flag (lightweight, no leak risk)
2. **All handler configuration** uses the DI-registered options via `Configure<T>` callbacks that resolve at runtime

```csharp
// Check enabled (snapshot is fine for this boolean check)
var probe = new HttpResilienceOptions();
section.Bind(probe);
if (!probe.Enabled)
    return builder;

// Register named options for runtime resolution
string optionsName = builder.Name;
builder.Services.AddOptions<HttpResilienceOptions>(optionsName)
    .Bind(section);

// Handler wiring uses the named options
// (Polly handler Configure callbacks resolve from DI)
```

For the `SocketsHttpHandler` factory, the `configureInnerPipeline` overload already does the right thing at [line 326-329](../../src/HttpResilience.NET/Extensions/ServiceCollectionExtensions.cs#L326-L329) — resolving from `IOptions<T>`. The section-based overloads should follow the same pattern.

### Scope limitation
The `AddStandardResilienceHandler` and `AddStandardHedgingHandler` from Microsoft's library accept `Action<HttpStandardResilienceOptions>` which is invoked once at build time. These don't support hot-reload natively. Our fix ensures:
- Single source of truth (DI) for options
- Consistent between `AddHttpResilienceOptions` and `AddHttpClientWithResilience`
- `SocketsHttpHandler` factory resolves from DI (already done in one overload, extend to all)

Hot-reload of the full pipeline would require `IOptionsMonitor<T>` + pipeline rebuild, which is a v2.0 feature (Microsoft's library doesn't support it either).

### Files affected
- `Extensions/ServiceCollectionExtensions.cs` — all section-based overloads

---

## 4. `HttpStandardHedgingHandlerConfig` Visibility

Change `public static class` → `internal static class` in `Internal/HttpStandardHedgingHandlerConfig.cs`.

Single-line change. No other files affected (already `internal` usage only).

---

## 5. Observability — Structured Logging

### Design

Add an `ILoggerFactory` resolved from DI at handler configuration time. Wire logging callbacks on:

**Fallback:**
```csharp
logger.LogWarning(
    "HttpResilience fallback activated for client '{ClientName}'. " +
    "Original status: {StatusCode}, Exception: {ExceptionType}",
    clientName,
    args.Outcome.Result?.StatusCode,
    args.Outcome.Exception?.GetType().Name);
```

**Retry (on `HttpStandardResilienceOptions.Retry.OnRetry`):**
```csharp
logger.LogWarning(
    "HttpResilience retry attempt {AttemptNumber} for client '{ClientName}' " +
    "after {RetryDelay}ms. Reason: {Reason}",
    args.AttemptNumber,
    clientName,
    args.RetryDelay.TotalMilliseconds,
    args.Outcome.Exception?.Message ?? args.Outcome.Result?.StatusCode.ToString());
```

**Circuit Breaker (on `HttpStandardResilienceOptions.CircuitBreaker.OnOpened`):**
```csharp
logger.LogWarning(
    "HttpResilience circuit breaker OPENED for client '{ClientName}'. " +
    "Break duration: {BreakDuration}s",
    clientName,
    args.BreakDuration.TotalSeconds);
```

**Circuit Breaker (on `HttpStandardResilienceOptions.CircuitBreaker.OnHalfOpened`):**
```csharp
logger.LogInformation(
    "HttpResilience circuit breaker HALF-OPEN for client '{ClientName}'.",
    clientName);
```

**Circuit Breaker (on `HttpStandardResilienceOptions.CircuitBreaker.OnClosed`):**
```csharp
logger.LogInformation(
    "HttpResilience circuit breaker CLOSED for client '{ClientName}'.",
    clientName);
```

### Implementation

Create a new internal static class `HttpResilienceLogging` that encapsulates all log message definitions using `LoggerMessage.Define` for high-performance structured logging (zero-alloc when log level disabled):

```csharp
internal static partial class HttpResilienceLogging
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "HttpResilience fallback activated for client '{ClientName}'. Original status: {StatusCode}, Exception: {ExceptionType}")]
    public static partial void FallbackActivated(ILogger logger, string clientName, int? statusCode, string? exceptionType);

    // ... etc
}
```

### Logger resolution timing

The config builder methods (`HttpStandardResilienceHandlerConfig.Create`, `HttpStandardHedgingHandlerConfig.Create`) produce `Action<T>` delegates that are invoked **after** `ServiceProvider` is built (during first `HttpClient` resolution). Therefore, the `ILogger` cannot be a parameter on `Create()` — it must be captured lazily.

**Approach**: Pass `IServiceProvider` (or `ILoggerFactory`) into the action delegate via closure. The `AddResilienceHandler` overloads provide a `(ResiliencePipelineBuilder, IServiceProvider)` callback form that gives access to DI:

```csharp
builder.AddStandardResilienceHandler((options, serviceProvider) =>
{
    var logger = serviceProvider.GetService<ILoggerFactory>()?.CreateLogger("HttpResilience");
    // wire callbacks using logger (null-safe)
});
```

If `ILoggerFactory` is not registered (unlikely in real apps, possible in bare tests), logging is silently skipped via null-check.

### Where logging is wired

- `HttpStandardResilienceHandlerConfig.Create` — returns `Action<HttpStandardResilienceOptions>` that now also receives an `ILogger?` captured from the service provider callback. Wires `OnRetry`, `OnOpened`, `OnHalfOpened`, `OnClosed`.
- `HttpStandardHedgingHandlerConfig.Create` — same pattern for endpoint circuit breaker callbacks.
- `AddFallbackHandler` — the `FallbackAction` delegate receives an `ILogger?` via closure. Logs before returning synthetic response.

### Files affected
- New file: `Internal/HttpResilienceLogging.cs`
- `Internal/HttpStandardResilienceHandlerConfig.cs` — add `ILogger` parameter, wire callbacks
- `Internal/HttpStandardHedgingHandlerConfig.cs` — add `ILogger` parameter, wire callbacks
- `Extensions/ServiceCollectionExtensions.cs` — resolve `ILoggerFactory`, pass logger to config builders and fallback handler
- `HttpResilience.NET.csproj` — add `Microsoft.Extensions.Logging.Abstractions` package reference
- `Directory.Packages.props` — add version for `Microsoft.Extensions.Logging.Abstractions`

---

## 6. `BaseDelaySeconds` — `int` to `double`

### Change

In `Options/RetryOptions.cs`:
```csharp
// BEFORE
[Range(1, 60, ErrorMessage = "BaseDelaySeconds must be between 1 and 60 seconds.")]
public int BaseDelaySeconds { get; set; } = 2;

// AFTER
[Range(0.0, 60.0, ErrorMessage = "BaseDelaySeconds must be between 0 and 60 seconds.")]
public double BaseDelaySeconds { get; set; } = 2.0;
```

In `HttpStandardResilienceHandlerConfig.cs`:
```csharp
// Already uses TimeSpan.FromSeconds() which accepts double — no change needed
resilienceOptions.Retry.Delay = TimeSpan.FromSeconds(retry.BaseDelaySeconds);
```

In `HttpResilienceOptionsValidator`:
- Remove the `BackoffType` enum validation line (already handled by `[Range]` on the property and the `Enum.IsDefined` check remains for the enum itself)

### Config migration
```json
// BEFORE
"BaseDelaySeconds": 2

// AFTER (backward compatible — integers still parse as doubles)
"BaseDelaySeconds": 0.5
```

### Files affected
- `Options/RetryOptions.cs`
- Tests referencing `BaseDelaySeconds` with integer values (still valid, no test changes needed)

---

## 7. Dead Code Removal — `IHttpPolicyEventArguments`

### Change

In `Internal/HttpResilienceMeteringEnricher.cs`:
- Delete the `IHttpPolicyEventArguments` interface (lines 133-137)
- Delete the unreachable branch in `AddRequestDependencyName` (lines 77-80) that checks `context.TelemetryEvent.Arguments is IHttpPolicyEventArguments`

### Files affected
- `Internal/HttpResilienceMeteringEnricher.cs`
- `Tests/Internal/HttpResilienceMeteringEnricherTests.cs` — remove any test for the dead interface if present

---

## 8. Sample Project — Fix Package Versions

### Change

In `samples/HttpResilience.NET.Sample/HttpResilience.NET.Sample.csproj`, pin all package versions:

```xml
<PackageReference Include="HttpResilience.NET" Version="1.0.0" />
<PackageReference Include="Microsoft.Extensions.Configuration" Version="10.0.5" />
<PackageReference Include="Microsoft.Extensions.Configuration.Json" Version="10.0.5" />
<PackageReference Include="Microsoft.Extensions.Configuration.EnvironmentVariables" Version="10.0.5" />
<PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.0.5" />
<PackageReference Include="Microsoft.Extensions.Http" Version="10.0.5" />
<PackageReference Include="Microsoft.Extensions.Logging.Console" Version="10.0.5" />
```

Update `samples/HttpResilience.NET.Sample/appsettings.json` and `Program.cs` for the new `PipelineOrder` format (remove `PipelineType`, `PipelineOrder` enum references).

### Files affected
- `samples/HttpResilience.NET.Sample/HttpResilience.NET.Sample.csproj`
- `samples/HttpResilience.NET.Sample/appsettings.json`
- `samples/HttpResilience.NET.Sample/Program.cs`

---

## 9. Test Updates

### Existing tests to update
- All tests referencing `PipelineType` → replace with `PipelineOrder` list containing `"Standard"` or `"Hedging"`
- All tests referencing `PipelineStrategyOrder` → rename to `PipelineOrder`
- All tests referencing `PipelineOrder` as an enum → replace with `PipelineOrder` as list
- Validation tests for removed enums → delete
- Validation tests for `PipelineStrategyOrder` → update to validate `PipelineOrder` as required field

### New tests to add
- **RateLimiter disposal**: Verify that `RateLimiter` instances registered in DI are disposed when `ServiceProvider` is disposed
- **Named options resolution**: Verify section-based overloads register and resolve named options correctly
- **Logging callbacks**: Verify `OnRetry`, `OnBreak`, `OnHalfOpen`, `OnClosed` log structured messages when wired
- **Fallback logging**: Verify fallback activation produces a warning log entry
- **Sub-second retry delay**: Verify `BaseDelaySeconds = 0.5` is accepted and applied
- **`PipelineOrder` required validation**: Verify `Enabled = true` with null/empty `PipelineOrder` fails validation

---

## Out of Scope

- Hot-reload of full resilience pipeline (v2.0 — requires Microsoft library support)
- Per-endpoint override configuration (v2.0)
- Removing explicit `Polly.Extensions` package reference (low risk, defer)
- Custom metrics counters beyond the existing metering enricher

---

## Risk Assessment

| Change | Risk | Mitigation |
|--------|------|------------|
| Unified PipelineOrder | Breaking change for consumers | v1.0.0 pre-release, clear migration guide in CLAUDE.md |
| RateLimiter DI registration | Multiple limiters registered as singletons | `rateLimiterHandledExternally` flag prevents duplicates |
| Named options | Options name collision if two clients share a name | Client names are already unique by `IHttpClientFactory` contract |
| Logging callbacks | Performance overhead | `LoggerMessage.Define` is zero-alloc when level disabled |
| `double BaseDelaySeconds` | Backward compatible | JSON integers parse as doubles |
