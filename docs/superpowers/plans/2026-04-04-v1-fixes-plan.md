# HttpResilience.NET v1.0 Fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Address all critical issues and improvements from the architectural review to reach 9/10 resilience maturity before v1.0.0 release.

**Architecture:** The library is a thin wrapper over `Microsoft.Extensions.Http.Resilience` (Polly 8). Changes consolidate the dual pipeline ordering system into a single `PipelineOrder` list, fix RateLimiter disposal, eliminate options snapshot divergence, add structured logging for resilience events, and clean up dead code.

**Tech Stack:** .NET 10, Microsoft.Extensions.Http.Resilience 10.4.0, Polly 8.6.6, xunit 2.9.3

---

## File Structure

### Files to delete
- `src/HttpResilience.NET/Options/ResiliencePipelineType.cs` — enum replaced by PipelineOrder list
- `src/HttpResilience.NET/Options/PipelineOrderType.cs` — enum replaced by PipelineOrder list

### Files to create
- `src/HttpResilience.NET/Internal/HttpResilienceLogging.cs` — high-perf structured log messages

### Files to modify
- `src/HttpResilience.NET/Options/HttpResilienceOptions.cs` — remove PipelineType/old PipelineOrder, rename PipelineStrategyOrder
- `src/HttpResilience.NET/Options/RetryOptions.cs` — int to double for BaseDelaySeconds
- `src/HttpResilience.NET/Extensions/ServiceCollectionExtensions.cs` — unified ordering, DI lifecycle, logging, named options
- `src/HttpResilience.NET/Internal/HttpStandardResilienceHandlerConfig.cs` — IServiceCollection param, ILogger param
- `src/HttpResilience.NET/Internal/HttpStandardHedgingHandlerConfig.cs` — internal visibility, ILogger param
- `src/HttpResilience.NET/Internal/HttpResilienceMeteringEnricher.cs` — remove dead code
- `src/HttpResilience.NET/HttpResilience.NET.csproj` — add logging abstractions package
- `Directory.Packages.props` — add logging abstractions version
- `samples/HttpResilience.NET.Sample/HttpResilience.NET.Sample.csproj` — pin versions
- `samples/HttpResilience.NET.Sample/appsettings.json` — new PipelineOrder format
- `samples/HttpResilience.NET.Sample/Program.cs` — no code changes needed (uses section-based overloads)
- `CLAUDE.md` — update architecture docs
- `tests/HttpResilience.NET.Tests/Options/HttpResilienceOptionsValidationTests.cs` — update for new API
- `tests/HttpResilience.NET.Tests/Extensions/ServiceCollectionExtensionsTests.cs` — update for new API
- `tests/HttpResilience.NET.Tests/Extensions/HttpClientBehaviorTests.cs` — update for new API
- `tests/HttpResilience.NET.Tests/Internal/HttpStandardResilienceHandlerConfigTests.cs` — add IServiceCollection param
- `tests/HttpResilience.NET.Tests/Internal/HttpStandardHedgingHandlerConfigTests.cs` — no signature change needed
- `tests/HttpResilience.NET.Tests/Internal/HttpResilienceMeteringEnricherTests.cs` — verify no dead-code test
- `tests/HttpResilience.NET.IntegrationTests/ResiliencePipelineIntegrationTests.cs` — add PipelineOrder to config

---

## Task 1: Delete Legacy Ordering Enums and Update Options Model

**Files:**
- Delete: `src/HttpResilience.NET/Options/ResiliencePipelineType.cs`
- Delete: `src/HttpResilience.NET/Options/PipelineOrderType.cs`
- Modify: `src/HttpResilience.NET/Options/HttpResilienceOptions.cs`

- [ ] **Step 1: Delete `ResiliencePipelineType.cs`**

Delete the file `src/HttpResilience.NET/Options/ResiliencePipelineType.cs` entirely.

- [ ] **Step 2: Delete `PipelineOrderType.cs`**

Delete the file `src/HttpResilience.NET/Options/PipelineOrderType.cs` entirely.

- [ ] **Step 3: Update `HttpResilienceOptions.cs`**

Replace the full content of `src/HttpResilience.NET/Options/HttpResilienceOptions.cs` with:

```csharp
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace HttpResilience.NET.Options;

/// <summary>
/// Configuration options for outgoing HTTP client resilience. Consumed by multiple applications that register
/// HTTP clients with <c>AddHttpClientWithResilience</c>.
/// Options are grouped by feature (Retry, CircuitBreaker, Timeout, etc.) for clear boundaries and separation.
/// All properties are bindable from configuration under "HttpResilienceOptions"; nested sections use the property names (e.g. "Retry", "CircuitBreaker").
/// </summary>
public class HttpResilienceOptions
{
    /// <summary>
    /// Master switch for HTTP resilience. When true, the resilience pipeline and primary handler are applied when you call
    /// AddHttpClientWithResilience. When false or not set, the extension does nothing.
    /// <para><b>Use case:</b> Set to true in appsettings (e.g. per environment) to enable resilience; set to false to disable without changing code. Default: false (opt-in).</para>
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Order of pipeline strategies from outermost to innermost. Allowed values: "Fallback", "Bulkhead", "RateLimiter", "Standard", "Hedging".
    /// Must contain exactly one of "Standard" or "Hedging". Required when <see cref="Enabled"/> is true.
    /// <para><b>Use case:</b> e.g. [ "Fallback", "Bulkhead", "RateLimiter", "Standard" ] or [ "Hedging" ]. Config key: "PipelineOrder".</para>
    /// <para><b>Standard</b> = retry, circuit breaker, timeouts, optional rate limiting. <b>Hedging</b> = multiple requests, first success wins.</para>
    /// </summary>
    [JsonPropertyName("PipelineOrder")]
    public List<string>? PipelineOrder { get; set; }

    /// <summary>
    /// Connection and connection-pool options for the primary SocketsHttpHandler. Config key: "HttpResilienceOptions:Connection".
    /// </summary>
    [ValidateObjectMembers]
    public ConnectionOptions Connection { get; set; } = new();

    /// <summary>
    /// Request timeout options (total and per-attempt). Config key: "HttpResilienceOptions:Timeout".
    /// </summary>
    [ValidateObjectMembers]
    public TimeoutOptions Timeout { get; set; } = new();

    /// <summary>
    /// Retry strategy options. Config key: "HttpResilienceOptions:Retry".
    /// </summary>
    [ValidateObjectMembers]
    public RetryOptions Retry { get; set; } = new();

    /// <summary>
    /// Circuit breaker strategy options. Config key: "HttpResilienceOptions:CircuitBreaker".
    /// </summary>
    [ValidateObjectMembers]
    public CircuitBreakerOptions CircuitBreaker { get; set; } = new();

    /// <summary>
    /// Rate limiter options (standard and hedging pipelines when enabled). Config key: "HttpResilienceOptions:RateLimiter".
    /// </summary>
    [ValidateObjectMembers]
    public RateLimiterOptions RateLimiter { get; set; } = new();

    /// <summary>
    /// Fallback strategy options (both pipelines). Config key: "HttpResilienceOptions:Fallback".
    /// </summary>
    [ValidateObjectMembers]
    public FallbackOptions Fallback { get; set; } = new();

    /// <summary>
    /// Hedging strategy options (used when PipelineOrder contains "Hedging"). Config key: "HttpResilienceOptions:Hedging".
    /// </summary>
    [ValidateObjectMembers]
    public HedgingOptions Hedging { get; set; } = new();

    /// <summary>
    /// Bulkhead / concurrency limit options (both pipelines). Config key: "HttpResilienceOptions:Bulkhead".
    /// </summary>
    [ValidateObjectMembers]
    public BulkheadOptions Bulkhead { get; set; } = new();

    /// <summary>
    /// Pipeline selection (e.g. per-authority). When Mode is "ByAuthority", a separate pipeline instance is used per request authority (scheme + host + port).
    /// Config key: "HttpResilienceOptions:PipelineSelection".
    /// </summary>
    [ValidateObjectMembers]
    public PipelineSelectionOptions PipelineSelection { get; set; } = new();
}
```

- [ ] **Step 4: Verify the project does NOT build (expected — references to deleted types will fail)**

Run: `dotnet build src/HttpResilience.NET/ 2>&1 | head -30`

Expected: Build errors referencing `ResiliencePipelineType`, `PipelineOrderType`, `PipelineType`, old `PipelineOrder`, and `PipelineStrategyOrder`.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "refactor: remove legacy PipelineType/PipelineOrderType enums, unify to PipelineOrder list

Delete ResiliencePipelineType.cs and PipelineOrderType.cs.
Update HttpResilienceOptions to use a single PipelineOrder (List<string>) property.
Build is intentionally broken — subsequent commits fix dependent code.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

## Task 2: Update ServiceCollectionExtensions — Remove Legacy Ordering, Fix Validator

**Files:**
- Modify: `src/HttpResilience.NET/Extensions/ServiceCollectionExtensions.cs`

