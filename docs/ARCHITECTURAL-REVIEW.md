# HttpResilience.NET - Principal Architect Review

**Date**: 2026-04-04
**Reviewer**: Principal .NET Architect (AI-assisted)
**Version Reviewed**: v1.0.0 (commit a644cb2)
**Resilience Maturity Score**: 7.5 / 10

---

## Overview

HttpResilience.NET is a standardization wrapper over `Microsoft.Extensions.Http.Resilience` (Polly 8) providing consistent HTTP client resilience configuration across microservices. This review evaluates production-readiness for high-scale distributed systems.

---

## Critical Issues (Must Fix)

### 1. RateLimiter Memory Leak — Limiter Never Disposed

**Files**: `RateLimiterFactory.cs`, `HttpStandardResilienceHandlerConfig.cs:45-47`, `ServiceCollectionExtensions.cs:482`

`RateLimiterFactory.CreateRateLimiter()` creates `RateLimiter` instances that implement `IDisposable` but are never disposed. `FixedWindowRateLimiter`, `SlidingWindowRateLimiter`, and `TokenBucketRateLimiter` all use internal timers. Under high RPS with client rotation (`IHttpClientFactory` recreates handlers), these limiter instances accumulate without cleanup.

**Impact**: Memory leak under sustained production load. Timer-based replenishment in `TokenBucketRateLimiter` and `SlidingWindowRateLimiter` continues indefinitely.

**Fix**: Register the limiter as a singleton in DI, tied to `ServiceProvider` disposal lifecycle.

### 2. Options Bound Twice — Config Snapshot Divergence

**Files**: `ServiceCollectionExtensions.cs:270-271`, `ServiceCollectionExtensions.cs:328`

The two-step registration pattern creates inconsistency:
- Step 1 (`AddHttpResilienceOptions`): Binds to DI via `IOptions<HttpResilienceOptions>` with `ValidateOnStart`
- Step 2 (`AddHttpClientWithResilience`): Does `new HttpResilienceOptions()` + `section.Bind(options)` — a separate snapshot

The DI-registered options and the pipeline-wiring options are two different instances. If config changes at runtime (e.g., Azure App Configuration, `reloadOnChange: true`), the DI `IOptions<T>` updates but the pipeline config is frozen at startup.

**Fix**: Use named options registered in DI. Section-based overloads register `IOptions<HttpResilienceOptions>` with a name derived from the client name. Pipeline wiring resolves options from DI consistently.

### 3. `HttpStandardHedgingHandlerConfig` is `public` — Should Be `internal`

**File**: `HttpStandardHedgingHandlerConfig.cs:11`

Declared `public static class` despite living in the `Internal` namespace. Leaks implementation details into the public API surface. `HttpStandardResilienceHandlerConfig` is correctly `internal`.

**Fix**: Change to `internal static class`.

---

## Improvements (Should Fix)

### 1. Fallback Swallows Exceptions Silently

**File**: `ServiceCollectionExtensions.cs:490-506`

The fallback handler catches `HttpRequestException` and non-success responses, then returns a synthetic response with zero logging. Operators have no way to know fallback is activating.

**Fix**: Accept `ILoggerFactory` and log at `Warning` level when fallback activates.

### 2. No Structured Logging for Retry/Circuit Breaker Events

The `HttpResilienceMeteringEnricher` handles metrics, but there are no Polly `OnRetry`, `OnBreak`, `OnHalfOpen` callbacks configured. Circuit breaker state transitions and retry attempts are invisible to application logs.

**Fix**: Add structured logging callbacks on retry and circuit breaker strategies.

### 3. `BaseDelaySeconds` as `int` Prevents Sub-Second Retries

**File**: `RetryOptions.cs`

Uses `int BaseDelaySeconds` with a minimum of 1. In high-throughput systems, a 1-second minimum retry delay is too coarse. Polly supports `TimeSpan` with millisecond precision.

**Fix**: Change to `double`, update range to `[Range(0.0, 60.0)]`.

### 4. `IHttpPolicyEventArguments` Interface is Dead Code

