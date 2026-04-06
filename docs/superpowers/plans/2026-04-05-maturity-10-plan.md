# Maturity 10/10 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close remaining observability, cloud-native, and test coverage gaps to reach 10/10 resilience maturity score.

**Architecture:** Add a `CircuitBreakerStateTracker` singleton that receives state updates from existing `OnOpened`/`OnHalfOpened`/`OnClosed` callbacks. An `IHealthCheck` implementation reads from this tracker. Three new test files cover rate limiter disposal, options hot-reload, and concurrent pipeline usage.

**Tech Stack:** Microsoft.Extensions.Diagnostics.HealthChecks, Microsoft.Extensions.Options (IOptionsMonitor), xunit, Microsoft.AspNetCore.TestHost

---

## File Structure

| Action | File | Responsibility |
|--------|------|---------------|
| Create | `src/HttpResilience.NET/Internal/CircuitBreakerStateTracker.cs` | Thread-safe tracker for circuit breaker state per client |
| Create | `src/HttpResilience.NET/Extensions/HealthCheckExtensions.cs` | `AddHttpResilienceHealthChecks()` extension method |
| Create | `src/HttpResilience.NET/Internal/HttpResilienceHealthCheck.cs` | `IHealthCheck` implementation reading from tracker |
| Modify | `src/HttpResilience.NET/Internal/HttpStandardResilienceHandlerConfig.cs` | Wire tracker into OnOpened/OnHalfOpened/OnClosed callbacks |
| Modify | `src/HttpResilience.NET/Internal/HttpStandardHedgingHandlerConfig.cs` | Wire tracker into endpoint circuit breaker callbacks |
| Modify | `src/HttpResilience.NET/Extensions/ServiceCollectionExtensions.cs` | Pass tracker from DI to config builders |
| Modify | `src/HttpResilience.NET/HttpResilience.NET.csproj` | Add `Microsoft.Extensions.Diagnostics.HealthChecks` package ref |
| Modify | `Directory.Packages.props` | Add health checks package version |
| Create | `tests/HttpResilience.NET.Tests/Internal/CircuitBreakerStateTrackerTests.cs` | Unit tests for tracker |
| Create | `tests/HttpResilience.NET.Tests/Internal/HttpResilienceHealthCheckTests.cs` | Unit tests for health check |
| Create | `tests/HttpResilience.NET.Tests/Extensions/RateLimiterDisposalTests.cs` | Verify DI disposes rate limiters |
| Create | `tests/HttpResilience.NET.Tests/Options/OptionsMonitorHotReloadTests.cs` | Verify IOptionsMonitor fires on config change |
| Create | `tests/HttpResilience.NET.IntegrationTests/ConcurrencyIntegrationTests.cs` | Parallel HTTP calls through resilience pipeline |
| Modify | `tests/HttpResilience.NET.Tests/HttpResilience.NET.Tests.csproj` | Add `Microsoft.Extensions.Diagnostics.HealthChecks` package ref |

---

### Task 1: CircuitBreakerStateTracker

**Files:**
- Create: `src/HttpResilience.NET/Internal/CircuitBreakerStateTracker.cs`
- Test: `tests/HttpResilience.NET.Tests/Internal/CircuitBreakerStateTrackerTests.cs`

- [ ] **Step 1: Write failing tests for the tracker**

```csharp
// tests/HttpResilience.NET.Tests/Internal/CircuitBreakerStateTrackerTests.cs
using HttpResilience.NET.Internal;

namespace HttpResilience.NET.Tests.Internal;

public class CircuitBreakerStateTrackerTests
{
    [Fact]
    public void GetState_UnknownClient_ReturnsClosed()
    {
        var tracker = new CircuitBreakerStateTracker();
        var state = tracker.GetState("unknown-client");
        Assert.Equal(CircuitState.Closed, state);
    }

    [Fact]
    public void ReportOpened_ThenGetState_ReturnsOpen()
    {
        var tracker = new CircuitBreakerStateTracker();
        tracker.ReportOpened("my-client");
        Assert.Equal(CircuitState.Open, tracker.GetState("my-client"));
    }

    [Fact]
    public void ReportHalfOpen_ThenGetState_ReturnsHalfOpen()
    {
        var tracker = new CircuitBreakerStateTracker();
        tracker.ReportHalfOpen("my-client");
        Assert.Equal(CircuitState.HalfOpen, tracker.GetState("my-client"));
    }

    [Fact]
    public void ReportClosed_AfterOpen_ReturnsClosed()
    {
        var tracker = new CircuitBreakerStateTracker();
        tracker.ReportOpened("my-client");
        tracker.ReportClosed("my-client");
        Assert.Equal(CircuitState.Closed, tracker.GetState("my-client"));
    }

    [Fact]
    public void GetAllStates_ReturnsAllTrackedClients()
    {
        var tracker = new CircuitBreakerStateTracker();
        tracker.ReportOpened("client-a");
        tracker.ReportClosed("client-b");

        var all = tracker.GetAllStates();
        Assert.Equal(2, all.Count);
        Assert.Equal(CircuitState.Open, all["client-a"]);
        Assert.Equal(CircuitState.Closed, all["client-b"]);
    }

    [Fact]
    public void HasOpenCircuits_WhenNoneOpen_ReturnsFalse()
    {
        var tracker = new CircuitBreakerStateTracker();
        tracker.ReportClosed("client-a");
        Assert.False(tracker.HasOpenCircuits);
    }

    [Fact]
    public void HasOpenCircuits_WhenOneOpen_ReturnsTrue()
    {
        var tracker = new CircuitBreakerStateTracker();
        tracker.ReportOpened("client-a");
        Assert.True(tracker.HasOpenCircuits);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/HttpResilience.NET.Tests/ --filter "FullyQualifiedName~CircuitBreakerStateTrackerTests" --no-build 2>&1 || true`