- [ ] **Step 1: Update the validator class**

In `src/HttpResilience.NET/Extensions/ServiceCollectionExtensions.cs`, replace the `IsValidPipelineStrategyOrder` method (lines 59-83) with:

```csharp
    private static bool IsValidPipelineOrder(List<string>? order)
    {
        if (order is null || order.Count == 0)
            return false;

        var allowed = PipelineStrategyNames.Allowed;
        int standardOrHedging = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in order)
        {
            if (string.IsNullOrWhiteSpace(item) || !allowed.Contains(item))
                return false;
            if (!seen.Add(item))
                return false;

            if (string.Equals(item, PipelineStrategyNames.Standard, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item, PipelineStrategyNames.Hedging, StringComparison.OrdinalIgnoreCase))
            {
                standardOrHedging++;
            }
        }

        return standardOrHedging == 1;
    }
```

Key change: `null` or empty now returns `false` (was `true`).

- [ ] **Step 2: Update the `HttpResilienceOptionsValidator.Validate` method**

Replace the validator body inside `Validate(string? name, HttpResilienceOptions options)` with:

```csharp
        public ValidateOptionsResult Validate(string? name, HttpResilienceOptions options)
        {
            if (!options.Enabled)
                return ValidateOptionsResult.Success;

            var failures = new List<string>();

            var dataAnnotationsResult = _dataAnnotations.Validate(name, options);
            if (dataAnnotationsResult is { Failed: true, Failures: { } daFailures })
            {
                foreach (var failure in daFailures)
                {
                    if (ShouldIgnoreFailureForDisabledSection(failure, options))
                        continue;

                    failures.Add(failure);
                }
            }

            if (options.Connection.Enabled && options.Connection.MaxConnectionsPerServer is < 1 or > 1000)
                failures.Add("Connection.MaxConnectionsPerServer must be between 1 and 1000.");

            if (options.Timeout.AttemptTimeoutSeconds > options.Timeout.TotalRequestTimeoutSeconds)
                failures.Add("Timeout.AttemptTimeoutSeconds must be less than or equal to Timeout.TotalRequestTimeoutSeconds.");

            if (!Enum.IsDefined(options.Retry.BackoffType))
                failures.Add("Retry.BackoffType must be Constant, Linear, or Exponential.");

            if (!IsValidPipelineOrder(options.PipelineOrder))
                failures.Add("PipelineOrder is required when Enabled is true and must contain only Fallback, Bulkhead, RateLimiter, Standard, Hedging with exactly one of Standard or Hedging. Example: [\"Standard\"] or [\"Fallback\", \"Bulkhead\", \"Standard\"].");

            if (!Enum.IsDefined(options.PipelineSelection.Mode))
                failures.Add("PipelineSelection.Mode must be None or ByAuthority.");

            bool isHedging = options.PipelineOrder?.Exists(s =>
                string.Equals(s, PipelineStrategyNames.Hedging, StringComparison.OrdinalIgnoreCase)) ?? false;

            if (options.Fallback.Enabled &&
                options.Fallback.StatusCode is < 400 or > 599)
                failures.Add("Fallback.StatusCode must be between 400 and 599 when Fallback.Enabled is true.");

            if (options.RateLimiter.Enabled &&
                !Enum.IsDefined(options.RateLimiter.Algorithm))
                failures.Add("RateLimiter.Algorithm must be FixedWindow, SlidingWindow, or TokenBucket.");

            if (isHedging)
            {
                if (options.Hedging.DelaySeconds is < 0 or > 60)
                    failures.Add("Hedging.DelaySeconds must be between 0 and 60.");

                if (options.Hedging.MaxHedgedAttempts is < 0 or > 10)
                    failures.Add("Hedging.MaxHedgedAttempts must be between 0 and 10.");
            }

            return failures.Count > 0
                ? ValidateOptionsResult.Fail(failures)
                : ValidateOptionsResult.Success;
        }
```

- [ ] **Step 3: Update `ShouldIgnoreFailureForDisabledSection`**

Replace the method with:

```csharp
        private static bool ShouldIgnoreFailureForDisabledSection(string failure, HttpResilienceOptions options)
        {
            if (!options.Connection.Enabled &&
                failure.Contains("Connection", StringComparison.OrdinalIgnoreCase))
                return true;

            if (!options.RateLimiter.Enabled &&
                failure.Contains("RateLimiter", StringComparison.OrdinalIgnoreCase))
                return true;

            if (!options.Fallback.Enabled &&
                failure.Contains("Fallback", StringComparison.OrdinalIgnoreCase))
                return true;

            if (!options.Bulkhead.Enabled &&
                failure.Contains("Bulkhead", StringComparison.OrdinalIgnoreCase))
                return true;

            bool isHedging = options.PipelineOrder?.Exists(s =>
                string.Equals(s, PipelineStrategyNames.Hedging, StringComparison.OrdinalIgnoreCase)) ?? false;
            if (!isHedging &&
                failure.Contains("Hedging", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }
```

- [ ] **Step 4: Remove `AddHandlersLegacyOrder` and update core overload**

Delete the entire `AddHandlersLegacyOrder` method (lines 449-476).

In the core `AddHttpClientWithResilience(IHttpClientBuilder, IConfigurationSection, ...)` method (line 263-288), replace the branching logic:

```csharp
        // OLD (lines 281-284):
        if (options.PipelineStrategyOrder is { Count: > 0 } order)
            AddHandlersInOrder(builder, options, timeout, order, fallbackHandler);
        else
            AddHandlersLegacyOrder(builder, options, timeout, fallbackHandler);

        // NEW:
        AddHandlersInOrder(builder, options, timeout, options.PipelineOrder!, fallbackHandler);
```

- [ ] **Step 5: Remove old `using` references if any remain for deleted types**

Search for any remaining references to `ResiliencePipelineType` or `PipelineOrderType` in the file and remove them. The `PipelineOrder` property is now always a `List<string>`.

- [ ] **Step 6: Verify the library builds**

Run: `dotnet build src/HttpResilience.NET/`

