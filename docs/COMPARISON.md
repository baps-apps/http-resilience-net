# Comparison: HttpResilience.NET vs Polly & Microsoft Resilience

This document compares **HttpResilience.NET** with [Polly](https://www.pollydocs.org/index.html) and [Microsoft.Extensions.Http.Resilience](https://learn.microsoft.com/en-us/dotnet/core/resilience/) (built on Polly).

---

## Strategy coverage

| Strategy | Polly | Microsoft HTTP Resilience | HttpResilience.NET |
|----------|-------|---------------------------|--------------------|
| Retry | Yes | Yes | Yes — config-driven, backoff/jitter/Retry-After |
| Circuit Breaker | Yes | Yes | Yes — with health check integration |
| Timeout | Yes | Yes (total + attempt) | Yes — total + per-attempt |
| Rate Limiter | Yes | Yes (standard only) | Yes — FixedWindow/SlidingWindow/TokenBucket; both pipelines |
| Fallback | Yes | Yes | Yes — synthetic + custom `IHttpFallbackHandler` |
| Hedging | Yes | Yes (separate handler) | Yes — via `PipelineOrder` config switch |
| Bulkhead | Yes | Yes | Yes — optional concurrency limiter |

---

## Side-by-side with `AddStandardResilienceHandler()`

| Dimension | `AddStandardResilienceHandler()` | `AddHttpClientWithResilience()` |
|-----------|----------------------------------|---------------------------------|
| **Setup** | Single call, programmatic delegate | Two-step: `AddHttpResilienceOptions()` + `AddHttpClientWithResilience()` |
| **Source of truth** | Code (`Action<HttpStandardResilienceOptions>`) | Config file (`appsettings.json`) |
| **Environment tuning** | Requires code branches | Native via `appsettings.{env}.json` |
| **Multi-tenant** | Not built-in | `IConfigurationSection` overloads per named client |
| **Master switch** | None — remove the call | `Enabled: false` → no-op |
| **Pipeline ordering** | Fixed internal order | Explicit `PipelineOrder` list, outermost→innermost |
| **Startup validation** | None; surfaces at first request | `ValidateOnStart` with cross-field rules |
| **Bulkhead** | Not included | Config-driven concurrency limiter |
| **Fallback** | Not included | Synthetic + custom `IHttpFallbackHandler` |
| **Connection pooling** | .NET defaults | `SocketsHttpHandler` with DNS-aware `PooledConnectionLifetime` |
| **Per-authority pipeline** | `SelectPipelineByAuthority()` in code | `PipelineSelection.Mode: ByAuthority` in config |
| **Health checks** | Not included | `AddHttpResilienceHealthChecks()` for circuit breaker state |
| **Observability** | Polly metrics only | Polly metrics + enricher (`error.type`, `request.name`, `request.dependency.name`) + structured logging |
| **Custom escape hatches** | The delegate itself | `configurePipeline` (outer) + `configureInnerPipeline` (inner) |

### Registration example

**Microsoft — single call:**

```csharp
services.AddHttpClient("MyClient")
    .AddStandardResilienceHandler(options =>
    {
        options.Retry.MaxRetryAttempts = 3;
        options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(30);
    });
```

**HttpResilience.NET — config-driven:**

```csharp
services.AddHttpResilienceOptions(configuration);
services.AddHttpClient("MyClient")
    .AddHttpClientWithResilience(configuration);
```

```json
{
  "HttpResilienceOptions": {
    "Enabled": true,
    "PipelineOrder": ["Standard"],
    "Retry": { "MaxRetryAttempts": 3 },
    "Timeout": { "TotalRequestTimeoutSeconds": 30, "AttemptTimeoutSeconds": 10 }
  }
}
```

---

## What's good

1. **Configuration-first** — everything driven from `appsettings.json`; same config reused across services.
2. **Type-safe enums** — `RetryBackoffType`, `RateLimitAlgorithm`, `PipelineSelectionMode` validated at startup via `Enum.IsDefined`.
3. **Single `PipelineOrder` list** — controls all handler ordering from config without code changes.
4. **Per-authority isolation** — `PipelineSelection:Mode = ByAuthority` for separate circuit breakers per host.
5. **Health checks** — `AddHttpResilienceHealthChecks()` exposes aggregate circuit breaker state.
6. **Structured logging** — retry, circuit breaker, and fallback events logged via `LoggerMessage` source generation.
7. **Startup validation** — ranges, enum values, cross-field rules, conditional skip when disabled.
8. **Built on the right stack** — thin wrapper over `Microsoft.Extensions.Http.Resilience` and Polly 8.

---

## What's missing compared to Polly & Microsoft

1. **No chaos/testing helpers** — Polly has fault injection; this package does not.
2. **No generic resilience pipeline** — HTTP-only; cannot reuse for database or other non-HTTP calls.
3. **Dependency on Microsoft's handlers** — if `AddStandardResilienceHandler`/`AddStandardHedgingHandler` change, this package must follow.

---

## Summary scorecard

| Criterion | `AddStandardResilienceHandler()` | `AddHttpClientWithResilience()` |
|-----------|:---:|:---:|
| Config-driven / zero-redeploy tuning | — | **Win** |
| Strategy coverage | Partial | **Win** |
| Startup validation / fail-fast | — | **Win** |
| Health checks | — | **Win** |
| Observability | Tied | **Win** (enricher + structured logging) |
| Extensibility | Tied | **Win** (two escape hatches, fallback handler) |
| Configuration simplicity (code-first) | **Win** | — |
| Raw per-request performance | Marginal edge | — |
| API surface area | **Win** (smaller) | — |

---

## Related

- [IMPLEMENTATION.md](IMPLEMENTATION.md) — option-by-option reference
- [ARCHITECTURE.md](ARCHITECTURE.md) — pipeline wiring and handler ordering
- [README.md](../README.md) — getting started