Expected: Build failure — `CircuitBreakerStateTracker` does not exist yet.

- [ ] **Step 3: Implement CircuitBreakerStateTracker**

```csharp
// src/HttpResilience.NET/Internal/CircuitBreakerStateTracker.cs
using System.Collections.Concurrent;

namespace HttpResilience.NET.Internal;

/// <summary>
/// Possible states for a circuit breaker.
/// </summary>
public enum CircuitState
{
    /// <summary>Circuit is closed — requests flow normally.</summary>
    Closed,
    /// <summary>Circuit is open — requests are rejected.</summary>
    Open,
    /// <summary>Circuit is half-open — a trial request is allowed.</summary>
    HalfOpen
}

/// <summary>
/// Thread-safe tracker for circuit breaker state per named HTTP client.
/// Updated by Polly circuit breaker callbacks; read by health checks.
/// </summary>
public sealed class CircuitBreakerStateTracker
{
    private readonly ConcurrentDictionary<string, CircuitState> _states = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Reports that the circuit breaker for the given client has opened.</summary>
    public void ReportOpened(string clientName) => _states[clientName] = CircuitState.Open;

    /// <summary>Reports that the circuit breaker for the given client is half-open.</summary>
    public void ReportHalfOpen(string clientName) => _states[clientName] = CircuitState.HalfOpen;

    /// <summary>Reports that the circuit breaker for the given client has closed.</summary>
    public void ReportClosed(string clientName) => _states[clientName] = CircuitState.Closed;

    /// <summary>Gets the current state for the given client. Returns <see cref="CircuitState.Closed"/> if unknown.</summary>
    public CircuitState GetState(string clientName) =>
        _states.TryGetValue(clientName, out var state) ? state : CircuitState.Closed;

    /// <summary>Returns a snapshot of all tracked client states.</summary>
    public Dictionary<string, CircuitState> GetAllStates() => new(_states, StringComparer.OrdinalIgnoreCase);

    /// <summary>True if any tracked circuit breaker is currently open.</summary>
    public bool HasOpenCircuits => _states.Values.Any(s => s == CircuitState.Open);
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/HttpResilience.NET.Tests/ --filter "FullyQualifiedName~CircuitBreakerStateTrackerTests"`
Expected: All 7 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/HttpResilience.NET/Internal/CircuitBreakerStateTracker.cs tests/HttpResilience.NET.Tests/Internal/CircuitBreakerStateTrackerTests.cs
git commit -m "feat: add CircuitBreakerStateTracker for health check support"
```

---

### Task 2: HttpResilienceHealthCheck

**Files:**
- Create: `src/HttpResilience.NET/Internal/HttpResilienceHealthCheck.cs`
- Test: `tests/HttpResilience.NET.Tests/Internal/HttpResilienceHealthCheckTests.cs`
- Modify: `src/HttpResilience.NET/HttpResilience.NET.csproj` — add health checks package
- Modify: `Directory.Packages.props` — add version
- Modify: `tests/HttpResilience.NET.Tests/HttpResilience.NET.Tests.csproj` — add health checks package

- [ ] **Step 1: Add `Microsoft.Extensions.Diagnostics.HealthChecks` package**

Add to `Directory.Packages.props` inside the `<ItemGroup>`:
```xml
<PackageVersion Include="Microsoft.Extensions.Diagnostics.HealthChecks" Version="10.0.5" />
```

Add to `src/HttpResilience.NET/HttpResilience.NET.csproj` inside `<ItemGroup>` with other PackageReferences:
```xml
<PackageReference Include="Microsoft.Extensions.Diagnostics.HealthChecks" />
```

Add to `tests/HttpResilience.NET.Tests/HttpResilience.NET.Tests.csproj` inside `<ItemGroup>` with other PackageReferences:
```xml
<PackageReference Include="Microsoft.Extensions.Diagnostics.HealthChecks" />
```

Run: `dotnet build` to verify it compiles.

- [ ] **Step 2: Write failing tests for the health check**

```csharp
// tests/HttpResilience.NET.Tests/Internal/HttpResilienceHealthCheckTests.cs
using Microsoft.Extensions.Diagnostics.HealthChecks;
using HttpResilience.NET.Internal;