Expected: Build succeeds (tests will still fail — that's expected).

- [ ] **Step 7: Commit**

```bash
git add src/HttpResilience.NET/Extensions/ServiceCollectionExtensions.cs
git commit -m "refactor: unify pipeline ordering to single PipelineOrder list in validator and wiring

Remove AddHandlersLegacyOrder. PipelineOrder is now required when Enabled=true.
Validator checks PipelineOrder list instead of PipelineType/PipelineOrderType enums.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

## Task 3: Fix RateLimiter Memory Leak

**Files:**
- Modify: `src/HttpResilience.NET/Internal/HttpStandardResilienceHandlerConfig.cs`
- Modify: `src/HttpResilience.NET/Extensions/ServiceCollectionExtensions.cs`

- [ ] **Step 1: Add `IServiceCollection` parameter to `HttpStandardResilienceHandlerConfig.Create`**

Replace the full content of `src/HttpResilience.NET/Internal/HttpStandardResilienceHandlerConfig.cs` with:

```csharp
using System.Threading.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using HttpResilience.NET.Options;

namespace HttpResilience.NET.Internal;

/// <summary>
/// Builds <see cref="HttpStandardResilienceOptions"/> from <see cref="HttpResilienceOptions"/> and request timeout.
/// Internal-only helper; not part of the public API.
/// </summary>
internal static class HttpStandardResilienceHandlerConfig
{
    /// <summary>
    /// Creates an action that configures <see cref="HttpStandardResilienceOptions"/> from the given options and per-request timeout.
    /// </summary>
    /// <param name="options">HTTP resilience options (retry, circuit breaker, timeout, rate limit).</param>
    /// <param name="requestTimeoutSeconds">Effective total request timeout in seconds (use options.TotalRequestTimeoutSeconds or override per client).</param>
    /// <param name="services">Service collection for registering disposable resources (e.g. rate limiters).</param>
    /// <param name="rateLimiterHandledExternally">When true, rate limiting is already added as an outer handler (e.g. via PipelineOrder); do not configure the built-in rate limiter.</param>
    /// <returns>An action that configures HttpStandardResilienceOptions when applied to an options instance.</returns>
    public static Action<HttpStandardResilienceOptions> Create(
        HttpResilienceOptions options,
        int requestTimeoutSeconds,
        IServiceCollection services,
        bool rateLimiterHandledExternally = false)
    {
        var retry = options.Retry;
        var cb = options.CircuitBreaker;
        var rateLimit = options.RateLimiter;

        RateLimiter? limiter = null;
        if (rateLimit.Enabled && !rateLimiterHandledExternally)
        {
            limiter = RateLimiterFactory.CreateRateLimiter(rateLimit);
            services.AddSingleton(limiter);
        }

        return resilienceOptions =>
        {
            resilienceOptions.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(requestTimeoutSeconds);
            resilienceOptions.AttemptTimeout.Timeout = TimeSpan.FromSeconds(options.Timeout.AttemptTimeoutSeconds);
            resilienceOptions.Retry.MaxRetryAttempts = retry.MaxRetryAttempts;
            resilienceOptions.Retry.Delay = TimeSpan.FromSeconds(retry.BaseDelaySeconds);
            resilienceOptions.Retry.BackoffType = ToPollyBackoffType(retry.BackoffType);
            resilienceOptions.Retry.UseJitter = retry.UseJitter;
            resilienceOptions.Retry.ShouldRetryAfterHeader = retry.UseRetryAfterHeader;
            resilienceOptions.CircuitBreaker.FailureRatio = cb.FailureRatio;
            resilienceOptions.CircuitBreaker.MinimumThroughput = cb.MinimumThroughput;
            resilienceOptions.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(cb.SamplingDurationSeconds);
            resilienceOptions.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(cb.BreakDurationSeconds);

            if (limiter is not null)
            {
                resilienceOptions.RateLimiter.RateLimiter = args =>
                    limiter.AcquireAsync(1, args.Context.CancellationToken);
            }
        };
    }

    private static DelayBackoffType ToPollyBackoffType(RetryBackoffType value) =>
        value switch
        {
            RetryBackoffType.Constant => DelayBackoffType.Constant,
            RetryBackoffType.Linear => DelayBackoffType.Linear,
            _ => DelayBackoffType.Exponential
        };
}
```

- [ ] **Step 2: Update `AddRateLimitHandler` in `ServiceCollectionExtensions.cs`**

Replace the `AddRateLimitHandler` method with:

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

- [ ] **Step 3: Update all `AddStandardResilienceHandler` call sites to pass `builder.Services`**

In `AddHandlersInOrder`, update the standard handler line:

```csharp
            // OLD:
            var resilienceBuilder = builder.AddStandardResilienceHandler(HttpStandardResilienceHandlerConfig.Create(options, timeout, rateLimiterHandledExternally: rateLimiterInOrder));

            // NEW:
            var resilienceBuilder = builder.AddStandardResilienceHandler(HttpStandardResilienceHandlerConfig.Create(options, timeout, builder.Services, rateLimiterHandledExternally: rateLimiterInOrder));
```

- [ ] **Step 4: Verify the library builds**

Run: `dotnet build src/HttpResilience.NET/`

Expected: Build succeeds.

- [ ] **Step 5: Commit**

```bash
git add src/HttpResilience.NET/Internal/HttpStandardResilienceHandlerConfig.cs src/HttpResilience.NET/Extensions/ServiceCollectionExtensions.cs
git commit -m "fix: register RateLimiter as singleton in DI for proper disposal

RateLimiter instances are now registered in the service collection so
ServiceProvider disposal handles cleanup. Fixes memory leak under sustained load.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

## Task 4: Fix HttpStandardHedgingHandlerConfig Visibility

**Files:**
- Modify: `src/HttpResilience.NET/Internal/HttpStandardHedgingHandlerConfig.cs`

- [ ] **Step 1: Change `public` to `internal`**

In `src/HttpResilience.NET/Internal/HttpStandardHedgingHandlerConfig.cs`, change line 11:

```csharp
// OLD:
public static class HttpStandardHedgingHandlerConfig

// NEW:
internal static class HttpStandardHedgingHandlerConfig
```

- [ ] **Step 2: Verify build**

Run: `dotnet build src/HttpResilience.NET/`

Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/HttpResilience.NET/Internal/HttpStandardHedgingHandlerConfig.cs
git commit -m "fix: change HttpStandardHedgingHandlerConfig from public to internal

This class is in the Internal namespace and should not be part of the public API.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

## Task 5: Change BaseDelaySeconds from int to double

**Files:**
- Modify: `src/HttpResilience.NET/Options/RetryOptions.cs`

- [ ] **Step 1: Update the property type and range**

In `src/HttpResilience.NET/Options/RetryOptions.cs`, replace lines 22-23:

```csharp
    // OLD:
    [Range(1, 60, ErrorMessage = "BaseDelaySeconds must be between 1 and 60 seconds.")]
    public int BaseDelaySeconds { get; set; } = 2;

    // NEW:
    [Range(0.0, 60.0, ErrorMessage = "BaseDelaySeconds must be between 0 and 60 seconds.")]
    public double BaseDelaySeconds { get; set; } = 2.0;
```

- [ ] **Step 2: Verify build**

Run: `dotnet build src/HttpResilience.NET/`

Expected: Build succeeds. `TimeSpan.FromSeconds()` already accepts `double`.

- [ ] **Step 3: Commit**

```bash
git add src/HttpResilience.NET/Options/RetryOptions.cs
git commit -m "feat: change BaseDelaySeconds from int to double for sub-second retry delays

Allows values like 0.5 for high-throughput scenarios where 1-second minimum is too coarse.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

## Task 6: Remove Dead Code — IHttpPolicyEventArguments

**Files:**
- Modify: `src/HttpResilience.NET/Internal/HttpResilienceMeteringEnricher.cs`

- [ ] **Step 1: Remove the dead interface and unreachable branch**

In `src/HttpResilience.NET/Internal/HttpResilienceMeteringEnricher.cs`:

Remove the unreachable branch in `AddRequestDependencyName` (the `IHttpPolicyEventArguments` check):

```csharp
        // DELETE these lines (the if block checking IHttpPolicyEventArguments):
        if (dependencyName is null && context.TelemetryEvent.Arguments is IHttpPolicyEventArguments policyArgs)
        {
            dependencyName = GetDependencyNameFromRequest(policyArgs);
        }
```

Remove the `GetDependencyNameFromRequest` method:

```csharp
        // DELETE this entire method:
        private static string? GetDependencyNameFromRequest(IHttpPolicyEventArguments policyArgs)
        { ... }
```

Remove the `IHttpPolicyEventArguments` interface:

```csharp
        // DELETE this entire interface:
        internal interface IHttpPolicyEventArguments
        {
            HttpRequestMessage? Request { get; }
        }
```

- [ ] **Step 2: Verify build**

Run: `dotnet build src/HttpResilience.NET/`

Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/HttpResilience.NET/Internal/HttpResilienceMeteringEnricher.cs
git commit -m "refactor: remove dead IHttpPolicyEventArguments interface

Polly 8.x never exposes arguments implementing this interface. The type check
was always false at runtime. Vestigial from Polly v7 patterns.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

## Task 7: Add Structured Logging for Resilience Events

**Files:**
- Create: `src/HttpResilience.NET/Internal/HttpResilienceLogging.cs`
- Modify: `src/HttpResilience.NET/HttpResilience.NET.csproj`
- Modify: `Directory.Packages.props`
- Modify: `src/HttpResilience.NET/Internal/HttpStandardResilienceHandlerConfig.cs`
- Modify: `src/HttpResilience.NET/Internal/HttpStandardHedgingHandlerConfig.cs`
- Modify: `src/HttpResilience.NET/Extensions/ServiceCollectionExtensions.cs`

- [ ] **Step 1: Add `Microsoft.Extensions.Logging.Abstractions` package**

In `Directory.Packages.props`, add inside the `<ItemGroup>`:

```xml
    <PackageVersion Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.5" />
```

In `src/HttpResilience.NET/HttpResilience.NET.csproj`, add inside the `<ItemGroup>` with PackageReferences:

```xml
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
```

- [ ] **Step 2: Create `HttpResilienceLogging.cs`**

Create `src/HttpResilience.NET/Internal/HttpResilienceLogging.cs`:

```csharp
using Microsoft.Extensions.Logging;

namespace HttpResilience.NET.Internal;

/// <summary>
/// High-performance structured log messages for HTTP resilience events.
/// Uses LoggerMessage source generation for zero-allocation logging when the log level is disabled.
/// </summary>
internal static partial class HttpResilienceLogging
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "HttpResilience fallback activated for client '{ClientName}'. Original status: {StatusCode}, Exception: {ExceptionType}")]
    public static partial void FallbackActivated(ILogger logger, string clientName, int? statusCode, string? exceptionType);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "HttpResilience retry attempt {AttemptNumber} for client '{ClientName}' after {RetryDelayMs}ms. Reason: {Reason}")]
    public static partial void RetryAttempt(ILogger logger, int attemptNumber, string clientName, double retryDelayMs, string? reason);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
        Message = "HttpResilience circuit breaker OPENED for client '{ClientName}'. Break duration: {BreakDurationSeconds}s")]
    public static partial void CircuitBreakerOpened(ILogger logger, string clientName, double breakDurationSeconds);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information,
        Message = "HttpResilience circuit breaker HALF-OPEN for client '{ClientName}'")]
    public static partial void CircuitBreakerHalfOpen(ILogger logger, string clientName);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information,
        Message = "HttpResilience circuit breaker CLOSED for client '{ClientName}'")]
    public static partial void CircuitBreakerClosed(ILogger logger, string clientName);
}
```

- [ ] **Step 3: Add logging callbacks to `HttpStandardResilienceHandlerConfig`**

In `src/HttpResilience.NET/Internal/HttpStandardResilienceHandlerConfig.cs`, add `using Microsoft.Extensions.Logging;` at top, then add an `ILogger?` parameter and wire callbacks. Replace the full `Create` method:

```csharp
    public static Action<HttpStandardResilienceOptions> Create(
        HttpResilienceOptions options,
        int requestTimeoutSeconds,
        IServiceCollection services,
        bool rateLimiterHandledExternally = false,
        ILogger? logger = null,
        string? clientName = null)
    {
        var retry = options.Retry;
        var cb = options.CircuitBreaker;
        var rateLimit = options.RateLimiter;

        RateLimiter? limiter = null;
        if (rateLimit.Enabled && !rateLimiterHandledExternally)
        {
            limiter = RateLimiterFactory.CreateRateLimiter(rateLimit);
            services.AddSingleton(limiter);
        }

        return resilienceOptions =>
        {
            resilienceOptions.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(requestTimeoutSeconds);
            resilienceOptions.AttemptTimeout.Timeout = TimeSpan.FromSeconds(options.Timeout.AttemptTimeoutSeconds);
            resilienceOptions.Retry.MaxRetryAttempts = retry.MaxRetryAttempts;
            resilienceOptions.Retry.Delay = TimeSpan.FromSeconds(retry.BaseDelaySeconds);
            resilienceOptions.Retry.BackoffType = ToPollyBackoffType(retry.BackoffType);
            resilienceOptions.Retry.UseJitter = retry.UseJitter;
            resilienceOptions.Retry.ShouldRetryAfterHeader = retry.UseRetryAfterHeader;
            resilienceOptions.CircuitBreaker.FailureRatio = cb.FailureRatio;
            resilienceOptions.CircuitBreaker.MinimumThroughput = cb.MinimumThroughput;
            resilienceOptions.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(cb.SamplingDurationSeconds);
            resilienceOptions.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(cb.BreakDurationSeconds);

            if (limiter is not null)
            {
                resilienceOptions.RateLimiter.RateLimiter = args =>
                    limiter.AcquireAsync(1, args.Context.CancellationToken);
            }

            if (logger is not null)
            {
                var name = clientName ?? "unknown";
                resilienceOptions.Retry.OnRetry = args =>
                {
                    HttpResilienceLogging.RetryAttempt(logger, args.AttemptNumber, name,
                        args.RetryDelay.TotalMilliseconds,
                        args.Outcome.Exception?.Message ?? args.Outcome.Result?.StatusCode.ToString());
                    return default;
                };
                resilienceOptions.CircuitBreaker.OnOpened = args =>
                {
                    HttpResilienceLogging.CircuitBreakerOpened(logger, name, args.BreakDuration.TotalSeconds);
                    return default;
                };
                resilienceOptions.CircuitBreaker.OnHalfOpened = _ =>
                {
                    HttpResilienceLogging.CircuitBreakerHalfOpen(logger, name);
                    return default;
                };
                resilienceOptions.CircuitBreaker.OnClosed = _ =>
                {
                    HttpResilienceLogging.CircuitBreakerClosed(logger, name);
                    return default;
                };
            }
        };
    }