**File**: `HttpResilienceMeteringEnricher.cs:134-137`

Defines `IHttpPolicyEventArguments` but Polly 8.x never exposes arguments implementing this interface. The check at line 78 will never be true. Vestigial from Polly v7.

**Fix**: Remove the interface and unreachable branch.

### 5. Two Ordering Systems Creates Confusion

Three config properties interact to determine pipeline behavior: `PipelineStrategyOrder` (list), `PipelineOrder` (enum), `PipelineType` (enum). The legacy path has different behavior for Standard vs Hedging.

**Fix**: Remove `PipelineType` and `PipelineOrder` entirely. Make `PipelineStrategyOrder` (renamed to `PipelineOrder`) the single required source of truth. See design spec for details.

### 6. Sample Project Has Missing Package Versions

The sample project sets `ManagePackageVersionsCentrally=false` but doesn't pin versions, causing build failure.

---

## Good Practices Found

1. **Correct library choice**: Built on `Microsoft.Extensions.Http.Resilience` — the Microsoft-recommended Polly 8 integration.
2. **Thin wrapper, not reimplementation**: Config builders are pure mappers (~50 lines each). Zero custom resilience logic.
3. **`Enabled = false` master switch** with validation bypass — smart operational pattern.
4. **`ValidateOnStart()`** with custom validator — fails fast at startup.
5. **`SelectPipelineByAuthority()`** — per-authority circuit breaker isolation.
6. **Proper `IHttpClientFactory` integration** — named clients, `ConfigurePrimaryHttpMessageHandler`, correct handler pipeline.
7. **`SocketsHttpHandler` configuration** — connection pooling with `PooledConnectionLifetime` prevents DNS stale cache (critical for Kubernetes).
8. **`UseRetryAfterHeader = true`** — respects server-indicated backoff, prevents retry storms.
9. **`UseJitter = true` by default** — prevents thundering herd.
10. **Sensible defaults** — `MinimumThroughput = 100`, `FailureRatio = 0.1`, `SamplingDuration = 30s`.
11. **Metering enricher** — `error.type`, `request.name`, `request.dependency.name` tags align with OpenTelemetry conventions.
12. **51 tests passing** — unit + integration coverage.
13. **`TreatWarningsAsErrors` + `Deterministic` builds** — production-grade hardening.

---

## Polly vs Microsoft.Extensions.Http.Resilience Recommendation

**Verdict: Already on the right path.**

```
HttpResilience.NET (this library)
  └── Microsoft.Extensions.Http.Resilience  <-- correctly used
        └── Polly 8 (Polly.Core, Polly.Extensions)
              └── System.Threading.RateLimiting
```

Do NOT switch to raw Polly. The only direct Polly reference needed is `Polly.Extensions` for `TelemetryOptions`/`MeteringEnricher`. Consider removing the explicit `Polly.Extensions` reference and letting it come transitively from `Microsoft.Extensions.Http.Resilience`.

---

## Resilience Maturity Score: 7.5 / 10

| Category | Score | Notes |
|----------|-------|-------|
| Library Choice | 10/10 | Microsoft.Extensions.Http.Resilience — exactly right |
| Abstraction Quality | 8/10 | Thin, justified. Dual ordering systems confusing |
| HttpClient Integration | 9/10 | Proper IHttpClientFactory, named clients, SocketsHttpHandler |
| Strategy Implementation | 8/10 | All major strategies present. Sub-second retry gap |
| Configuration | 8/10 | Externalized, per-client, multi-tenant. Snapshot divergence |
| Observability | 5/10 | Metrics enricher present. No logging callbacks. Silent fallback |
| Performance | 7/10 | RateLimiter leak. Otherwise clean |
| Cloud-Native | 8/10 | K8s-friendly connection pooling. Per-authority isolation |
| Security | 8/10 | Jitter + RetryAfter prevent storms |
| Test Coverage | 8/10 | 51 tests. Missing dispose/hot-reload/concurrency tests |

**Target after fixes: 9/10**