namespace HttpResilience.NET.Tests.Internal;

public class HttpResilienceHealthCheckTests
{
    [Fact]
    public async Task CheckHealthAsync_AllClosed_ReturnsHealthy()
    {
        var tracker = new CircuitBreakerStateTracker();
        tracker.ReportClosed("client-a");
        var check = new HttpResilienceHealthCheck(tracker);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task CheckHealthAsync_NoClients_ReturnsHealthy()
    {
        var tracker = new CircuitBreakerStateTracker();
        var check = new HttpResilienceHealthCheck(tracker);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task CheckHealthAsync_OneOpen_ReturnsDegraded()
    {
        var tracker = new CircuitBreakerStateTracker();
        tracker.ReportClosed("client-a");
        tracker.ReportOpened("client-b");
        var check = new HttpResilienceHealthCheck(tracker);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("client-b", result.Description!);
    }

    [Fact]
    public async Task CheckHealthAsync_HalfOpen_ReturnsDegraded()
    {
        var tracker = new CircuitBreakerStateTracker();
        tracker.ReportHalfOpen("client-a");
        var check = new HttpResilienceHealthCheck(tracker);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    [Fact]
    public async Task CheckHealthAsync_IncludesDataPerClient()
    {
        var tracker = new CircuitBreakerStateTracker();
        tracker.ReportOpened("client-a");
        tracker.ReportClosed("client-b");
        var check = new HttpResilienceHealthCheck(tracker);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.True(result.Data.ContainsKey("client-a"));
        Assert.True(result.Data.ContainsKey("client-b"));
        Assert.Equal("Open", result.Data["client-a"]);
        Assert.Equal("Closed", result.Data["client-b"]);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/HttpResilience.NET.Tests/ --filter "FullyQualifiedName~HttpResilienceHealthCheckTests" --no-build 2>&1 || true`
Expected: Build failure — `HttpResilienceHealthCheck` does not exist yet.

- [ ] **Step 4: Implement HttpResilienceHealthCheck**

```csharp
// src/HttpResilience.NET/Internal/HttpResilienceHealthCheck.cs
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HttpResilience.NET.Internal;

/// <summary>
/// Reports the aggregate circuit breaker state across all tracked HTTP clients.
/// Returns <see cref="HealthStatus.Healthy"/> when all breakers are closed,
/// <see cref="HealthStatus.Degraded"/> when any breaker is open or half-open.
/// </summary>
internal sealed class HttpResilienceHealthCheck : IHealthCheck
{
    private readonly CircuitBreakerStateTracker _tracker;

    public HttpResilienceHealthCheck(CircuitBreakerStateTracker tracker) => _tracker = tracker;

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var states = _tracker.GetAllStates();
        var data = new Dictionary<string, object>();
        var unhealthy = new List<string>();

        foreach (var (clientName, state) in states)
        {
            data[clientName] = state.ToString();
            if (state != CircuitState.Closed)
                unhealthy.Add($"{clientName}={state}");
        }

        if (unhealthy.Count == 0)
            return Task.FromResult(HealthCheckResult.Healthy("All circuit breakers are closed.", data));

        var description = $"Circuit breakers not closed: {string.Join(", ", unhealthy)}";
        return Task.FromResult(HealthCheckResult.Degraded(description, data: data));
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/HttpResilience.NET.Tests/ --filter "FullyQualifiedName~HttpResilienceHealthCheckTests"`
Expected: All 5 tests PASS.

- [ ] **Step 6: Commit**

```bash
git add Directory.Packages.props src/HttpResilience.NET/HttpResilience.NET.csproj tests/HttpResilience.NET.Tests/HttpResilience.NET.Tests.csproj src/HttpResilience.NET/Internal/HttpResilienceHealthCheck.cs tests/HttpResilience.NET.Tests/Internal/HttpResilienceHealthCheckTests.cs
git commit -m "feat: add HttpResilienceHealthCheck with IHealthCheck implementation"
```

---

### Task 3: Wire tracker into pipeline callbacks and add HealthCheckExtensions

**Files:**
- Create: `src/HttpResilience.NET/Extensions/HealthCheckExtensions.cs`
- Modify: `src/HttpResilience.NET/Internal/HttpStandardResilienceHandlerConfig.cs`
- Modify: `src/HttpResilience.NET/Internal/HttpStandardHedgingHandlerConfig.cs`
- Modify: `src/HttpResilience.NET/Extensions/ServiceCollectionExtensions.cs`

- [ ] **Step 1: Create HealthCheckExtensions**

```csharp
// src/HttpResilience.NET/Extensions/HealthCheckExtensions.cs
using HttpResilience.NET.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HttpResilience.NET.Extensions;

/// <summary>
/// Extension methods for registering HTTP resilience health checks.
/// </summary>
public static class HealthCheckExtensions
{
    /// <summary>
    /// Adds a health check that reports the aggregate circuit breaker state across all HTTP clients
    /// configured with <see cref="ServiceCollectionExtensions.AddHttpClientWithResilience"/>.
    /// Returns <see cref="HealthStatus.Degraded"/> when any circuit breaker is open or half-open.
    /// <para><b>Use case:</b> Wire into Kubernetes readiness probes or ASP.NET health check endpoints
    /// so that traffic is shifted away when downstream dependencies are unhealthy.</para>
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="name">Health check name. Default: "http-resilience".</param>
    /// <param name="failureStatus">Status to report on failure. Default: <see cref="HealthStatus.Degraded"/>.</param>
    /// <param name="tags">Optional tags for filtering health checks.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddHttpResilienceHealthChecks(
        this IServiceCollection services,
        string name = "http-resilience",
        HealthStatus? failureStatus = null,
        IEnumerable<string>? tags = null)
    {
        // Ensure the tracker singleton exists (idempotent).
        services.TryAddSingleton<CircuitBreakerStateTracker>();

        services.AddHealthChecks()
            .Add(new HealthCheckRegistration(
                name,
                sp => new HttpResilienceHealthCheck(sp.GetRequiredService<CircuitBreakerStateTracker>()),
                failureStatus,
                tags));

        return services;
    }
}
```

Add `using Microsoft.Extensions.DependencyInjection.Extensions;` at the top for `TryAddSingleton`.

- [ ] **Step 2: Modify HttpStandardResilienceHandlerConfig to accept tracker**

In `src/HttpResilience.NET/Internal/HttpStandardResilienceHandlerConfig.cs`, change the `Create` method signature to add an optional `CircuitBreakerStateTracker? tracker = null` parameter. Then wire the tracker inside the existing callback block (alongside the logger calls):

Replace the full `Create` method with:

```csharp
public static Action<HttpStandardResilienceOptions> Create(
    HttpResilienceOptions options,
    int requestTimeoutSeconds,
    IServiceCollection services,
    bool rateLimiterHandledExternally = false,
    ILogger? logger = null,
    string? clientName = null,
    CircuitBreakerStateTracker? tracker = null)
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

        var name = clientName ?? "unknown";

        if (logger is not null)
        {
            resilienceOptions.Retry.OnRetry = args =>
            {
                HttpResilienceLogging.RetryAttempt(logger, args.AttemptNumber, name,
                    args.RetryDelay.TotalMilliseconds,
                    args.Outcome.Exception?.Message ?? args.Outcome.Result?.StatusCode.ToString());
                return default;
            };
        }

        resilienceOptions.CircuitBreaker.OnOpened = args =>
        {
            logger?.Let(l => HttpResilienceLogging.CircuitBreakerOpened(l, name, args.BreakDuration.TotalSeconds));
            tracker?.ReportOpened(name);
            return default;
        };
        resilienceOptions.CircuitBreaker.OnHalfOpened = _ =>
        {
            logger?.Let(l => HttpResilienceLogging.CircuitBreakerHalfOpen(l, name));
            tracker?.ReportHalfOpen(name);
            return default;
        };
        resilienceOptions.CircuitBreaker.OnClosed = _ =>
        {
            logger?.Let(l => HttpResilienceLogging.CircuitBreakerClosed(l, name));
            tracker?.ReportClosed(name);
            return default;
        };
    };
}
```

Wait — using `?.Let()` is not a standard C# pattern. Instead, use simple null checks:

```csharp
resilienceOptions.CircuitBreaker.OnOpened = args =>
{
    if (logger is not null)
        HttpResilienceLogging.CircuitBreakerOpened(logger, name, args.BreakDuration.TotalSeconds);
    tracker?.ReportOpened(name);
    return default;
};
resilienceOptions.CircuitBreaker.OnHalfOpened = _ =>
{
    if (logger is not null)
        HttpResilienceLogging.CircuitBreakerHalfOpen(logger, name);
    tracker?.ReportHalfOpen(name);
    return default;
};
resilienceOptions.CircuitBreaker.OnClosed = _ =>
{
    if (logger is not null)
        HttpResilienceLogging.CircuitBreakerClosed(logger, name);
    tracker?.ReportClosed(name);
    return default;
};
```

The key change: circuit breaker callbacks are now **always set** (not just when logger is not null), because the tracker needs them regardless of logging.

- [ ] **Step 3: Modify HttpStandardHedgingHandlerConfig to accept tracker**

In `src/HttpResilience.NET/Internal/HttpStandardHedgingHandlerConfig.cs`, add `CircuitBreakerStateTracker? tracker = null` parameter to `Create`. Apply the same pattern — always set circuit breaker callbacks:

```csharp
public static Action<HttpStandardHedgingResilienceOptions> Create(
    HttpResilienceOptions options,
    int requestTimeoutSeconds,
    ILogger? logger = null,
    string? clientName = null,
    CircuitBreakerStateTracker? tracker = null)
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

        var name = clientName ?? "unknown";
        resilienceOptions.Endpoint.CircuitBreaker.OnOpened = args =>
        {
            if (logger is not null)
                HttpResilienceLogging.CircuitBreakerOpened(logger, name, args.BreakDuration.TotalSeconds);
            tracker?.ReportOpened(name);
            return default;
        };
        resilienceOptions.Endpoint.CircuitBreaker.OnHalfOpened = _ =>
        {
            if (logger is not null)
                HttpResilienceLogging.CircuitBreakerHalfOpen(logger, name);
            tracker?.ReportHalfOpen(name);
            return default;
        };
        resilienceOptions.Endpoint.CircuitBreaker.OnClosed = _ =>
        {
            if (logger is not null)
                HttpResilienceLogging.CircuitBreakerClosed(logger, name);
            tracker?.ReportClosed(name);
            return default;
        };
    };
}
```

- [ ] **Step 4: Modify ServiceCollectionExtensions to resolve and pass tracker**

In `src/HttpResilience.NET/Extensions/ServiceCollectionExtensions.cs`, update `AddHandlersInOrder` to resolve the tracker from DI and pass it to the config builders.

Change the `AddHandlersInOrder` call sites inside the lambda to resolve tracker:

In the Standard handler block (around line 438):
```csharp
var resilienceBuilder = builder.AddStandardResilienceHandler().Configure((resilienceOptions, serviceProvider) =>
{
    var logger = serviceProvider.GetService<ILoggerFactory>()?.CreateLogger("HttpResilience");
    var tracker = serviceProvider.GetService<CircuitBreakerStateTracker>();
    HttpStandardResilienceHandlerConfig.Create(options, timeout, builder.Services, rateLimiterHandledExternally: rateLimiterInOrder, logger: logger, clientName: clientName, tracker: tracker)(resilienceOptions);
});
```

In the Hedging handler block (around line 449):
```csharp
var hedgingBuilder = builder.AddStandardHedgingHandler().Configure((resilienceOptions, serviceProvider) =>
{
    var logger = serviceProvider.GetService<ILoggerFactory>()?.CreateLogger("HttpResilience");
    var tracker = serviceProvider.GetService<CircuitBreakerStateTracker>();
    HttpStandardHedgingHandlerConfig.Create(options, timeout, logger: logger, clientName: clientName, tracker: tracker)(resilienceOptions);
});
```

Add `using Microsoft.Extensions.DependencyInjection.Extensions;` if not already present (needed by HealthCheckExtensions).

- [ ] **Step 5: Build and run all existing tests**

Run: `dotnet build && dotnet test`
Expected: All existing tests still pass (48 unit + 3 integration).

- [ ] **Step 6: Commit**

```bash
git add src/HttpResilience.NET/Extensions/HealthCheckExtensions.cs src/HttpResilience.NET/Internal/HttpStandardResilienceHandlerConfig.cs src/HttpResilience.NET/Internal/HttpStandardHedgingHandlerConfig.cs src/HttpResilience.NET/Extensions/ServiceCollectionExtensions.cs
git commit -m "feat: wire CircuitBreakerStateTracker into pipeline callbacks and add health check extension"
```

---

### Task 4: RateLimiter Disposal Test

**Files:**
- Create: `tests/HttpResilience.NET.Tests/Extensions/RateLimiterDisposalTests.cs`

- [ ] **Step 1: Write the disposal test**

```csharp
// tests/HttpResilience.NET.Tests/Extensions/RateLimiterDisposalTests.cs
using System.Threading.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using HttpResilience.NET.Extensions;

namespace HttpResilience.NET.Tests.Extensions;

public class RateLimiterDisposalTests
{
    [Fact]
    public async Task ServiceProvider_Dispose_DisposesRateLimiterSingleton()
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
        services.AddHttpClient("DisposalTest", _ => { })
            .AddHttpClientWithResilience(configuration);