```

Add `using Microsoft.Extensions.Logging;` to the top of the file.

- [ ] **Step 4: Add logging to `HttpStandardHedgingHandlerConfig`**

In `src/HttpResilience.NET/Internal/HttpStandardHedgingHandlerConfig.cs`, add `using Microsoft.Extensions.Logging;` and update `Create`:

```csharp
    public static Action<HttpStandardHedgingResilienceOptions> Create(
        HttpResilienceOptions options,
        int requestTimeoutSeconds,
        ILogger? logger = null,
        string? clientName = null)
    {
        var hedging = options.Hedging;
        var timeout = options.Timeout;
        var cb = options.CircuitBreaker;
        return resilienceOptions =>
        {
            resilienceOptions.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(requestTimeoutSeconds);
            resilienceOptions.Hedging.Delay = hedging.DelaySeconds == 0
                ? TimeSpan.Zero
                : TimeSpan.FromSeconds(hedging.DelaySeconds);
            resilienceOptions.Hedging.MaxHedgedAttempts = hedging.MaxHedgedAttempts;

            resilienceOptions.Endpoint.Timeout.Timeout = TimeSpan.FromSeconds(timeout.AttemptTimeoutSeconds);
            resilienceOptions.Endpoint.CircuitBreaker.FailureRatio = cb.FailureRatio;
            resilienceOptions.Endpoint.CircuitBreaker.MinimumThroughput = cb.MinimumThroughput;
            resilienceOptions.Endpoint.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(cb.SamplingDurationSeconds);
            resilienceOptions.Endpoint.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(cb.BreakDurationSeconds);

            if (logger is not null)
            {
                var name = clientName ?? "unknown";
                resilienceOptions.Endpoint.CircuitBreaker.OnOpened = args =>
                {
                    HttpResilienceLogging.CircuitBreakerOpened(logger, name, args.BreakDuration.TotalSeconds);
                    return default;
                };
                resilienceOptions.Endpoint.CircuitBreaker.OnHalfOpened = _ =>
                {
                    HttpResilienceLogging.CircuitBreakerHalfOpen(logger, name);
                    return default;
                };
                resilienceOptions.Endpoint.CircuitBreaker.OnClosed = _ =>
                {
                    HttpResilienceLogging.CircuitBreakerClosed(logger, name);
                    return default;
                };
            }
        };
    }
```

- [ ] **Step 5: Wire logger in `ServiceCollectionExtensions.cs`**

Add `using Microsoft.Extensions.Logging;` to the top of `ServiceCollectionExtensions.cs`.

In `AddHandlersInOrder`, update the standard and hedging handler creation to resolve a logger:

```csharp
    private static void AddHandlersInOrder(IHttpClientBuilder builder, HttpResilienceOptions options, int timeout, List<string> order, IHttpFallbackHandler? fallbackHandler)
    {
        bool rateLimiterInOrder = order.Exists(s => string.Equals(s, PipelineStrategyNames.RateLimiter, StringComparison.OrdinalIgnoreCase));
        string clientName = builder.Name;

        for (int i = order.Count - 1; i >= 0; i--)
        {
            var name = order[i];
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (string.Equals(name, PipelineStrategyNames.Standard, StringComparison.OrdinalIgnoreCase))
            {
                var resilienceBuilder = builder.AddStandardResilienceHandler().Configure((resilienceOptions, serviceProvider) =>
                {
                    var logger = serviceProvider.GetService<ILoggerFactory>()?.CreateLogger("HttpResilience");
                    HttpStandardResilienceHandlerConfig.Create(options, timeout, builder.Services, rateLimiterHandledExternally: rateLimiterInOrder, logger: logger, clientName: clientName)(resilienceOptions);
                });
                if (IsPipelineSelectionByAuthority(options))
                    resilienceBuilder.SelectPipelineByAuthority();
                continue;
            }
            if (string.Equals(name, PipelineStrategyNames.Hedging, StringComparison.OrdinalIgnoreCase))
            {
                var hedgingBuilder = builder.AddStandardHedgingHandler().Configure((resilienceOptions, serviceProvider) =>
                {
                    var logger = serviceProvider.GetService<ILoggerFactory>()?.CreateLogger("HttpResilience");
                    HttpStandardHedgingHandlerConfig.Create(options, timeout, logger: logger, clientName: clientName)(resilienceOptions);
                });
                if (IsPipelineSelectionByAuthority(options))
                    hedgingBuilder.SelectPipelineByAuthority();
                continue;
            }
            if (string.Equals(name, PipelineStrategyNames.RateLimiter, StringComparison.OrdinalIgnoreCase) && options.RateLimiter.Enabled)
            {
                AddRateLimitHandler(builder, options.RateLimiter);
                continue;
            }
            if (string.Equals(name, PipelineStrategyNames.Bulkhead, StringComparison.OrdinalIgnoreCase) && options.Bulkhead.Enabled)
            {
                builder.AddResilienceHandler("concurrency", resilienceBuilder =>
                    resilienceBuilder.AddConcurrencyLimiter(options.Bulkhead.Limit, options.Bulkhead.QueueLimit));
                continue;
            }
            if (string.Equals(name, PipelineStrategyNames.Fallback, StringComparison.OrdinalIgnoreCase) && options.Fallback.Enabled)
                AddFallbackHandler(builder, options.Fallback, fallbackHandler);
        }
    }
