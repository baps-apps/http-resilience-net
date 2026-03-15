# `AddStandardResilienceHandler()` vs `AddHttpClientWithResilience()` — Deep Comparison

This document provides a detailed side-by-side comparison of Microsoft's built-in `AddStandardResilienceHandler()` (from `Microsoft.Extensions.Http.Resilience`) and the `AddHttpClientWithResilience()` extension provided by this library.

---

## 1. Configuration Complexity

| Dimension | `AddStandardResilienceHandler()` | `AddHttpClientWithResilience()` |
|---|---|---|
| **Setup** | Single call, programmatic delegate | Two-step: `AddHttpResilienceOptions()` + `AddHttpClientWithResilience()` |
| **Source of truth** | Code (`Action<HttpStandardResilienceOptions>`) | Config file (`appsettings.json`) — no code change required to tune |
| **Environment-specific tuning** | Requires code branches or manual `IOptions<T>` wiring | Native — different `appsettings.{env}.json` drives different behaviour |
| **Multi-tenant / per-client** | Not built-in; requires separate registrations | First-class — `IConfigurationSection` overloads bind a different section per named client |
| **Master switch** | None — removing the call is the only "off" | `Enabled: false` → no-op; zero handlers added, zero DI side effects |
| **Pipeline strategy ordering** | Fixed internal order (timeout → retry → CB → rate limiter) | Explicit `PipelineStrategyOrder: ["Fallback","Bulkhead","RateLimiter","Standard"]`, outermost→innermost |
| **Legacy ordering** | N/A | `PipelineOrder` enum (`FallbackThenConcurrency` / `ConcurrencyThenFallback`) as a backwards-compatible fallback |
| **Custom inner pipeline escape hatch** | The delegate *is* the escape hatch | `configureInnerPipeline` delegate — gives full `ResiliencePipelineBuilder<HttpResponseMessage>` access while keeping `SocketsHttpHandler` wiring and total-timeout wrapper |

**When to choose Microsoft's API:** Purely in-code scenarios, single app, simple pipeline.

**When to choose `AddHttpClientWithResilience()`:** Config-driven, multi-environment, multi-tenant applications where changing retry counts should not require a redeploy.

---

## 2. Resilience Strategy Coverage

| Strategy | `AddStandardResilienceHandler()` | `AddHttpClientWithResilience()` |
|---|---|---|
| **Total timeout** | Yes (configurable) | Yes — `TotalRequestTimeoutSeconds`, per-client override via `requestTimeoutSeconds` param |
| **Attempt timeout** | Yes | Yes — `AttemptTimeoutSeconds` |
| **Retry** | Yes (count, delay, backoff, jitter, Retry-After) | Yes — same, fully config-driven; `UseRetryAfterHeader` honours 429/503 header |
| **Circuit breaker** | Yes (failure ratio, throughput, sampling, break duration) | Yes — all four CB knobs exposed from config |
| **Rate limiter** | Optional, single instance, code-only | Yes — 3 algorithms (`FixedWindow`, `SlidingWindow`, `TokenBucket`), config-selectable; positionable anywhere in the order list |
| **Hedging** | Separate `AddStandardHedgingHandler()` call | Unified — `PipelineType: Hedging` in config; delay + max attempts configurable |
| **Bulkhead / concurrency limiter** | Not included | Yes — `Bulkhead.Enabled`, configurable `Limit` and `QueueLimit` |
| **Fallback** | Not included | Yes — configurable status code (400–599), optional body, `OnlyOn5xx` flag, custom `IHttpFallbackHandler` |
| **Connection pooling** | Not included (relies on .NET defaults) | Yes — `SocketsHttpHandler` with `MaxConnectionsPerServer`, `ConnectTimeout`, `PooledConnectionIdleTimeout`, `PooledConnectionLifetime` |
| **Per-authority pipeline** | Via `SelectPipelineByAuthority()` extension | Via `PipelineSelection.Mode: ByAuthority` in config |

`AddHttpClientWithResilience()` covers every strategy Microsoft offers plus Bulkhead, Fallback, and explicit connection pooling configuration.

---

## 3. Startup Validation and Fail-Fast

| Dimension | `AddStandardResilienceHandler()` | `AddHttpClientWithResilience()` |
|---|---|---|
| **Validation mechanism** | None at startup; misconfiguration surfaces at first request | `IValidateOptions<HttpResilienceOptions>` + `ValidateOnStart()` — process fails before the first request |
| **Disabled-section skipping** | N/A | Validation skips nested sections when their feature flag (`Enabled: false`) is off — no false failures |
| **Conditional validation** | N/A | When root `Enabled: false`, the entire validator short-circuits |
| **Cross-field rules** | N/A | `AttemptTimeout ≤ TotalTimeout`, `PipelineStrategyOrder` must contain exactly one of `Standard` or `Hedging` |
| **Enum safety** | N/A | All enums (`BackoffType`, `PipelineOrder`, `PipelineType`, `PipelineSelection.Mode`, `RateLimiter.Algorithm`) validated at startup via `Enum.IsDefined` |

---

## 4. Security

| Dimension | `AddStandardResilienceHandler()` | `AddHttpClientWithResilience()` |
|---|---|---|
| **Redirect handling** | .NET defaults (`AllowAutoRedirect: true`) | Explicitly left at .NET defaults; documented as intentional — caller configures post-creation if needed |
| **Credential / auth surface** | None (appropriate) | None (appropriate) |
| **Outbound rate limiting** | Manual wiring | Config-driven; acts as an outbound request throttle, protecting downstream services from being overwhelmed by retried bursts |
| **Connection pool recycling** | .NET default (no enforced lifetime) | `PooledConnectionLifetime` prevents stale connections after DNS changes or load-balancer recycling — a reliability and security concern in cloud environments |
| **Config binding safety** | N/A | Uses standard `IConfigurationSection.Bind()` — no eval-style deserialization; safe as long as the config source is trusted |