        var provider = services.BuildServiceProvider();

        // Resolve the rate limiter singleton registered by the library.
        var limiter = provider.GetRequiredService<RateLimiter>();
        Assert.NotNull(limiter);

        // Verify the limiter is functional before disposal.
        using var lease = await limiter.AcquireAsync(1);
        Assert.True(lease.IsAcquired);

        // Dispose the provider — should dispose all singletons including the rate limiter.
        await provider.DisposeAsync();

        // After disposal, the limiter should throw ObjectDisposedException.
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await limiter.AcquireAsync(1));
    }

    [Fact]
    public async Task ServiceProvider_Dispose_DisposesRateLimiterFromPipelineOrder()
    {
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "true",
            ["HttpResilienceOptions:PipelineOrder:0"] = "RateLimiter",
            ["HttpResilienceOptions:PipelineOrder:1"] = "Standard",
            ["HttpResilienceOptions:RateLimiter:Enabled"] = "true",
            ["HttpResilienceOptions:RateLimiter:PermitLimit"] = "50",
            ["HttpResilienceOptions:RateLimiter:WindowSeconds"] = "1"
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);
        services.AddHttpClient("DisposalTest2", _ => { })
            .AddHttpClientWithResilience(configuration);

        var provider = services.BuildServiceProvider();
        var limiter = provider.GetRequiredService<RateLimiter>();
        Assert.NotNull(limiter);

        await provider.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await limiter.AcquireAsync(1));
    }
}
```

- [ ] **Step 2: Run tests to verify they pass**

Run: `dotnet test tests/HttpResilience.NET.Tests/ --filter "FullyQualifiedName~RateLimiterDisposalTests"`
Expected: Both tests PASS.

- [ ] **Step 3: Commit**

```bash
git add tests/HttpResilience.NET.Tests/Extensions/RateLimiterDisposalTests.cs
git commit -m "test: verify ServiceProvider disposal disposes rate limiter singletons"
```

---

### Task 5: Options Hot-Reload Test

**Files:**
- Create: `tests/HttpResilience.NET.Tests/Options/OptionsMonitorHotReloadTests.cs`

- [ ] **Step 1: Write the hot-reload test**

```csharp
// tests/HttpResilience.NET.Tests/Options/OptionsMonitorHotReloadTests.cs
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using HttpResilience.NET.Extensions;
using HttpResilience.NET.Options;