```

- [ ] **Step 6: Add logging to `AddFallbackHandler` and `ExecuteFallbackAsync`**

Update `AddFallbackHandler` to capture the client name and resolve logger lazily:

```csharp
    private static void AddFallbackHandler(IHttpClientBuilder builder, FallbackOptions fallback, IHttpFallbackHandler? customHandler)
    {
        var only5xx = fallback.OnlyOn5xx;
        var body = fallback.ResponseBody;
        string clientName = builder.Name;
        builder.AddResilienceHandler("fallback", (resilienceBuilder, serviceProvider) =>
        {
            var logger = serviceProvider.GetService<ILoggerFactory>()?.CreateLogger("HttpResilience");
            resilienceBuilder.AddFallback(new FallbackStrategyOptions<HttpResponseMessage>
            {
                ShouldHandle = only5xx
                    ? new PredicateBuilder<HttpResponseMessage>()
                        .Handle<HttpRequestException>()
                        .HandleResult(r => r.StatusCode >= HttpStatusCode.InternalServerError)
                    : new PredicateBuilder<HttpResponseMessage>()
                        .Handle<HttpRequestException>()
                        .HandleResult(r => !r.IsSuccessStatusCode),
                FallbackAction = args => ExecuteFallbackAsync(customHandler, args, fallback.StatusCode, body, logger, clientName)
            });
        });
    }
```

Update `ExecuteFallbackAsync` to accept and use the logger:

```csharp
    private static async ValueTask<Outcome<HttpResponseMessage>> ExecuteFallbackAsync(
        IHttpFallbackHandler? customHandler,
        FallbackActionArguments<HttpResponseMessage> args,
        int statusCode,
        string? body,
        ILogger? logger,
        string clientName)
    {
        if (logger is not null)
        {
            HttpResilienceLogging.FallbackActivated(logger, clientName,
                (int?)args.Outcome.Result?.StatusCode,
                args.Outcome.Exception?.GetType().Name);
        }

        if (customHandler is not null)
        {
            var context = new HttpFallbackContext(args.Outcome);
            var customResponse = await customHandler.TryHandleAsync(context, args.Context.CancellationToken).ConfigureAwait(false);
            if (customResponse is not null)
                return Outcome.FromResult(customResponse);
        }
        var response = new HttpResponseMessage((HttpStatusCode)statusCode);
        if (!string.IsNullOrEmpty(body))
            response.Content = new StringContent(body);
        if (args.Outcome.Result?.RequestMessage is { } requestMessage)
            response.RequestMessage = requestMessage;
        return Outcome.FromResult(response);
    }
```

- [ ] **Step 7: Verify build**

Run: `dotnet build src/HttpResilience.NET/`

Expected: Build succeeds.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat: add structured logging for retry, circuit breaker, and fallback events

Wire OnRetry, OnOpened, OnHalfOpened, OnClosed callbacks with structured logs.
Use LoggerMessage source generation for zero-allocation when log level disabled.
Resolve ILoggerFactory from DI; gracefully skip if not registered.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

## Task 8: Fix Options Snapshot Divergence

**Files:**
- Modify: `src/HttpResilience.NET/Extensions/ServiceCollectionExtensions.cs`

- [ ] **Step 1: Update section-based core overload to register named options**

In the core `AddHttpClientWithResilience(IHttpClientBuilder, IConfigurationSection, ...)` method, update the approach to register named options and resolve `SocketsHttpHandler` from DI:

```csharp
    public static IHttpClientBuilder AddHttpClientWithResilience(
        this IHttpClientBuilder builder,
        IConfigurationSection section,
        int? requestTimeoutSeconds,
        IHttpFallbackHandler? fallbackHandler,
        Action<IHttpClientBuilder>? configurePipeline)
    {
        // Lightweight probe to check the Enabled flag only.
        var probe = new HttpResilienceOptions();
        section.Bind(probe);

        if (!probe.Enabled)
            return builder;

        // Register named options bound to this section so DI is the single source of truth.
        string optionsName = builder.Name;
        builder.Services.AddOptions<HttpResilienceOptions>(optionsName)
            .Bind(section);
        builder.Services.AddSingleton<IValidateOptions<HttpResilienceOptions>, HttpResilienceOptionsValidator>();

        int timeout = requestTimeoutSeconds ?? probe.Timeout.TotalRequestTimeoutSeconds;

        if (probe.Connection.Enabled)
        {
            builder.ConfigurePrimaryHttpMessageHandler(serviceProvider =>
            {
                var opts = serviceProvider.GetRequiredService<IOptionsSnapshot<HttpResilienceOptions>>().Get(optionsName);
                return SocketsHttpHandlerFactory.Create(opts);
            });
        }

        if (probe.PipelineOrder is { Count: > 0 })
            AddHandlersInOrder(builder, probe, timeout, probe.PipelineOrder, fallbackHandler);

        configurePipeline?.Invoke(builder);
        return builder;
    }
```

Add `using Microsoft.Extensions.Options;` if not already present (it should be).

- [ ] **Step 2: Update the `configureInnerPipeline` section-based overload similarly**

In the section-based `configureInnerPipeline` overload, apply the same named options pattern:

```csharp
    public static IHttpClientBuilder AddHttpClientWithResilience(
        this IHttpClientBuilder builder,
        IConfigurationSection section,
        int? requestTimeoutSeconds,
        IHttpFallbackHandler? fallbackHandler,
        Action<ResiliencePipelineBuilder<HttpResponseMessage>> configureInnerPipeline,
        Action<IHttpClientBuilder>? configurePipeline = null)
    {
        var probe = new HttpResilienceOptions();
        section.Bind(probe);

        if (!probe.Enabled)
            return builder;

        string optionsName = builder.Name;
        builder.Services.AddOptions<HttpResilienceOptions>(optionsName)
            .Bind(section);
        builder.Services.AddSingleton<IValidateOptions<HttpResilienceOptions>, HttpResilienceOptionsValidator>();

        int timeout = requestTimeoutSeconds ?? probe.Timeout.TotalRequestTimeoutSeconds;

        if (probe.Connection.Enabled)
        {
            builder.ConfigurePrimaryHttpMessageHandler(serviceProvider =>
            {
                var opts = serviceProvider.GetRequiredService<IOptionsSnapshot<HttpResilienceOptions>>().Get(optionsName);
                return SocketsHttpHandlerFactory.Create(opts);
            });
        }

        AddCustomStandardHandler(builder, timeout, fallbackHandler, configureInnerPipeline, configurePipeline);
        return builder;
    }
```

- [ ] **Step 3: Verify build**

Run: `dotnet build src/HttpResilience.NET/`

Expected: Build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/HttpResilience.NET/Extensions/ServiceCollectionExtensions.cs
git commit -m "fix: use named options from DI instead of local snapshot for section-based overloads

Section-based AddHttpClientWithResilience overloads now register named options
and resolve SocketsHttpHandler config from DI. Eliminates config divergence
between AddHttpResilienceOptions and AddHttpClientWithResilience.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

## Task 9: Fix Sample Project

**Files:**
- Modify: `samples/HttpResilience.NET.Sample/HttpResilience.NET.Sample.csproj`
- Modify: `samples/HttpResilience.NET.Sample/appsettings.json`

- [ ] **Step 1: Pin package versions in `.csproj`**

Replace the `<ItemGroup>` with PackageReferences in `samples/HttpResilience.NET.Sample/HttpResilience.NET.Sample.csproj`:

```xml
  <ItemGroup>
    <PackageReference Include="HttpResilience.NET" Version="1.0.0" />
    <PackageReference Include="Microsoft.Extensions.Configuration" Version="10.0.5" />
    <PackageReference Include="Microsoft.Extensions.Configuration.Json" Version="10.0.5" />
    <PackageReference Include="Microsoft.Extensions.Configuration.EnvironmentVariables" Version="10.0.5" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.0.5" />
    <PackageReference Include="Microsoft.Extensions.Http" Version="10.0.5" />
    <PackageReference Include="Microsoft.Extensions.Logging.Console" Version="10.0.5" />
  </ItemGroup>
