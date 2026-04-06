# HttpResilience.NET — Implementation Reference

This document describes the **implementation logic** and **configuration** for each part of the package. For quick-start setup, see the [README](../README.md).

---

## Overview

The package builds on **Microsoft.Extensions.Http.Resilience** and **Polly** to provide a configuration-driven HTTP resilience pipeline. Options are grouped by feature (Connection, Timeout, Retry, CircuitBreaker, RateLimiter, Fallback, Hedging, Bulkhead).

A single **PipelineOrder** list controls all handler ordering (e.g. `["Fallback", "Bulkhead", "RateLimiter", "Standard"]`). It must contain exactly one of `"Standard"` or `"Hedging"`. The root **Enabled** flag turns the pipeline on or off; optional features (RateLimiter, Fallback, Bulkhead) each have their own **Enabled** flag.

---

## Root: Enabled

Master switch. When `false` (default), no resilience pipeline, no custom primary handler, and no startup validation. When `true`, the full pipeline is applied.

```json
{ "HttpResilienceOptions": { "Enabled": true, "PipelineOrder": ["Standard"] } }
```

---

## PipelineOrder

Order of pipeline strategies from **outermost to innermost**. Allowed values: `"Fallback"`, `"Bulkhead"`, `"RateLimiter"`, `"Standard"`, `"Hedging"`.

**Rules:**
- Must contain exactly one of `"Standard"` or `"Hedging"`.
- No duplicates. Case-insensitive.
- Required when `Enabled = true`.
- Optional strategies (Fallback, Bulkhead, RateLimiter) are only added when their `Enabled` flag is `true`.

**How it works:** Handlers are added in reverse order internally, so the first element is outermost (executes first).

```json
{
  "HttpResilienceOptions": {
    "Enabled": true,
    "PipelineOrder": ["Fallback", "Bulkhead", "RateLimiter", "Standard"]
  }
}
```

Execution order: Fallback → Bulkhead → RateLimiter → Standard → SocketsHttpHandler → Network.

For **Hedging**, replace `"Standard"` with `"Hedging"`:

```json
{ "PipelineOrder": ["Fallback", "RateLimiter", "Hedging"] }
```

---

## Connection

Configures the primary `SocketsHttpHandler` when `Connection.Enabled = true`.

| Property | Range | Default | Effect |
|----------|-------|---------|--------|
| `Enabled` | — | `false` | When false, .NET's default handler is used |
| `MaxConnectionsPerServer` | 1–1000 | 10 | Max concurrent TCP connections per host:port |
| `PooledConnectionIdleTimeoutSeconds` | 1–3600 | 120 | Close idle connections after this many seconds |
| `PooledConnectionLifetimeSeconds` | 1–3600 | 600 | Recycle connections to respect DNS/LB changes |
| `ConnectTimeoutSeconds` | 1–120 | 21 | Max time for TCP/TLS establishment |

> `AllowAutoRedirect`, `Credentials`, and other security properties are **not** set by this package; .NET defaults apply.

```json
"Connection": {
  "Enabled": true,
  "MaxConnectionsPerServer": 20,
  "ConnectTimeoutSeconds": 10,
  "PooledConnectionIdleTimeoutSeconds": 90,
  "PooledConnectionLifetimeSeconds": 300
}
```

---

## Timeout

Two timeouts: **total** (entire operation including retries) and **per-attempt**.

| Property | Range | Default | Effect |
|----------|-------|---------|--------|
| `TotalRequestTimeoutSeconds` | 1–600 | 30 | Hard limit for the whole operation |
| `AttemptTimeoutSeconds` | 1–300 | 10 | Limit per single attempt |

**Validation:** `AttemptTimeoutSeconds` must be ≤ `TotalRequestTimeoutSeconds`.

> **Sizing:** With 3 retries and exponential backoff at 2s, total delay can reach ~14s (2+4+8). Ensure `TotalRequestTimeoutSeconds` accommodates attempts + backoff.

```json
"Timeout": {
  "TotalRequestTimeoutSeconds": 30,
  "AttemptTimeoutSeconds": 8
}
```

---

## Retry

Retries failed attempts with configurable count, delay, backoff, jitter, and `Retry-After` header support.

| Property | Range | Default | Effect |
|----------|-------|---------|--------|
| `MaxRetryAttempts` | 0–10 | 3 | Retries after first failure. 0 = no retries |
| `BaseDelaySeconds` | 0.0–60.0 | 2.0 | Base delay (seconds, supports sub-second e.g. `0.5`) |
| `BackoffType` | `Constant` / `Linear` / `Exponential` | `Exponential` | How delay grows between retries |
| `UseJitter` | bool | `true` | Add random jitter to prevent thundering herd |
| `UseRetryAfterHeader` | bool | `true` | Respect `Retry-After` header from 429/503 responses |

**Guidance:** For interactive paths, keep `MaxRetryAttempts` low (0–3). Set to `0` to disable retries entirely.

```json
"Retry": {
  "MaxRetryAttempts": 3,
  "BaseDelaySeconds": 2,
  "BackoffType": "Exponential",
  "UseJitter": true,
  "UseRetryAfterHeader": true
}
```

