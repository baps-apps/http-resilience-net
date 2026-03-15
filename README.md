# HttpResilience.NET

Shared .NET package for HTTP client resilience: options, `SocketsHttpHandler` factory, and extensions to add resilience to `HttpClient`. Pipeline type is chosen via config (**PipelineType**: Standard or Hedging). Supports optional **rate limiting** (standard and hedging), **fallback** (synthetic or custom via `IHttpFallbackHandler`), **bulkhead**, configurable **pipeline order**, **per-authority** pipeline selection, and a **custom pipeline** delegate for extra strategies.

Consumer solutions reference **HttpResilience.NET NuGet package** (from a feed or local nupkg), not a project reference.

## Table of contents

- [Benefits](#benefits)
- [Pipeline types](#pipeline-types)
- [Installation](#installation)
- [Quick Start](#quick-start)
- [Configuration](#configuration)
- [Options reference](#options-reference)
- [Telemetry](#telemetry)
- [Operations and docs](#operations-and-docs)
- [Versioning and compatibility](#versioning-and-compatibility)

## Benefits

This package is mainly a **standardization + maintenance win**: one validated configuration schema and one implementation reused across services.

- **Duplicate code removed**: ~**150–400 lines** per service (DI registration, options binding + validation, standard/hedging handler wiring, rate limiter/fallback/bulkhead toggles, pipeline order/selection glue).
- **Duplicate configuration removed**: ~**30–80 lines** of repeated `appsettings.json` resilience blocks per service, replaced by a consistent shared schema.
- **Duplication across a fleet**: for **10 services**, that’s typically **1,500–4,000 fewer LOC** and **300–800 fewer config lines** to maintain.
- **Operational consistency**: one implementation means fewer “almost-the-same” pipelines (different defaults, missing jitter, inconsistent timeouts) and faster rollouts for policy changes.
- **Feature-flag resilience**: set **Enabled** to **false** to disable resilience without changing application code (helps during incidents and troubleshooting).

For detailed implementation logic, use cases per option, and comparison with hand-rolled setups, see [docs/IMPLEMENTATION.md](docs/IMPLEMENTATION.md) and [docs/COMPARISON.md](docs/COMPARISON.md).

## Pipeline types


| Type         | Description                                                                                                                                   |
| ------------ | --------------------------------------------------------------------------------------------------------------------------------------------- |
| **Standard** | Timeout, retry, circuit breaker, optional rate limiting. Single request per attempt; retries on transient failure. Use for most APIs.         |
| **Hedging**  | Multiple requests (hedged attempts), first success wins; optional rate limiting. Use for tail-latency sensitive calls to replicated backends. |


Optional features (each has its own `Enabled` in its section):

- **RateLimiter** – Polly rate limiter (FixedWindow / SlidingWindow / TokenBucket) around the inner or hedging handler.
- **Fallback** – return a synthetic response on total failure, or use a custom **IHttpFallbackHandler**.
- **Bulkhead** – Polly concurrency limiter to cap concurrent outbound requests.

**PipelineSelection:Mode**: `None` (default) or **ByAuthority** for a separate pipeline instance per request authority (scheme + host + port), e.g. to keep circuit breakers isolated per host.

When **Enabled** is `false`, the extensions do nothing and the builder is returned unchanged (no resilience pipeline, no custom primary handler).

## Installation

### Step 1: Add the package

Add a **PackageReference** to HttpResilience.NET in your project (or use your NuGet feed). If you use GitHub Packages:

```bash
dotnet nuget add source https://nuget.pkg.github.com/YOUR_ORG/index.json \
  --name github \
  --username YOUR_GITHUB_USERNAME \
  --password YOUR_GITHUB_PAT \
  --store-password-in-clear-text
```

Create a PAT at: `https://github.com/settings/tokens` (requires `read:packages` permission).

### Step 2: Install package

```bash
dotnet add package HttpResilience.NET --source github
```

Or add a **PackageReference** in your `.csproj`. Consumers reference the **HttpResilience.NET NuGet package**, not a project reference.

## Quick Start

### 1. Configure `appsettings.json`

Minimal configuration with resilience enabled (default pipeline: Standard):

```json
{
  "HttpResilienceOptions": {
    "Enabled": true,
    "PipelineType": "Standard",
    "Connection": {
      "MaxConnectionsPerServer": 10,
      "ConnectTimeoutSeconds": 21
    },
    "Timeout": {
      "TotalRequestTimeoutSeconds": 30,
      "AttemptTimeoutSeconds": 10
    },
    "Retry": {
      "MaxRetryAttempts": 3,
      "BaseDelaySeconds": 2,
      "BackoffType": "Exponential",
      "UseJitter": true
    },
    "CircuitBreaker": {
      "MinimumThroughput": 100,
      "FailureRatio": 0.1,
      "SamplingDurationSeconds": 30,
      "BreakDurationSeconds": 5
    }
  }
}
```

Set **Enabled** to **true** to enable HTTP resilience. All other properties are optional; defaults are applied when not provided. For full options and examples (rate limiter, fallback, bulkhead, hedging), see the **Configuration** section below.

### 2. Add to `Program.cs`

```csharp
using HttpResilience.NET.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Register options (once per app)
builder.Services.AddHttpResilienceOptions(builder.Configuration);

// Optional but recommended for production: telemetry (enriches metrics with error.type, request.name, request.dependency.name)
builder.Services.AddHttpResilienceTelemetry();

// Named client with resilience (pipeline type from config: Standard or Hedging)
builder.Services.AddHttpClient("MyClient", client => { /* optional */ })
    .AddHttpClientWithResilience(builder.Configuration, requestTimeoutSeconds: 30);

builder.Services.AddControllers();

var app = builder.Build();

app.MapControllers();

app.Run();
```

Inject `**IHttpClientFactory**` and create the named client where you need it:

```csharp
var client = httpClientFactory.CreateClient("MyClient");
// use client as usual
```

### 3. Run and verify

```bash
dotnet run
```

### 4. Sample app

The solution includes a minimal console sample in `**samples/HttpResilience.NET.Sample**`:

- Reads **HttpResilienceOptions** from `appsettings.json`.
- Registers options and telemetry (`AddHttpResilienceOptions`, `AddHttpResilienceTelemetry`).
- Registers a named `HttpClient` and sends a single request, logging the status code.

Run from the repository root:

```bash
dotnet run --project samples/HttpResilience.NET.Sample
```

Modify the sample `appsettings.json` (timeouts, retries, circuit breaker, etc.) to observe different behaviors in logs and telemetry.

## Configuration

Use the **HttpResilienceOptions** section. Options are grouped by feature: **Connection**, **Timeout**, **Retry**, **CircuitBreaker**, **RateLimiter**, **Fallback**, **Hedging**, **Bulkhead**. Nested keys use the section name (e.g. `Connection:MaxConnectionsPerServer`).

### Example: full schema (Standard pipeline with optional features)

```json
{
  "HttpResilienceOptions": {
    "Enabled": true,
    "PipelineType": "Standard",
    "PipelineOrder": "FallbackThenConcurrency",
    "Connection": {
      "MaxConnectionsPerServer": 10,
      "PooledConnectionIdleTimeoutSeconds": 120,
      "PooledConnectionLifetimeSeconds": 600,
      "ConnectTimeoutSeconds": 21
    },
    "Timeout": {
      "TotalRequestTimeoutSeconds": 30,
      "AttemptTimeoutSeconds": 10
    },
    "Retry": {
      "MaxRetryAttempts": 3,
      "BaseDelaySeconds": 2,
      "BackoffType": "Exponential",
      "UseJitter": true,
      "UseRetryAfterHeader": true
    },
    "CircuitBreaker": {
      "MinimumThroughput": 100,
      "FailureRatio": 0.1,
      "SamplingDurationSeconds": 30,
      "BreakDurationSeconds": 5
    },
    "RateLimiter": { "Enabled": false, "PermitLimit": 1000, "WindowSeconds": 1, "QueueLimit": 0, "Algorithm": "FixedWindow" },
    "Fallback": { "Enabled": false, "StatusCode": 503, "OnlyOn5xx": false, "ResponseBody": null },
    "Hedging": { "DelaySeconds": 2, "MaxHedgedAttempts": 1 },
    "Bulkhead": { "Enabled": false, "Limit": 100, "QueueLimit": 0 }
  }
}
```

Optional **PipelineStrategyOrder** (array): order of outer strategies, e.g. `[ "Fallback", "Bulkhead", "RateLimiter", "Standard" ]`. When set, overrides **PipelineOrder**. Handlers are added so the **first element is outermost**.

**Binding from a specific section** (e.g. multi-tenant):

```csharp
var tenantSection = configuration.GetSection("HttpResilienceOptions:TenantA");
services.AddHttpClient("TenantAClient", _ => { })
    .AddHttpClientWithResilience(tenantSection);
```

Ranges and allowed values are validated at startup when using `AddHttpResilienceOptions`. Full option details and use cases: [docs/IMPLEMENTATION.md](docs/IMPLEMENTATION.md).

## Options reference

This table maps the config schema to what `AddHttpClientWithResilience(...)` configures and when you typically use it.


| Option / section                                | What it configures                                                                                                                 | Typical usage                                                                            |
| ----------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------- |
| `Enabled`                                       | If `false`, no resilience pipeline. If `true`, applies primary handler + resilience handlers.                                      | Feature-flag resilience per environment/service.                                         |
| `PipelineType` (`Standard`/`Hedging`)           | Chooses standard vs hedging handler (timeout, retry, circuit breaker; hedging = multiple requests, first success wins).            | Standard for most APIs; Hedging for tail-latency sensitive calls to replicated backends. |
| `PipelineOrder`                                 | Relative order of **Fallback** and **Bulkhead** when both enabled (legacy).                                                        | Keep defaults unless you need bulkhead outermost vs fallback outermost.                  |
| `PipelineStrategyOrder` (array)                 | Explicit outer strategy order: `Fallback`, `Bulkhead`, `RateLimiter`, and one of `Standard`/`Hedging`. First element is outermost. | Full control of outer handler ordering without code changes.                             |
| `PipelineSelection:Mode` (`None`/`ByAuthority`) | When `ByAuthority`, separate pipeline instances per authority (scheme+host+port).                                                  | One `HttpClient` calling many hosts; isolate circuit breakers per host.                  |
| `Connection:`*                                  | Primary `SocketsHttpHandler` (pool, timeouts, `ConnectTimeout`).                                                                   | Connection pool tuning and faster failure on connect hangs.                              |
| `Timeout:TotalRequestTimeoutSeconds`            | Total operation timeout (all attempts/retries).                                                                                    | Ensure callers never wait longer than a fixed bound.                                     |
| `Timeout:AttemptTimeoutSeconds`                 | Per-attempt timeout.                                                                                                               | Prevent a single attempt from consuming the entire total timeout.                        |
| `Retry:`*                                       | HTTP retry strategy (attempt count, delay/backoff, jitter, `Retry-After` header).                                                  | Transient faults, throttling, flaky dependencies.                                        |
| `CircuitBreaker:`*                              | HTTP circuit breaker (failure ratio, throughput, sampling/break duration).                                                         | Fail fast when a dependency is unhealthy and give it time to recover.                    |
| `RateLimiter:Enabled` + `RateLimiter:`*         | Polly rate limiter (FixedWindow/SlidingWindow/TokenBucket) around inner/hedging handler.                                           | Enforce quotas and prevent self-throttling / downstream overload.                        |
| `Fallback:Enabled` + `Fallback:*`               | Polly fallback; custom `IHttpFallbackHandler` runs first if provided, else synthetic response.                                     | Serve cached/default responses or degrade gracefully on total failure.                   |
| `Hedging:*`                                     | Hedging delay + max hedged attempts.                                                                                               | Reduce tail latency by racing replicas.                                                  |
| `Bulkhead:Enabled` + `Bulkhead:*`               | Polly concurrency limiter.                                                                                                         | Stop one hot dependency from consuming all outbound concurrency.                         |


### Advanced: custom fallback and pipeline

**Custom fallback handler:** Pass an `IHttpFallbackHandler` instance (e.g. resolve from DI when you have `IServiceProvider`, or pass `new MyFallbackHandler()` if stateless):

```csharp
// Example: pass a concrete instance (or resolve from DI when configuring the client)
var fallbackHandler = new MyFallbackHandler(); // or get from DI
services.AddHttpClient("MyClient", _ => { })
    .AddHttpClientWithResilience(builder.Configuration, requestTimeoutSeconds: null, fallbackHandler: fallbackHandler);
```

**Custom pipeline** (extra strategies outermost): Pass `configurePipeline` to add handlers after the built-in pipeline. Use the overload that includes `requestTimeoutSeconds` and `fallbackHandler` when needed:

```csharp
services.AddHttpClient("MyClient", _ => { })
    .AddHttpClientWithResilience(builder.Configuration, requestTimeoutSeconds: null, fallbackHandler: null, configurePipeline: b => b.AddResilienceHandler("custom", rb => { /* ... */ }));
```

**Custom inner pipeline** (full control via code; options like `PipelineOrder` / `PipelineStrategyOrder` are not applied):

```csharp
services.AddHttpClient("MyClient", _ => { })
    .AddHttpClientWithResilience(
        builder.Configuration,
        requestTimeoutSeconds: 30,
        fallbackHandler: null,
        configureInnerPipeline: inner =>
        {
            inner
                .AddRetry(new HttpRetryStrategyOptions { /* ... */ })
                .AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions { /* ... */ })
                .AddTimeout(new HttpTimeoutStrategyOptions { Timeout = TimeSpan.FromSeconds(30) });
        });
```

For **per-tenant or per-client** connection/timeout when using a custom inner pipeline, use the overload that accepts **IConfigurationSection** so the primary handler is built from that section: `AddHttpClientWithResilience(tenantSection, requestTimeoutSeconds: null, fallbackHandler: null, configureInnerPipeline: inner => { ... })`.

## Telemetry

Register `**AddHttpResilienceTelemetry()**` to enable metrics and diagnostics for resilience pipelines. The package uses the same telemetry abstractions as Microsoft.Extensions.Http.Resilience (e.g. meters and enrichers). See [docs/IMPLEMENTATION.md](docs/IMPLEMENTATION.md) for emitted metrics and tags.

## Operations and docs

- **Building and packing:** From the solution directory run `dotnet build` and `dotnet pack -c Release -o ./nupkgs`.
- This package configures **outgoing** HTTP client resilience only. Incoming request limits (Kestrel, FormOptions, etc.) are not part of this package.

For operations runbooks, versioning policy, security/governance, recipes, troubleshooting, and production readiness:

- [docs/OPERATIONS.md](docs/OPERATIONS.md)
- [docs/RUNBOOK.md](docs/RUNBOOK.md) – What to do when resilience alerts fire
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) – Pipeline overview and sequence diagrams
- [docs/VERSIONING.md](docs/VERSIONING.md)
- [docs/RECIPES.md](docs/RECIPES.md)
- [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md)
- [docs/SECURITY-GOVERNANCE.md](docs/SECURITY-GOVERNANCE.md)
- [docs/PRODUCTION-CHECKLIST.md](docs/PRODUCTION-CHECKLIST.md)

## Versioning and compatibility

- HttpResilience.NET follows **Semantic Versioning**:
  - **MAJOR:** breaking API/behavior changes.
  - **MINOR:** new features and configuration options, backwards compatible.
  - **PATCH:** bug fixes and internal improvements only.
- The library targets **.NET 10** (`net10.0`) for the core package, tests, and sample. See [docs/VERSIONING.md](docs/VERSIONING.md) for details.