```

- [ ] **Step 2: Update `appsettings.json` for new PipelineOrder format**

Replace the full content of `samples/HttpResilience.NET.Sample/appsettings.json`:

```json
{
  "HttpResilienceOptions": {
    "Enabled": true,
    "PipelineOrder": ["Standard"],
    "Connection": {
      "Enabled": true,
      "MaxConnectionsPerServer": 10,
      "PooledConnectionIdleTimeoutSeconds": 120,
      "PooledConnectionLifetimeSeconds": 600,
      "ConnectTimeoutSeconds": 10
    },
    "Timeout": {
      "TotalRequestTimeoutSeconds": 30,
      "AttemptTimeoutSeconds": 10
    },
    "Retry": {
      "MaxRetryAttempts": 3,
      "BaseDelaySeconds": 1,
      "BackoffType": "Exponential",
      "UseJitter": true,
      "UseRetryAfterHeader": true
    },
    "CircuitBreaker": {
      "MinimumThroughput": 20,
      "FailureRatio": 0.2,
      "SamplingDurationSeconds": 30,
      "BreakDurationSeconds": 5
    }
  },
  "HttpResilienceOptions:Hedging": {
    "Enabled": true,
    "PipelineOrder": ["Hedging"],
    "Connection": { "MaxConnectionsPerServer": 50, "ConnectTimeoutSeconds": 5 },
    "Timeout": { "TotalRequestTimeoutSeconds": 10, "AttemptTimeoutSeconds": 3 },
    "CircuitBreaker": { "MinimumThroughput": 20, "FailureRatio": 0.2, "SamplingDurationSeconds": 30, "BreakDurationSeconds": 5 },
    "Hedging": { "DelaySeconds": 1, "MaxHedgedAttempts": 1 }
  },
  "HttpResilienceOptions:WithFallback": {
    "Enabled": true,
    "PipelineOrder": ["Fallback", "Standard"],
    "Timeout": { "TotalRequestTimeoutSeconds": 10, "AttemptTimeoutSeconds": 5 },
    "Retry": { "MaxRetryAttempts": 2, "BaseDelaySeconds": 1, "BackoffType": "Exponential", "UseJitter": true },
    "Fallback": {
      "Enabled": true,
      "StatusCode": 503,
      "OnlyOn5xx": true,
      "ResponseBody": "Service temporarily unavailable. Please try again later."
    }
  },
  "HttpResilienceOptions:BulkheadAndRateLimit": {
    "Enabled": true,
    "PipelineOrder": ["Bulkhead", "RateLimiter", "Standard"],
    "Timeout": { "TotalRequestTimeoutSeconds": 30, "AttemptTimeoutSeconds": 10 },
    "Retry": { "MaxRetryAttempts": 1, "BaseDelaySeconds": 2, "BackoffType": "Exponential", "UseJitter": true },
    "Bulkhead": { "Enabled": true, "Limit": 20, "QueueLimit": 50 },
    "RateLimiter": {
      "Enabled": true,
      "PermitLimit": 100,
      "WindowSeconds": 1,
      "QueueLimit": 20,
      "Algorithm": "FixedWindow"
    }
  },
  "HttpResilienceOptions:TenantA": {
    "Enabled": true,
    "PipelineOrder": ["Standard"],
    "Timeout": { "TotalRequestTimeoutSeconds": 20, "AttemptTimeoutSeconds": 10 }
  },
  "HttpResilienceOptions:TenantB": {
    "Enabled": true,
    "PipelineOrder": ["Hedging"],
    "Timeout": { "TotalRequestTimeoutSeconds": 8, "AttemptTimeoutSeconds": 2 },
    "Hedging": { "DelaySeconds": 0, "MaxHedgedAttempts": 1 }
  }
}
```

- [ ] **Step 3: Commit**

```bash
git add samples/
git commit -m "fix: pin package versions in sample project and update to PipelineOrder format

Sample .csproj now has explicit versions. appsettings.json migrated from
PipelineType/PipelineOrder enum to unified PipelineOrder list.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

## Task 10: Update All Tests for New API Surface

**Files:**
- Modify: `tests/HttpResilience.NET.Tests/Options/HttpResilienceOptionsValidationTests.cs`
- Modify: `tests/HttpResilience.NET.Tests/Extensions/ServiceCollectionExtensionsTests.cs`
- Modify: `tests/HttpResilience.NET.Tests/Extensions/HttpClientBehaviorTests.cs`
- Modify: `tests/HttpResilience.NET.Tests/Internal/HttpStandardResilienceHandlerConfigTests.cs`
- Modify: `tests/HttpResilience.NET.IntegrationTests/ResiliencePipelineIntegrationTests.cs`

- [ ] **Step 1: Update `HttpResilienceOptionsValidationTests.cs`**

Replace the full content of `tests/HttpResilience.NET.Tests/Options/HttpResilienceOptionsValidationTests.cs` with:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using HttpResilience.NET.Extensions;
using HttpResilience.NET.Options;

namespace HttpResilience.NET.Tests.Options;

public class HttpResilienceOptionsValidationTests
{
    [Fact]
    public void AddHttpResilienceOptions_WithValidConfig_BindsAndValidatesOnStart()
    {
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Connection:MaxConnectionsPerServer"] = "20",
            ["HttpResilienceOptions:Retry:MaxRetryAttempts"] = "5"
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<HttpResilienceOptions>>().Value;
        Assert.Equal(20, options.Connection.MaxConnectionsPerServer);
        Assert.Equal(5, options.Retry.MaxRetryAttempts);
    }

    [Fact]
    public void AddHttpResilienceOptions_WithInvalidMaxConnectionsPerServer_ThrowsOnStart()
    {
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "true",
            ["HttpResilienceOptions:PipelineOrder:0"] = "Standard",
            ["HttpResilienceOptions:Connection:Enabled"] = "true",
            ["HttpResilienceOptions:Connection:MaxConnectionsPerServer"] = "0"
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(
            () => _ = provider.GetRequiredService<IOptions<HttpResilienceOptions>>().Value);
    }

    [Fact]
    public void AddHttpResilienceOptions_WithInvalidFallbackStatusCode_ThrowsOnStart()
    {
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "true",
            ["HttpResilienceOptions:PipelineOrder:0"] = "Standard",
            ["HttpResilienceOptions:Fallback:Enabled"] = "true",
            ["HttpResilienceOptions:Fallback:StatusCode"] = "200"
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(
            () => _ = provider.GetRequiredService<IOptions<HttpResilienceOptions>>().Value);
    }

    [Fact]
    public void AddHttpResilienceOptions_WithInvalidRetryBackoffType_ThrowsOnStart()
    {
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "true",
            ["HttpResilienceOptions:PipelineOrder:0"] = "Standard",
            ["HttpResilienceOptions:Retry:BackoffType"] = "99"
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(
            () => _ = provider.GetRequiredService<IOptions<HttpResilienceOptions>>().Value);
    }

    [Fact]
    public void AddHttpResilienceOptions_WithValidRetryBackoffType_BindsSuccessfully()
    {
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "true",
            ["HttpResilienceOptions:PipelineOrder:0"] = "Standard",
            ["HttpResilienceOptions:Retry:BackoffType"] = "Linear",
            ["HttpResilienceOptions:Retry:UseJitter"] = "false",
            ["HttpResilienceOptions:Retry:UseRetryAfterHeader"] = "true"
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<HttpResilienceOptions>>().Value;
        Assert.Equal(RetryBackoffType.Linear, options.Retry.BackoffType);
        Assert.False(options.Retry.UseJitter);
        Assert.True(options.Retry.UseRetryAfterHeader);
    }

    [Fact]
    public void AddHttpResilienceOptions_WithValidRateLimitAlgorithm_BindsSuccessfully()
    {
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "true",
            ["HttpResilienceOptions:PipelineOrder:0"] = "Standard",
            ["HttpResilienceOptions:RateLimiter:Algorithm"] = "SlidingWindow",
            ["HttpResilienceOptions:RateLimiter:SegmentsPerWindow"] = "4"
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<HttpResilienceOptions>>().Value;
        Assert.Equal(RateLimitAlgorithm.SlidingWindow, options.RateLimiter.Algorithm);
        Assert.Equal(4, options.RateLimiter.SegmentsPerWindow);
    }

    [Fact]
    public void AddHttpResilienceOptions_WithPipelineOrderHedging_BindsSuccessfully()
    {
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "true",
            ["HttpResilienceOptions:PipelineOrder:0"] = "Hedging",
            ["HttpResilienceOptions:Hedging:MaxHedgedAttempts"] = "2"
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<HttpResilienceOptions>>().Value;
        Assert.NotNull(options.PipelineOrder);
        Assert.Contains("Hedging", options.PipelineOrder);
        Assert.Equal(2, options.Hedging.MaxHedgedAttempts);
    }

    [Fact]
    public void AddHttpResilienceOptions_WithValidPipelineOrder_BindsSuccessfully()
    {
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "true",
            ["HttpResilienceOptions:PipelineOrder:0"] = "Fallback",
            ["HttpResilienceOptions:PipelineOrder:1"] = "Bulkhead",
            ["HttpResilienceOptions:PipelineOrder:2"] = "Standard"
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<HttpResilienceOptions>>().Value;
        Assert.NotNull(options.PipelineOrder);
        Assert.Equal(3, options.PipelineOrder.Count);
        Assert.Equal("Standard", options.PipelineOrder[2]);
    }

    [Fact]
    public void AddHttpResilienceOptions_WithPipelineOrderMissingCore_ThrowsOnStart()
    {
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "true",
            ["HttpResilienceOptions:PipelineOrder:0"] = "Fallback",
            ["HttpResilienceOptions:PipelineOrder:1"] = "Bulkhead"
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(
            () => _ = provider.GetRequiredService<IOptions<HttpResilienceOptions>>().Value);
    }

    [Fact]
    public void AddHttpResilienceOptions_WithNullPipelineOrder_ThrowsOnStart()
    {
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "true"
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(
            () => _ = provider.GetRequiredService<IOptions<HttpResilienceOptions>>().Value);
    }

    [Fact]
    public void AddHttpResilienceOptions_WithPipelineSelectionByAuthority_BindsSuccessfully()
    {
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "true",
            ["HttpResilienceOptions:PipelineOrder:0"] = "Standard",
            ["HttpResilienceOptions:PipelineSelection:Mode"] = "ByAuthority"
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<HttpResilienceOptions>>().Value;
        Assert.Equal(PipelineSelectionMode.ByAuthority, options.PipelineSelection.Mode);
    }