---

## CircuitBreaker

Stops sending requests when the failure ratio exceeds a threshold within a sampling window.

| Property | Range | Default | Effect |
|----------|-------|---------|--------|
| `MinimumThroughput` | 1–100 | 100 | Min requests before circuit can open |
| `FailureRatio` | 0.01–1.0 | 0.1 | Fraction of failures that triggers open (e.g. 0.1 = 10%) |
| `SamplingDurationSeconds` | 1–600 | 30 | Time window for failure measurement |
| `BreakDurationSeconds` | 1–300 | 5 | How long circuit stays open before half-open trial |

> **Tuning:** The default `MinimumThroughput: 100` is conservative. For services with < 100 req/30s, lower to 10–20. A `FailureRatio: 0.1` is strict; for best-effort APIs consider 0.3–0.5.

```json
"CircuitBreaker": {
  "MinimumThroughput": 10,
  "FailureRatio": 0.5,
  "SamplingDurationSeconds": 30,
  "BreakDurationSeconds": 15
}
```

---

## RateLimiter (optional)

Limits requests per time window. Three algorithms from `System.Threading.RateLimiting`.

| Property | Range | Default | Effect |
|----------|-------|---------|--------|
| `Enabled` | bool | `false` | Enable/disable rate limiting |
| `PermitLimit` | 1–100000 | 1000 | Max requests per window |
| `WindowSeconds` | 1–3600 | 1 | Window length |
| `QueueLimit` | 0–10000 | 0 | Requests that wait when limit hit (0 = reject) |
| `Algorithm` | `FixedWindow` / `SlidingWindow` / `TokenBucket` | `FixedWindow` | Rate limiting algorithm |
| `SegmentsPerWindow` | 1–100 | 2 | SlidingWindow only: segments per window |
| `TokenBucketCapacity` | 1–100000 | 1000 | TokenBucket only: max tokens |
| `TokensPerPeriod` | 1–100000 | 1000 | TokenBucket only: tokens per replenishment |
| `ReplenishmentPeriodSeconds` | 1–3600 | 1 | TokenBucket only: replenishment interval |

**FixedWindow** — simple X permits per window:
```json
"RateLimiter": { "Enabled": true, "Algorithm": "FixedWindow", "PermitLimit": 100, "WindowSeconds": 1 }
```

**SlidingWindow** — smoother, avoids boundary spikes:
```json
"RateLimiter": { "Enabled": true, "Algorithm": "SlidingWindow", "PermitLimit": 100, "WindowSeconds": 10, "SegmentsPerWindow": 5 }
```

**TokenBucket** — sustained average with burst capacity:
```json
"RateLimiter": { "Enabled": true, "Algorithm": "TokenBucket", "TokenBucketCapacity": 200, "TokensPerPeriod": 50, "ReplenishmentPeriodSeconds": 1 }
```

---

## Fallback (optional)

On total failure, returns a **synthetic response** or a **custom response** from an `IHttpFallbackHandler`.

| Property | Range | Default | Effect |
|----------|-------|---------|--------|
| `Enabled` | bool | `false` | Enable/disable fallback |
| `StatusCode` | 400–599 | 503 | Synthetic response status code |
| `OnlyOn5xx` | bool | `false` | `true` = only fallback on 5xx + exceptions; 4xx pass through |
| `ResponseBody` | string? | `null` | Plain-text body for synthetic response |

**Custom fallback:** Implement `IHttpFallbackHandler` and pass to `AddHttpClientWithResilience(..., fallbackHandler: handler)`. If the handler returns `null`, the synthetic response is used as backstop. `HttpFallbackContext` provides the failed outcome.

The synthetic response has `RequestMessage` set from the failed outcome for correlation.

```json
"Fallback": {
  "Enabled": true,
  "StatusCode": 503,
  "OnlyOn5xx": true,
  "ResponseBody": "Service temporarily unavailable."
}
```

---

## Hedging

Sends multiple requests (original + hedged) and uses the **first successful** response. Include `"Hedging"` in `PipelineOrder` to activate.

| Property | Range | Default | Effect |
|----------|-------|---------|--------|
| `DelaySeconds` | 0–60 | 2 | Seconds before sending hedged request. 0 = immediate |
| `MaxHedgedAttempts` | 0–10 | 1 | Extra attempts (total = 1 + this) |

> **Hedging amplifies load.** With `MaxHedgedAttempts: 2`, up to 3 requests can be in-flight. Keep low (1–2) and consider RateLimiter/Bulkhead to bound amplification.

```json
"Hedging": { "DelaySeconds": 1, "MaxHedgedAttempts": 1 }
```

---

## Bulkhead (optional)

Limits **concurrent** outbound requests.

| Property | Range | Default | Effect |
|----------|-------|---------|--------|
| `Enabled` | bool | `false` | Enable/disable bulkhead |
| `Limit` | 1–1000 | 100 | Max concurrent in-flight requests |
| `QueueLimit` | 0–10000 | 0 | Requests waiting for a slot (0 = reject at limit) |

