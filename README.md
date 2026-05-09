# HttpResilience.NET

Shared .NET package for HTTP client resilience: options, `SocketsHttpHandler` factory, and extensions to add resilience to `HttpClient`. Pipeline behaviour is driven by a single **PipelineOrder** list (e.g. `["Fallback", "Bulkhead", "RateLimiter", "Standard"]`). Supports optional **rate limiting**, **fallback** (synthetic or custom via `IHttpFallbackHandler`), **bulkhead**, **per-authority** pipeline selection, **health checks**, and a **custom pipeline** delegate for extra strategies.

Consumer solutions reference **HttpResilience.NET NuGet package** (from a feed or local nupkg), not a project reference.

## Table of contents

- [Benefits](#benefits)
- [Pipeline types](#pipeline-types)
- [Installation](#installation)
- [Quick Start](#quick-start)
- [Configuration](#configuration)
- [Options reference](#options-reference)
- [Telemetry](#telemetry)
- [Rate limiter scope across multiple HttpClients](#rate-limiter-scope-across-multiple-httpclients)
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

Include `"Standard"` or `"Hedging"` (exactly one) in the **PipelineOrder** list. Optional features (each has its own `Enabled` in its section):

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

Minimal configuration with resilience enabled:

```json
{
  "HttpResilienceOptions": {
    "Enabled": true,
    "PipelineOrder": ["Standard"],
    "Connection": {
      "Enabled": true,
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

Set **Enabled** to **true** and provide a **PipelineOrder** list with at least `"Standard"` or `"Hedging"`. All other properties are optional with sensible defaults. For full options and examples (rate limiter, fallback, bulkhead, hedging), see the **Configuration** section below.

### 2. Add to `Program.cs`

```csharp
using HttpResilience.NET.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Register options (once per app)
builder.Services.AddHttpResilienceOptions(builder.Configuration);

// Optional but recommended for production: telemetry (enriches metrics with error.type, request.name, request.dependency.name)
builder.Services.AddHttpResilienceTelemetry();

// Optional: health checks for circuit breaker state
builder.Services.AddHttpResilienceHealthChecks();

// Named client with resilience (pipeline from config PipelineOrder)
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
    "PipelineOrder": ["Fallback", "Bulkhead", "RateLimiter", "Standard"],
    "Connection": {
      "Enabled": true,
      "MaxConnectionsPerServer": 10,
      "PooledConnectionIdleTimeoutSeconds": 120,
      "PooledConnectionLifetimeSeconds": 600,
      "ConnectTimeoutSeconds": 21,
      "EnableMultipleHttp2Connections": true
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
    "RateLimiter": { "Enabled": true, "PermitLimit": 1000, "WindowSeconds": 1, "QueueLimit": 0, "Algorithm": "FixedWindow" },
    "Fallback": { "Enabled": true, "StatusCode": 503, "OnlyOn5xx": false, "ResponseBody": null },
    "Hedging": { "DelaySeconds": 2, "MaxHedgedAttempts": 1 },
    "Bulkhead": { "Enabled": true, "Limit": 100, "QueueLimit": 0 }
  }
}
```

**PipelineOrder** is a list of strategy names from outermost to innermost: `"Fallback"`, `"Bulkhead"`, `"RateLimiter"`, and exactly one of `"Standard"` or `"Hedging"`. The first element is outermost (executes first). Optional strategies are only added when their `Enabled` flag is `true`.

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
| `PipelineOrder` (array)                         | Strategy order outermost→innermost: `Fallback`, `Bulkhead`, `RateLimiter`, and exactly one of `Standard`/`Hedging`.               | `["Fallback", "Bulkhead", "RateLimiter", "Standard"]`. Required when `Enabled = true`.  |
| `PipelineSelection:Mode` (`None`/`ByAuthority`) | When `ByAuthority`, separate pipeline instances per authority (scheme+host+port).                                                  | One `HttpClient` calling many hosts; isolate circuit breakers per host.                  |
| `Connection:`*                                  | Primary `SocketsHttpHandler` (pool, timeouts, `ConnectTimeout`, HTTP/2 multi-connection).                                          | Connection pool tuning, faster failure on connect hangs, and HTTP/2 throughput scaling.  |
| `Timeout:TotalRequestTimeoutSeconds`            | Total operation timeout (all attempts/retries).                                                                                    | Ensure callers never wait longer than a fixed bound.                                     |
| `Timeout:AttemptTimeoutSeconds`                 | Per-attempt timeout.                                                                                                               | Prevent a single attempt from consuming the entire total timeout.                        |
| `Retry:`*                                       | HTTP retry strategy (attempt count, delay/backoff, jitter, `Retry-After` header).                                                  | Transient faults, throttling, flaky dependencies.                                        |
| `CircuitBreaker:`*                              | HTTP circuit breaker (failure ratio, throughput, sampling/break duration).                                                         | Fail fast when a dependency is unhealthy and give it time to recover.                    |
| `RateLimiter:Enabled` + `RateLimiter:`*         | Polly rate limiter (FixedWindow/SlidingWindow/TokenBucket) around inner/hedging handler.                                           | Enforce quotas and prevent self-throttling / downstream overload.                        |
| `Fallback:Enabled` + `Fallback:*`               | Polly fallback; custom `IHttpFallbackHandler` runs first if provided, else synthetic response.                                     | Serve cached/default responses or degrade gracefully on total failure.                   |
| `Hedging:*`                                     | Hedging delay + max hedged attempts (when `PipelineOrder` contains `"Hedging"`).                                                   | Reduce tail latency by racing replicas.                                                  |
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

**Custom inner pipeline** (full control via code; `PipelineOrder` is not applied):

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

Register `AddHttpResilienceTelemetry()` to enable metrics enrichment (`error.type`, `request.name`, `request.dependency.name`) on Polly metrics. Register `AddHttpResilienceHealthChecks()` to expose aggregate circuit breaker state (Healthy/Degraded) via ASP.NET health checks. See [docs/OPERATIONS.md](docs/OPERATIONS.md) for dashboards and alerts.

## Rate limiter scope across multiple HttpClients

When your application talks to several independent downstream services (e.g. **SSO**, **MIS**, **ASM**, **MDS**), you have to decide whether to share one `RateLimiter` across all of them or give each `HttpClient` its own.

> **Recommendation: one limiter per `HttpClient`.** This is also the library default — each named client registered with rate limiting enabled gets its own keyed-singleton `RateLimiter` (key = the `HttpClient` name). The DI container owns the lifetime and disposes them on shutdown.

### Why per-client (separate) is the right default

| Concern | Shared limiter | Separate limiter (per `HttpClient`) |
| --- | --- | --- |
| **Per-service capacity** | One global cap can't reflect SSO=200 rps, MIS=50 rps, MDS=80 rps simultaneously — you either over-throttle the fast ones or over-pressure the slow ones. | Each downstream gets a `PermitLimit` that matches its actual throughput contract. |
| **Failure isolation** | A slow MIS fills the shared queue → unrelated SSO/ASM calls block behind it (cascading degradation). | A burst or slowdown on one downstream does not affect callers of the others. |
| **Independent tuning** | Changing one knob affects all four services. | Tune SSO without touching MDS. |
| **Symmetry with the circuit breaker** | Circuit breakers are already per-client; mixing scopes makes ops confusing. | Rate limiter and circuit breaker share the same scope and metrics dimension. |
| **Telemetry granularity** | A shared `available_permits` metric can't tell you which downstream is hot. | Per-client metrics show exactly which dependency is saturated. |
| **Backpressure semantics** | One client's burst can starve another client's permits. | Backpressure is contained to the client that caused it. |

### When a single shared limiter actually makes sense

Rare. Consider it only when:

- All `HttpClient`s hit **the same physical backend** (e.g. all calls go through the same API gateway and the gateway enforces a single quota). Even then, prefer enforcing on the gateway.
- You have a hard **process-wide outbound budget** (paid per request, fixed `$/sec` cap). A shared `TokenBucket` reflects that budget directly.
- All downstreams are homogeneous and cheap, and one cap is sufficient.

For typical enterprise scenarios with distinct backends (SSO/MIS/ASM/MDS), none of these apply.

### Recommended configuration shape

Bind each named client to its own configuration section:

```jsonc
{
  "HttpResilienceOptions:SSO": {
    "Enabled": true,
    "PipelineOrder": ["RateLimiter", "Standard"],
    "RateLimiter": { "Enabled": true, "PermitLimit": 200, "WindowSeconds": 1, "QueueLimit": 50, "Algorithm": "FixedWindow" }
  },
  "HttpResilienceOptions:MIS": {
    "Enabled": true,
    "PipelineOrder": ["RateLimiter", "Standard"],
    "RateLimiter": { "Enabled": true, "PermitLimit": 50,  "WindowSeconds": 1, "QueueLimit": 20, "Algorithm": "FixedWindow" }
  },
  "HttpResilienceOptions:ASM": {
    "Enabled": true,
    "PipelineOrder": ["RateLimiter", "Standard"],
    "RateLimiter": { "Enabled": true, "PermitLimit": 100, "WindowSeconds": 1, "QueueLimit": 30, "Algorithm": "FixedWindow" }
  },
  "HttpResilienceOptions:MDS": {
    "Enabled": true,
    "PipelineOrder": ["RateLimiter", "Standard"],
    "RateLimiter": { "Enabled": true, "PermitLimit": 80,  "WindowSeconds": 1, "QueueLimit": 25, "Algorithm": "FixedWindow" }
  }
}
```

```csharp
services.AddHttpResilienceOptions(configuration);

services.AddHttpClient("SSO").AddHttpClientWithResilience(configuration.GetSection("HttpResilienceOptions:SSO"));
services.AddHttpClient("MIS").AddHttpClientWithResilience(configuration.GetSection("HttpResilienceOptions:MIS"));
services.AddHttpClient("ASM").AddHttpClientWithResilience(configuration.GetSection("HttpResilienceOptions:ASM"));
services.AddHttpClient("MDS").AddHttpClientWithResilience(configuration.GetSection("HttpResilienceOptions:MDS"));
```

Each client now has its own keyed `RateLimiter`. To inspect or operate on a limiter directly:

```csharp
var ssoLimiter = serviceProvider.GetRequiredKeyedService<RateLimiter>("SSO");
var stats = ssoLimiter.GetStatistics();
```

### Rate limiter vs bulkhead — different questions

These two strategies answer different questions and can be combined:

- **Rate limiter** — *"how fast am I allowed to talk to this service?"* → scope **per downstream** (per `HttpClient`).
- **Bulkhead** — *"how much of my own capacity am I willing to spend on outbound calls?"* → scope is typically **per client**, but a process-wide bulkhead at a higher layer is a reasonable additional cap if you need to protect your own thread pool / sockets across all outbound traffic.

### Sizing rules of thumb

- `PermitLimit` ≈ the throughput the downstream guarantees you, with 10–20 % headroom.
- `WindowSeconds` = `1` for steady traffic; longer if the downstream documents per-minute quotas.
- `QueueLimit` should be small. Long queues hide failures and amplify p99 latency. If the queue is persistently full, raise downstream capacity or shed load — do not grow the queue.
- For bursty traffic prefer `TokenBucket` (smooths bursts) over `FixedWindow` (boundary spikes).

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