    [Fact]
    public void AddHttpResilienceOptions_WithAttemptTimeoutGreaterThanTotalTimeout_ThrowsOnStart()
    {
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "true",
            ["HttpResilienceOptions:PipelineOrder:0"] = "Standard",
            ["HttpResilienceOptions:Timeout:TotalRequestTimeoutSeconds"] = "10",
            ["HttpResilienceOptions:Timeout:AttemptTimeoutSeconds"] = "15"
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(
            () => _ = provider.GetRequiredService<IOptions<HttpResilienceOptions>>().Value);
    }

    [Fact]
    public void AddHttpResilienceOptions_WithMaxConnectionsPerServerOver1000_ThrowsOnStart()
    {
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "true",
            ["HttpResilienceOptions:PipelineOrder:0"] = "Standard",
            ["HttpResilienceOptions:Connection:Enabled"] = "true",
            ["HttpResilienceOptions:Connection:MaxConnectionsPerServer"] = "1001"
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(
            () => _ = provider.GetRequiredService<IOptions<HttpResilienceOptions>>().Value);
    }

    [Fact]
    public void AddHttpResilienceOptions_WithFallbackDisabled_DoesNotValidateStatusCode()
    {
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "false",
            ["HttpResilienceOptions:Fallback:Enabled"] = "false",
            ["HttpResilienceOptions:Fallback:StatusCode"] = "200"
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<HttpResilienceOptions>>().Value;
        Assert.False(options.Fallback.Enabled);
        Assert.Equal(200, options.Fallback.StatusCode);
    }

    [Fact]
    public void AddHttpResilienceOptions_WithFeatureDisabled_DoesNotValidateInvalidValues()
    {
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "false",
            ["HttpResilienceOptions:Connection:MaxConnectionsPerServer"] = "0",
            ["HttpResilienceOptions:Timeout:TotalRequestTimeoutSeconds"] = "10",
            ["HttpResilienceOptions:Timeout:AttemptTimeoutSeconds"] = "15",
            ["HttpResilienceOptions:Retry:BackoffType"] = "99",
            ["HttpResilienceOptions:RateLimiter:Algorithm"] = "99",
            ["HttpResilienceOptions:PipelineOrder:0"] = "Invalid",
            ["HttpResilienceOptions:PipelineSelection:Mode"] = "999"
        };

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<HttpResilienceOptions>>().Value;
        Assert.False(options.Enabled);
    }

    [Fact]
    public void AddHttpResilienceOptions_WithSubSecondBaseDelay_BindsSuccessfully()
    {
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "true",
            ["HttpResilienceOptions:PipelineOrder:0"] = "Standard",
            ["HttpResilienceOptions:Retry:BaseDelaySeconds"] = "0.5"
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<HttpResilienceOptions>>().Value;
        Assert.Equal(0.5, options.Retry.BaseDelaySeconds);
    }
}
```

- [ ] **Step 2: Update `ServiceCollectionExtensionsTests.cs`**

Every test that has `Enabled = true` needs `PipelineOrder:0` = `"Standard"` or `"Hedging"`. Replace the full file content:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using HttpResilience.NET.Extensions;

namespace HttpResilience.NET.Tests.Extensions;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddHttpResilienceOptions_And_AddHttpClientWithResilience_RegisterWithoutThrowing()
    {
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "true",
            ["HttpResilienceOptions:PipelineOrder:0"] = "Standard",
            ["HttpResilienceOptions:Connection:MaxConnectionsPerServer"] = "10",
            ["HttpResilienceOptions:Retry:MaxRetryAttempts"] = "3"
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);
        services.AddHttpClient("TestClient", _ => { })
            .AddHttpClientWithResilience(configuration, 30);

        using ServiceProvider provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        HttpClient client = factory.CreateClient("TestClient");

        Assert.NotNull(client);
    }

    [Fact]
    public void AddHttpClientWithResilience_WithNullTimeout_UsesDefault()
    {
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "true",
            ["HttpResilienceOptions:PipelineOrder:0"] = "Standard",
            ["HttpResilienceOptions:Connection:MaxConnectionsPerServer"] = "5"
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);
        services.AddHttpClient("DefaultTimeout", _ => { })
            .AddHttpClientWithResilience(configuration, requestTimeoutSeconds: null);

        using ServiceProvider provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        HttpClient client = factory.CreateClient("DefaultTimeout");

        Assert.NotNull(client);
    }

    [Fact]
    public void AddHttpClientWithResilience_WhenDisabled_ReturnsBuilderWithoutApplyingResilience()
    {
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "false",
            ["HttpResilienceOptions:Connection:MaxConnectionsPerServer"] = "10"
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);
        services.AddHttpClient("NoResilience", _ => { })
            .AddHttpClientWithResilience(configuration);

        using ServiceProvider provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        HttpClient client = factory.CreateClient("NoResilience");

        Assert.NotNull(client);
    }

    [Fact]
    public void AddHttpClientWithResilience_WithRateLimitEnabled_RegistersWithoutThrowing()
    {
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "true",
            ["HttpResilienceOptions:PipelineOrder:0"] = "Standard",
            ["HttpResilienceOptions:RateLimiter:Enabled"] = "true",
            ["HttpResilienceOptions:RateLimiter:PermitLimit"] = "100",
            ["HttpResilienceOptions:RateLimiter:WindowSeconds"] = "1"
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);
        services.AddHttpClient("RateLimited", _ => { })
            .AddHttpClientWithResilience(configuration);

        using ServiceProvider provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        HttpClient client = factory.CreateClient("RateLimited");

        Assert.NotNull(client);
    }

    [Fact]
    public void AddHttpClientWithResilience_WithConcurrencyLimitEnabled_RegistersWithoutThrowing()
    {
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "true",
            ["HttpResilienceOptions:PipelineOrder:0"] = "Bulkhead",
            ["HttpResilienceOptions:PipelineOrder:1"] = "Standard",
            ["HttpResilienceOptions:Bulkhead:Enabled"] = "true",
            ["HttpResilienceOptions:Bulkhead:Limit"] = "50",
            ["HttpResilienceOptions:Bulkhead:QueueLimit"] = "10"
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);
        services.AddHttpClient("ConcurrencyLimited", _ => { })
            .AddHttpClientWithResilience(configuration);

        using ServiceProvider provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        HttpClient client = factory.CreateClient("ConcurrencyLimited");

        Assert.NotNull(client);
    }

    [Fact]
    public void AddHttpClientWithResilience_WithFallbackEnabled_RegistersWithoutThrowing()
    {
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "true",
            ["HttpResilienceOptions:PipelineOrder:0"] = "Fallback",
            ["HttpResilienceOptions:PipelineOrder:1"] = "Standard",
            ["HttpResilienceOptions:Fallback:Enabled"] = "true",
            ["HttpResilienceOptions:Fallback:StatusCode"] = "503"
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);
        services.AddHttpClient("WithFallback", _ => { })
            .AddHttpClientWithResilience(configuration);

        using ServiceProvider provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        HttpClient client = factory.CreateClient("WithFallback");

        Assert.NotNull(client);
    }

    [Fact]
    public void AddHttpClientWithResilience_WithHedgingPipelineOrder_RegistersWithoutThrowing()
    {
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "true",
            ["HttpResilienceOptions:PipelineOrder:0"] = "Hedging",
            ["HttpResilienceOptions:Connection:MaxConnectionsPerServer"] = "10",
            ["HttpResilienceOptions:Hedging:DelaySeconds"] = "2",
            ["HttpResilienceOptions:Hedging:MaxHedgedAttempts"] = "1"
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);
        services.AddHttpClient("HedgingClient", _ => { })
            .AddHttpClientWithResilience(configuration, 30);

        using ServiceProvider provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        HttpClient client = factory.CreateClient("HedgingClient");

        Assert.NotNull(client);
    }

    [Fact]
    public void AddHttpClientWithResilience_WhenDisabled_ReturnsBuilderWithoutApplyingResilience_RegardlessOfPipelineOrder()
    {
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "false",
            ["HttpResilienceOptions:PipelineOrder:0"] = "Hedging"
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);
        services.AddHttpClient("NoHedging", _ => { })
            .AddHttpClientWithResilience(configuration);

        using ServiceProvider provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        HttpClient client = factory.CreateClient("NoHedging");

        Assert.NotNull(client);
    }

    [Fact]
    public void AddHttpClientWithResilience_WithCustomInnerPipeline_RegistersWithoutThrowing()
    {
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "true",
            ["HttpResilienceOptions:PipelineOrder:0"] = "Standard",
            ["HttpResilienceOptions:Connection:MaxConnectionsPerServer"] = "10"
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);
        services.AddHttpClient("CustomInner", _ => { })
            .AddHttpClientWithResilience(
                configuration,
                requestTimeoutSeconds: 10,
                fallbackHandler: null,
                configureInnerPipeline: inner =>
                {
                    inner
                        .AddTimeout(new HttpTimeoutStrategyOptions { Timeout = TimeSpan.FromSeconds(10) })
                        .AddRetry(new HttpRetryStrategyOptions
                        {
                            MaxRetryAttempts = 1,
                            Delay = TimeSpan.FromMilliseconds(50),
                            BackoffType = DelayBackoffType.Constant
                        })
                        .AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
                        {
                            FailureRatio = 0.5,
                            MinimumThroughput = 10,
                            SamplingDuration = TimeSpan.FromSeconds(30),
                            BreakDuration = TimeSpan.FromSeconds(5)
                        });
                });

        using ServiceProvider provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        HttpClient client = factory.CreateClient("CustomInner");

        Assert.NotNull(client);
    }
}
```