```json
"Bulkhead": { "Enabled": true, "Limit": 50, "QueueLimit": 20 }
```

---

## PipelineSelection (optional)

When `Mode` is `ByAuthority`, a **separate pipeline instance** is used per request authority (scheme + host + port), isolating circuit breakers and rate limiters per host.

| Value | Effect |
|-------|--------|
| `None` (default) | One pipeline per named client |
| `ByAuthority` | One pipeline per distinct authority |

```json
"PipelineSelection": { "Mode": "ByAuthority" }
```

---

## Custom pipeline (code)

Two escape hatches for code-first control:

1. **`configurePipeline`** — adds extra resilience handlers outermost (e.g. custom logging):
   ```csharp
   .AddHttpClientWithResilience(config, requestTimeoutSeconds: null, fallbackHandler: null,
       configurePipeline: b => b.AddResilienceHandler("custom", rb => { /* ... */ }));
   ```

2. **`configureInnerPipeline`** — full control via `ResiliencePipelineBuilder<HttpResponseMessage>`, bypassing `AddStandardResilienceHandler`. Config keys like `PipelineOrder` are not applied to the inner pipeline:
   ```csharp
   .AddHttpClientWithResilience(config, requestTimeoutSeconds: 30, fallbackHandler: null,
       configureInnerPipeline: inner =>
       {
           inner
               .AddRetry(new HttpRetryStrategyOptions { /* ... */ })
               .AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions { /* ... */ })
               .AddTimeout(new HttpTimeoutStrategyOptions { Timeout = TimeSpan.FromSeconds(30) });
       });
   ```

For per-tenant scenarios, use the `IConfigurationSection` overload so the primary handler is built from that section.

---

## Health checks

Register `AddHttpResilienceHealthChecks()` to expose aggregate circuit breaker state via ASP.NET health checks:

```csharp
services.AddHttpResilienceHealthChecks();
```

- **Healthy** — all circuit breakers are closed.
- **Degraded** — any circuit breaker is open or half-open.
- Per-client state is included in the health check data dictionary.
- Suitable for Kubernetes readiness probes.

Internally uses `CircuitBreakerStateTracker`, which is automatically wired when the Standard or Hedging pipeline is configured.

---

## Structured logging

Resilience events are logged via high-performance `LoggerMessage` source generation (zero-allocation when disabled):

| Event | Level | When |
|-------|-------|------|
| Fallback activated | Warning | Fallback strategy produces a response |
| Retry attempt | Warning | A retry is attempted |
| Circuit breaker opened | Warning | Circuit transitions to open |
| Circuit breaker half-open | Information | Circuit allows a trial request |
| Circuit breaker closed | Information | Circuit recovers |

These are wired automatically when the pipeline is configured — no additional registration needed.

---

## Telemetry and metrics enrichment

`AddHttpResilienceTelemetry()` registers `HttpResilienceMeteringEnricher` which adds tags to Polly metrics (`resilience.polly.*` instruments):

| Tag | Source |
|-----|--------|
| `error.type` | Exception type name (e.g. `System.TimeoutException`) or `HttpStatusCode.<code>` |
| `request.name` | `ResilienceContext.OperationKey` if set, else pipeline/strategy names |
| `request.dependency.name` | `scheme://host[:port]` from the request URI |

**Safe by default:** No bodies, headers, paths, or query strings are exported.

Collect the Polly meter in your metrics pipeline:
```csharp
metrics.AddMeter(HttpResilienceTelemetryExtensions.PollyMeterName); // or "Polly"
```

---

## Validation

Options are validated at **startup** when `Enabled = true`. Invalid configuration throws `OptionsValidationException` for fast failure.

**Rules:**
- Data annotation ranges on all numeric properties.
- `PipelineOrder`: required, valid names, no duplicates, exactly one of Standard/Hedging.
- `AttemptTimeoutSeconds` ≤ `TotalRequestTimeoutSeconds`.
- `Enum.IsDefined` for `BackoffType`, `Algorithm`, `PipelineSelection.Mode`.
- Validation failures from disabled sections are ignored (e.g. RateLimiter range errors when `RateLimiter.Enabled = false`).

---

## Summary

| Section | Always applied when Enabled? | Purpose |
|---------|------------------------------|---------|
| Connection | When `Connection.Enabled` | Pool size, connect timeout |
| Timeout | Yes | Total vs per-attempt timeout |
| Retry | Yes (disable via `MaxRetryAttempts: 0`) | Transient failures, backoff, jitter |
| CircuitBreaker | Yes | Stop calling failing dependencies |
| RateLimiter | When `RateLimiter.Enabled` | Quota / rate limits |
| Fallback | When `Fallback.Enabled` | Synthetic or custom response on failure |
| Hedging | When `PipelineOrder` contains `"Hedging"` | Lower tail latency |
| Bulkhead | When `Bulkhead.Enabled` | Limit concurrent outbound requests |

For **how to consume** the package, see the [README](../README.md). For **operational guidance**, see [OPERATIONS.md](OPERATIONS.md).