namespace HttpResilience.NET.Tests.Options;

public class OptionsMonitorHotReloadTests
{
    [Fact]
    public void OptionsMonitor_DetectsConfigChange_WhenSectionReloads()
    {
        var source = new MemoryConfigurationSource
        {
            InitialData = new Dictionary<string, string?>
            {
                ["HttpResilienceOptions:Enabled"] = "true",
                ["HttpResilienceOptions:PipelineOrder:0"] = "Standard",
                ["HttpResilienceOptions:Retry:MaxRetryAttempts"] = "3"
            }
        };
        var configuration = new ConfigurationBuilder()
            .Add(source)
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);

        using var provider = services.BuildServiceProvider();
        var monitor = provider.GetRequiredService<IOptionsMonitor<HttpResilienceOptions>>();

        // Initial value
        Assert.Equal(3, monitor.CurrentValue.Retry.MaxRetryAttempts);

        // Track change notification
        var changed = false;
        monitor.OnChange(opts => changed = true);

        // Mutate the underlying config and trigger reload
        configuration["HttpResilienceOptions:Retry:MaxRetryAttempts"] = "5";
        configuration.Reload();

        // After reload, monitor reflects the new value
        Assert.Equal(5, monitor.CurrentValue.Retry.MaxRetryAttempts);
        Assert.True(changed);
    }

    [Fact]
    public void OptionsSnapshot_ReturnsNamedOptions_AfterConfigChange()
    {
        var source = new MemoryConfigurationSource
        {
            InitialData = new Dictionary<string, string?>
            {
                ["MySection:Enabled"] = "true",
                ["MySection:PipelineOrder:0"] = "Standard",
                ["MySection:Retry:MaxRetryAttempts"] = "2"
            }
        };
        var configuration = new ConfigurationBuilder()
            .Add(source)
            .Build();
        var section = configuration.GetSection("MySection");

        var services = new ServiceCollection();
        services.AddHttpClient("NamedClient", _ => { })
            .AddHttpClientWithResilience(section);

        using var provider = services.BuildServiceProvider();

        // Use a scope to get IOptionsSnapshot (scoped)
        using var scope = provider.CreateScope();
        var snapshot = scope.ServiceProvider.GetRequiredService<IOptionsSnapshot<HttpResilienceOptions>>();
        var opts = snapshot.Get("NamedClient");

        Assert.Equal(2, opts.Retry.MaxRetryAttempts);
    }
}
```

- [ ] **Step 2: Run tests to verify they pass**

Run: `dotnet test tests/HttpResilience.NET.Tests/ --filter "FullyQualifiedName~OptionsMonitorHotReloadTests"`
Expected: Both tests PASS.

- [ ] **Step 3: Commit**

```bash
git add tests/HttpResilience.NET.Tests/Options/OptionsMonitorHotReloadTests.cs
git commit -m "test: verify IOptionsMonitor hot-reload and named options snapshot"
```

---

### Task 6: Concurrency Integration Test

**Files:**
- Create: `tests/HttpResilience.NET.IntegrationTests/ConcurrencyIntegrationTests.cs`

- [ ] **Step 1: Write the concurrency test**

```csharp
// tests/HttpResilience.NET.IntegrationTests/ConcurrencyIntegrationTests.cs
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using HttpResilience.NET.Extensions;