- [ ] **Step 3: Update `HttpClientBehaviorTests.cs`**

Add `["HttpResilienceOptions:PipelineOrder:0"] = "Standard"` (or `"Hedging"`) to every test's config dictionary where `Enabled = true`. The key additions for each test:

For all tests using standard pipeline, add:
```csharp
["HttpResilienceOptions:PipelineOrder:0"] = "Standard",
```

For the hedging test, change from:
```csharp
["HttpResilienceOptions:PipelineType"] = "Hedging",
```
to:
```csharp
["HttpResilienceOptions:PipelineOrder:0"] = "Hedging",
```

For tests with fallback enabled, add Fallback to the order:
```csharp
["HttpResilienceOptions:PipelineOrder:0"] = "Fallback",
["HttpResilienceOptions:PipelineOrder:1"] = "Standard",
```

Apply these changes to all 8 tests in the file. The test logic stays the same.

- [ ] **Step 4: Update `HttpStandardResilienceHandlerConfigTests.cs`**

Update the `Create` calls to pass `IServiceCollection`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using HttpResilience.NET.Internal;
using HttpResilience.NET.Options;

namespace HttpResilience.NET.Tests.Internal;

public class HttpStandardResilienceHandlerConfigTests
{
    [Fact]
    public void Create_MapsOptionsToHttpStandardResilienceOptions()
    {
        var options = new HttpResilienceOptions
        {
            Timeout = { TotalRequestTimeoutSeconds = 30, AttemptTimeoutSeconds = 5 },
            Retry =
            {
                MaxRetryAttempts = 3,
                BaseDelaySeconds = 2,
                BackoffType = RetryBackoffType.Linear,
                UseJitter = false,
                UseRetryAfterHeader = true
            },
            CircuitBreaker =
            {
                FailureRatio = 0.5,
                MinimumThroughput = 10,
                SamplingDurationSeconds = 60,
                BreakDurationSeconds = 15
            },
            RateLimiter =
            {
                Enabled = false
            }
        };

        var services = new ServiceCollection();
        var config = HttpStandardResilienceHandlerConfig.Create(options, requestTimeoutSeconds: 25, services: services);
        var target = new HttpStandardResilienceOptions();

        config(target);

        Assert.Equal(TimeSpan.FromSeconds(25), target.TotalRequestTimeout.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(5), target.AttemptTimeout.Timeout);
        Assert.Equal(3, target.Retry.MaxRetryAttempts);
        Assert.Equal(TimeSpan.FromSeconds(2), target.Retry.Delay);
        Assert.Equal(DelayBackoffType.Linear, target.Retry.BackoffType);
        Assert.False(target.Retry.UseJitter);
        Assert.True(target.Retry.ShouldRetryAfterHeader);
        Assert.Equal(0.5, target.CircuitBreaker.FailureRatio);
        Assert.Equal(10, target.CircuitBreaker.MinimumThroughput);
        Assert.Equal(TimeSpan.FromSeconds(60), target.CircuitBreaker.SamplingDuration);
        Assert.Equal(TimeSpan.FromSeconds(15), target.CircuitBreaker.BreakDuration);
    }

    [Fact]
    public void Create_WhenRateLimiterHandledExternally_DoesNotConfigureRateLimiter()
    {
        var options = new HttpResilienceOptions
        {
            Timeout = { TotalRequestTimeoutSeconds = 30, AttemptTimeoutSeconds = 10 },
            RateLimiter = { Enabled = true, PermitLimit = 100, WindowSeconds = 1 }
        };

        var services = new ServiceCollection();
        var config = HttpStandardResilienceHandlerConfig.Create(options, requestTimeoutSeconds: 30, services: services, rateLimiterHandledExternally: true);
        var target = new HttpStandardResilienceOptions();

        config(target);

        Assert.Null(target.RateLimiter.RateLimiter);
    }

    [Fact]
    public void Create_WhenRateLimiterEnabled_RegistersLimiterAsSingleton()
    {
        var options = new HttpResilienceOptions
        {
            Timeout = { TotalRequestTimeoutSeconds = 30, AttemptTimeoutSeconds = 10 },
            RateLimiter = { Enabled = true, PermitLimit = 100, WindowSeconds = 1 }
        };

        var services = new ServiceCollection();
        HttpStandardResilienceHandlerConfig.Create(options, requestTimeoutSeconds: 30, services: services);

        using var provider = services.BuildServiceProvider();
        var limiter = provider.GetService<System.Threading.RateLimiting.RateLimiter>();
        Assert.NotNull(limiter);
    }
}
```

- [ ] **Step 5: Update `ResiliencePipelineIntegrationTests.cs`**

Add `PipelineOrder` to all integration test config dictionaries. For each test, add:

```csharp
["HttpResilienceOptions:PipelineOrder:0"] = "Standard",
```

For the fallback test, use:

```csharp
["HttpResilienceOptions:PipelineOrder:0"] = "Fallback",
["HttpResilienceOptions:PipelineOrder:1"] = "Standard",
```

- [ ] **Step 6: Run all tests**

Run: `dotnet test`

Expected: All tests pass.

- [ ] **Step 7: Commit**

```bash
git add tests/
git commit -m "test: update all tests for unified PipelineOrder, RateLimiter DI, and sub-second delay

Migrate all test configs from PipelineType/PipelineStrategyOrder to PipelineOrder list.
Add new tests: PipelineOrder required validation, sub-second BaseDelaySeconds,
RateLimiter singleton registration.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

## Task 11: Update CLAUDE.md

**Files:**
- Modify: `CLAUDE.md`

- [ ] **Step 1: Update architecture section**

In `CLAUDE.md`, replace the pipeline types and ordering sections:

Replace:
```markdown
### Pipeline types

- **Standard** (`PipelineType = Standard`) — `AddStandardResilienceHandler`: timeout, retry, circuit breaker, optional rate limiter.
- **Hedging** (`PipelineType = Hedging`) — `AddStandardHedgingHandler`: sends multiple requests in parallel, first success wins.

### Pipeline ordering

Two ordering systems exist:
- **`PipelineStrategyOrder`** (preferred, explicit) — list of strategy names outermost→innermost, e.g. `["Fallback", "Bulkhead", "Standard"]`. Must contain exactly one of `Standard` or `Hedging`.
- **`PipelineOrder`** (legacy enum) — `FallbackThenConcurrency` or `ConcurrencyThenFallback`.

Handlers are added innermost-first (reversed from the order list) in `AddHandlersInOrder`.
```

With:
```markdown
### Pipeline types

- **Standard** (include `"Standard"` in `PipelineOrder`) — `AddStandardResilienceHandler`: timeout, retry, circuit breaker, optional rate limiter.
- **Hedging** (include `"Hedging"` in `PipelineOrder`) — `AddStandardHedgingHandler`: sends multiple requests in parallel, first success wins.

### Pipeline ordering

A single `PipelineOrder` list controls all handler ordering:
- `PipelineOrder` — list of strategy names outermost→innermost, e.g. `["Fallback", "Bulkhead", "Standard"]`.
- Must contain exactly one of `Standard` or `Hedging`.
- Required when `Enabled = true`.
- Handlers are added innermost-first (reversed from the order list) in `AddHandlersInOrder`.
```

- [ ] **Step 2: Commit**

```bash
git add CLAUDE.md
git commit -m "docs: update CLAUDE.md architecture for unified PipelineOrder

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

## Task 12: Final Verification

- [ ] **Step 1: Full build**

Run: `dotnet build`

Expected: Build succeeds with 0 errors, 0 warnings.

- [ ] **Step 2: Full test suite**

Run: `dotnet test`

Expected: All tests pass (51+ tests).

- [ ] **Step 3: Verify no remaining references to deleted types**

Run: `grep -r "ResiliencePipelineType\|PipelineOrderType\|PipelineStrategyOrder\|\.PipelineType" src/ tests/ samples/ --include="*.cs" --include="*.json"`

Expected: No matches (or only in comments/docs if any).

- [ ] **Step 4: Commit any final fixups**

If any issues found in steps 1-3, fix and commit.