Neither introduces security vulnerabilities. `PooledConnectionLifetime` is the concrete security-adjacent win: DNS-aware connection recycling prevents stale connections to recycled IP addresses.

---

## 5. Performance

| Dimension | `AddStandardResilienceHandler()` | `AddHttpClientWithResilience()` |
|---|---|---|
| **Pipeline setup cost** | One-time at registration, zero per-request overhead | One-time; `section.Bind()` is called once per registration, not per request |
| **Rate limiter instance lifecycle** | Created per registration | `RateLimiterFactory.CreateRateLimiter()` creates a single instance captured in a closure — no per-request allocation |
| **Connection pooling** | .NET defaults (`int.MaxValue` connections per server) | Tunable `MaxConnectionsPerServer` — prevents connection storms |
| **Handler chain depth** | 1 composite handler internally | Potentially multiple `DelegatingHandler` wrappers (`fallback` → `concurrency` → `rateLimit` → `standard`). Each hop adds a small allocation per request |
| **No-op path** | Must remove code to disable | `Enabled: false` returns the builder unchanged — zero handlers, zero allocation |

The handler-chain depth is the only meaningful performance trade-off. For ultra-high-throughput paths, prefer `PipelineStrategyOrder` with only the strategies you need to minimise handler count. In typical scenarios the overhead is negligible.

---

## 6. Observability and Telemetry

| Dimension | `AddStandardResilienceHandler()` | `AddHttpClientWithResilience()` |
|---|---|---|
| **Built-in Polly metrics** | Yes — `resilience.*` OpenTelemetry meters via `Microsoft.Extensions.Http.Resilience` | Yes — inherited from the underlying `AddStandardResilienceHandler` / `AddStandardHedgingHandler` calls |
| **Custom metering enricher** | Not included | `HttpResilienceMeteringEnricher` adds `error.type`, `request.name`, `request.dependency.name` tags to Polly events |
| **Named handlers** | Framework-chosen names | Handlers named `"fallback"`, `"rateLimit"`, `"concurrency"`, `"custom-standard"` — appear distinctly in Polly metrics for per-strategy dashboards |
| **Correlation on fallback** | N/A | `response.RequestMessage = requestMessage` is set on synthetic fallback responses — preserves correlation for logging and distributed tracing |

---

## 7. Extensibility and Maintainability

| Dimension | `AddStandardResilienceHandler()` | `AddHttpClientWithResilience()` |
|---|---|---|
| **Adding a new strategy** | Add to the delegate | Add to `PipelineStrategyNames`, the validator, and `AddHandlersInOrder` — three touch points |
| **Per-client overrides** | Requires separate delegates per client | `IConfigurationSection` overload or `requestTimeoutSeconds` param |
| **Custom fallback logic** | Not supported | `IHttpFallbackHandler` interface — pluggable, DI-friendly; return `null` to fall through to the synthetic response |
| **Two escape hatches** | The delegate itself | `configurePipeline` (outer handlers) + `configureInnerPipeline` (inner `ResiliencePipelineBuilder`) — two levels of escape |
| **Backwards compatibility** | N/A | Legacy `PipelineOrder` enum kept alongside new `PipelineStrategyOrder` — migration path exists but both systems must be kept in sync |

---

## 8. Registration Pattern

### Microsoft — single call

```csharp
services.AddHttpClient("MyClient")
    .AddStandardResilienceHandler(options =>
    {
        options.Retry.MaxRetryAttempts = 3;
        options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(30);
    });
```

### `AddHttpClientWithResilience()` — two-step, config-driven

```csharp
// Program.cs
services.AddHttpResilienceOptions(configuration);   // validates at startup

services.AddHttpClient("MyClient")
    .AddHttpClientWithResilience(configuration);    // reads "HttpResilienceOptions" section
```

```json
// appsettings.json
{
  "HttpResilienceOptions": {
    "Enabled": true,
    "PipelineType": "Standard",
    "Timeout": { "TotalRequestTimeoutSeconds": 30, "AttemptTimeoutSeconds": 10 },
    "Retry": { "MaxRetryAttempts": 3, "BackoffType": "Exponential", "UseJitter": true },
    "CircuitBreaker": { "FailureRatio": 0.5, "MinimumThroughput": 10, "SamplingDurationSeconds": 30, "BreakDurationSeconds": 15 }
  }
}
```

---

## Summary Scorecard

| Criterion | `AddStandardResilienceHandler()` | `AddHttpClientWithResilience()` |
|---|:---:|:---:|
| Configuration simplicity (code-first) | **Win** | — |
| Config-driven / zero-redeploy tuning | — | **Win** |
| Strategy coverage | Partial | **Win** |
| Startup validation / fail-fast | — | **Win** |
| Security (connection hygiene) | — | **Win** |
| Raw per-request performance | Marginal edge | — |
| Observability | Tied | **Win** (enricher + named handlers) |
| Extensibility | Tied | **Win** (two escape hatches, `IHttpFallbackHandler`) |
| Maintainability / API surface area | **Win** (smaller) | — |

---

## Related

- [IMPLEMENTATION.md](IMPLEMENTATION.md) — option-by-option reference for every `HttpResilienceOptions` field
- [COMPARISON.md](COMPARISON.md) — broader comparison against Polly and Microsoft's full resilience stack
- [ARCHITECTURE.md](ARCHITECTURE.md) — internal pipeline wiring and handler ordering
- [README.md](../README.md) — getting started and registration examples