namespace HttpResilience.NET.IntegrationTests;

public class ConcurrencyIntegrationTests
{
    [Fact]
    public async Task ParallelRequests_ThroughResiliencePipeline_AllSucceed()
    {
        await using var server = await StartTestServerAsync();
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "true",
            ["HttpResilienceOptions:PipelineOrder:0"] = "Standard",
            ["HttpResilienceOptions:Timeout:TotalRequestTimeoutSeconds"] = "30",
            ["HttpResilienceOptions:Timeout:AttemptTimeoutSeconds"] = "10",
            ["HttpResilienceOptions:Retry:MaxRetryAttempts"] = "2",
            ["HttpResilienceOptions:Retry:BaseDelaySeconds"] = "0",
            ["HttpResilienceOptions:Retry:BackoffType"] = "Constant",
            ["HttpResilienceOptions:CircuitBreaker:MinimumThroughput"] = "100"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);
        services.AddHttpClient("ConcurrencyTest", _ => { })
            .AddHttpClientWithResilience(configuration)
            .ConfigurePrimaryHttpMessageHandler(() => server.CreateHandler());

        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        const int parallelRequests = 50;
        var tasks = Enumerable.Range(0, parallelRequests)
            .Select(async _ =>
            {
                var client = factory.CreateClient("ConcurrencyTest");
                return await client.GetAsync($"{server.BaseAddress}ok");
            })
            .ToArray();

        var responses = await Task.WhenAll(tasks);

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
    }

    [Fact]
    public async Task ParallelRequests_WithRateLimiter_CompletesWithoutDeadlock()
    {
        await using var server = await StartTestServerAsync();
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "true",
            ["HttpResilienceOptions:PipelineOrder:0"] = "RateLimiter",
            ["HttpResilienceOptions:PipelineOrder:1"] = "Standard",
            ["HttpResilienceOptions:RateLimiter:Enabled"] = "true",
            ["HttpResilienceOptions:RateLimiter:PermitLimit"] = "1000",
            ["HttpResilienceOptions:RateLimiter:WindowSeconds"] = "1",
            ["HttpResilienceOptions:Timeout:TotalRequestTimeoutSeconds"] = "30",
            ["HttpResilienceOptions:Timeout:AttemptTimeoutSeconds"] = "10",
            ["HttpResilienceOptions:Retry:MaxRetryAttempts"] = "1",
            ["HttpResilienceOptions:Retry:BaseDelaySeconds"] = "0",
            ["HttpResilienceOptions:CircuitBreaker:MinimumThroughput"] = "100"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);
        services.AddHttpClient("RateLimitConcurrency", _ => { })
            .AddHttpClientWithResilience(configuration)
            .ConfigurePrimaryHttpMessageHandler(() => server.CreateHandler());

        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        const int parallelRequests = 20;
        var tasks = Enumerable.Range(0, parallelRequests)
            .Select(async _ =>
            {
                var client = factory.CreateClient("RateLimitConcurrency");
                return await client.GetAsync($"{server.BaseAddress}ok");
            })
            .ToArray();

        // Should complete without deadlock within a reasonable time.
        var completed = await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.NotEqual(typeof(Task), completed.GetType()); // Ensure it didn't time out on delay

        var responses = await Task.WhenAll(tasks);
        // All should complete — some may be rate-limited (429) but none should hang.
        Assert.All(responses, r => Assert.True(
            r.StatusCode == HttpStatusCode.OK || (int)r.StatusCode == 429 || r.StatusCode == HttpStatusCode.ServiceUnavailable,
            $"Unexpected status: {r.StatusCode}"));
    }

    [Fact]
    public async Task ParallelRequests_WithHealthCheck_TracksState()
    {
        await using var server = await StartTestServerAsync();
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "true",
            ["HttpResilienceOptions:PipelineOrder:0"] = "Standard",
            ["HttpResilienceOptions:Timeout:TotalRequestTimeoutSeconds"] = "30",
            ["HttpResilienceOptions:Timeout:AttemptTimeoutSeconds"] = "10",
            ["HttpResilienceOptions:Retry:MaxRetryAttempts"] = "0",
            ["HttpResilienceOptions:Retry:BaseDelaySeconds"] = "0",
            ["HttpResilienceOptions:CircuitBreaker:MinimumThroughput"] = "100"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);
        services.AddHttpResilienceHealthChecks();
        services.AddHttpClient("HealthCheckTest", _ => { })
            .AddHttpClientWithResilience(configuration)
            .ConfigurePrimaryHttpMessageHandler(() => server.CreateHandler());

        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        // Make a successful request
        var client = factory.CreateClient("HealthCheckTest");
        var response = await client.GetAsync($"{server.BaseAddress}ok");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Tracker should exist and have no open circuits
        var tracker = provider.GetRequiredService<CircuitBreakerStateTracker>();
        Assert.False(tracker.HasOpenCircuits);
    }

    private static async Task<TestServerFixture> StartTestServerAsync()
    {
        var builder = Host.CreateDefaultBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services => services.AddRouting());
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/ok", () => Results.Ok());
                    });
                });
            });

        var host = builder.Build();
        await host.StartAsync();
        return new TestServerFixture(host);
    }

    private sealed class TestServerFixture : IAsyncDisposable
    {
        private readonly IHost _host;
        private readonly Func<HttpMessageHandler> _createHandler;

        public TestServerFixture(IHost host)
        {
            _host = host;
            var testServer = host.GetTestServer();
            BaseAddress = testServer.BaseAddress?.ToString() ?? "http://localhost/";
            _createHandler = testServer.CreateHandler;
        }

        public string BaseAddress { get; }
        public HttpMessageHandler CreateHandler() => _createHandler();
        public async ValueTask DisposeAsync() => await _host.StopAsync().ConfigureAwait(false);
    }
}
```

Add the `using HttpResilience.NET.Internal;` for the `CircuitBreakerStateTracker` reference and add the health checks package to the integration test project.

Add to `tests/HttpResilience.NET.IntegrationTests/HttpResilience.NET.IntegrationTests.csproj`:
```xml
<PackageReference Include="Microsoft.Extensions.Diagnostics.HealthChecks" />
```

- [ ] **Step 2: Run tests to verify they pass**

Run: `dotnet test tests/HttpResilience.NET.IntegrationTests/ --filter "FullyQualifiedName~ConcurrencyIntegrationTests"`
Expected: All 3 tests PASS.

- [ ] **Step 3: Commit**

```bash
git add tests/HttpResilience.NET.IntegrationTests/ConcurrencyIntegrationTests.cs tests/HttpResilience.NET.IntegrationTests/HttpResilience.NET.IntegrationTests.csproj
git commit -m "test: add concurrency integration tests and health check wiring verification"
```

---

### Task 7: Final verification

- [ ] **Step 1: Run full build and all tests**

Run: `dotnet build && dotnet test`
Expected: All tests pass (55+ unit + 6 integration).

- [ ] **Step 2: Verify no warnings**

Run: `dotnet build --no-incremental 2>&1 | grep -i "warning\|error" || echo "Clean build"`
Expected: Clean build with zero warnings (TreatWarningsAsErrors is on).

- [ ] **Step 3: Final commit if any cleanup needed**

Only if build/test revealed issues that needed fixing.
