# Comparison: HttpResilience.NET vs Polly & Microsoft Resilience

This document compares **HttpResilience.NET** with [Polly](https://www.pollydocs.org/index.html) and [Microsoft's resilience stack](https://learn.microsoft.com/en-us/dotnet/core/resilience/) ([Microsoft.Extensions.Http.Resilience](https://www.nuget.org/packages/Microsoft.Extensions.Http.Resilience), built on Polly). It summarizes what is **good** (aligned or improved) and what is **missing or weaker** in the current package.

---

## Strategy coverage (Polly / Microsoft vs HttpResilience.NET)


| Strategy               | Polly | Microsoft HTTP resilience     | HttpResilience.NET                                                                                      |
| ---------------------- | ----- | ----------------------------- | ------------------------------------------------------------------------------------------------------- |
| Retry                  | Yes   | Yes (standard/hedging)        | Yes (config-driven, backoff/jitter/Retry-After; **RetryBackoffType** enum)                              |
| Circuit Breaker        | Yes   | Yes                           | Yes                                                                                                     |
| Timeout                | Yes   | Yes (total + attempt)         | Yes (Timeout section)                                                                                   |
| Rate Limiter           | Yes   | Yes (standard)                | Yes Optional, **RateLimitAlgorithm** enum (FixedWindow/SlidingWindow/TokenBucket); standard and hedging |
| Fallback               | Yes   | Yes                           | Yes Optional (synthetic + **IHttpFallbackHandler**; OnlyOn5xx)                                          |
| Hedging                | Yes   | Yes (dedicated handler)       | Yes Via **PipelineType: Hedging** (same extension, config switch)                                       |
| Bulkhead / Concurrency | Yes   | Yes (`AddConcurrencyLimiter`) | Yes Optional (Bulkhead section)                                                                         |


**Verdict:** All core Polly/Microsoft strategies are present, configurable, and (where applicable) type-safe via enums.

---

## What's good in the current package

1. **Configuration-first, multi-app friendly**
  Everything is driven from **appsettings** (`HttpResilienceOptions` and nested sections). No pipeline code in app code; same config can be shared across many apps. Polly and Microsoft are typically **code-first**; here the pipeline is built from options, which fits enterprise and multi-service setups.
2. **Type-safe option values**
  Fixed sets of values are **enums**: **RetryBackoffType**, **RateLimitAlgorithm**, **PipelineOrderType**, **PipelineSelectionMode**, **ResiliencePipelineType**. Config still uses strings (e.g. `"Exponential"`, `"FixedWindow"`); invalid values fail at startup via `Enum.IsDefined`. Reduces typos and improves IDE support.
3. **Clear feature boundaries**
  Options are grouped by feature (Connection, Timeout, Retry, CircuitBreaker, RateLimiter, Fallback, Hedging, Bulkhead). That matches how [Polly](https://www.pollydocs.org/index.html) and [Microsoft](https://learn.microsoft.com/en-us/dotnet/core/resilience/) describe strategies and makes it easier to reason about and document each part.
4. **Single opt-in switch and pipeline type from config**
  Root **Enabled** plus **PipelineType** (Standard | Hedging) in config. One extension, `AddHttpClientWithResilience`; no code change to switch between standard and hedging. Optional **RateLimiter:Enabled**, **Fallback:Enabled**, **Bulkhead:Enabled** for extra features.
5. **Retry behavior on par with Polly**
  Backoff type (Constant/Linear/Exponential), jitter, and Retry-After header support align with [Polly retry](https://www.pollydocs.org/strategies/retry). Good for transient faults and rate-limited APIs.
6. **Rate limiter on both pipelines**
  FixedWindow, SlidingWindow, and TokenBucket (from `System.Threading.RateLimiting`) are available for **standard and hedging** when **RateLimiter:Enabled** is true. [Polly rate limiting](https://www.pollydocs.org/strategies/rate-limiter) and Microsoft's HTTP resilience support similar ideas; here they're exposed via config and the **RateLimitAlgorithm** enum.
7. **Fallback: synthetic and custom**
  Synthetic response (status + body) plus **IHttpFallbackHandler** for arbitrary fallback (e.g. call another URL, return cached value). Consistent with [Polly fallback](https://www.pollydocs.org/strategies/fallback); custom handler is invoked first, then synthetic if it returns null.
8. **Configurable pipeline order**
  **PipelineOrder** (FallbackThenConcurrency vs ConcurrencyThenFallback) or full **PipelineStrategyOrder** (array: Fallback, Bulkhead, RateLimiter, Standard/Hedging). Outer order is under your control; by default the inner order of retry/circuit breaker/timeout comes from Microsoft's standard/hedging handlers, but an advanced overload lets you build the inner pipeline yourself with `ResiliencePipelineBuilder<HttpResponseMessage>` when you need full control. A section-based overload allows per-tenant or per-client connection/timeout when using that custom inner pipeline.
9. **Per-authority pipeline selection**
  **PipelineSelection:Mode** = **ByAuthority** uses Microsoft's `SelectPipelineByAuthority()` so the same options yield **separate pipeline instances per request authority** (scheme + host + port). Circuit breakers and state are per host; no need for multiple named clients for multiple hosts.
10. **Custom pipeline extension point**
  **configurePipeline** overload lets you add extra resilience handlers (outermost) after the built-in pipeline. Aligns with "Polly/Microsoft let you build arbitrary pipelines" for advanced users who need custom strategies (e.g. logging, custom retry) without leaving the package.
11. **Startup validation**
  Options are validated at startup (ranges, `Enum.IsDefined` for enums, PipelineStrategyOrder rules). Misconfiguration fails fast instead of at runtime.
12. **Built on the same stack**
  Uses **Microsoft.Extensions.Http.Resilience** and **Polly** under the hood, so behavior and semantics match [Microsoft's resilient HTTP guidance](https://learn.microsoft.com/en-us/dotnet/core/resilience/) and [Polly's strategies](https://www.pollydocs.org/strategies).
13. **Documentation**
  [docs/IMPLEMENTATION.md](IMPLEMENTATION.md) explains each option, its values (and enums), and effect in a consistent way (options + effect).

---

## What's missing compared to Polly & Microsoft

1. **Telemetry / enrichment (now partially aligned)**
  [Microsoft](https://learn.microsoft.com/en-us/dotnet/core/resilience/) documents **AddResilienceEnricher** and metrics enrichment (e.g. `error.type`, `request.name`, `request.dependency.name`). [Polly](https://www.pollydocs.org/advanced/telemetry) has telemetry and metering.  
   **HttpResilience.NET** now registers a Polly `MeteringEnricher` via `AddHttpResilienceTelemetry`, adding Microsoft-style tags such as `error.type`, `request.name`, and `request.dependency.name` to the **Polly** meter (`resilience.polly.`* instruments), which are then collected by the shared `OpenTelemetry.NET` package. More advanced/custom telemetry scenarios (additional tags, per-pipeline telemetry options, or non-HTTP enrichment) are still left to consuming applications.
2. **No chaos / testing helpers**
  Polly has [chaos engineering](https://www.pollydocs.org/strategies/chaos) and testing support. This package does not provide fault injection or test utilities. If you want to test resilience, you do it yourself or use Polly directly.
3. **Dependency on Microsoft.Extensions.Http.Resilience**
  The package is a thin layer over Microsoft's and Polly's types. If Microsoft deprecates or changes `AddStandardResilienceHandler` / `AddStandardHedgingHandler`, this package must follow. You do not own the inner pipeline implementation.
4. **No explicit resilience pipeline abstraction**
  You do not get a `ResiliencePipeline` or `ResiliencePipelineProvider<TKey>` in your app; you only get an `HttpClient` configured with resilience. So you cannot reuse the same pipeline for non-HTTP work (e.g. database calls) like you can with [Microsoft.Extensions.Resilience](https://www.nuget.org/packages/Microsoft.Extensions.Resilience) and Polly's generic pipeline.

---

## Summary


| Aspect             | Good / Aligned                                                                                                                                                                        | Missing                                                          |
| ------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------- |
| Strategy coverage  | Retry, circuit breaker, timeout, rate limit, fallback, hedging, bulkhead; enums for type safety                                                                                       | —                                                                |
| Configuration      | Full appsettings-driven; PipelineType, PipelineStrategyOrder, PipelineSelection; enums for fixed values                                                                               | —                                                                |
| Flexibility        | PipelineStrategyOrder, advanced code-first inner pipeline via ResiliencePipelineBuilder, configurePipeline, custom fallback (IHttpFallbackHandler), ByAuthority                       | No chaos/testing helpers                                         |
| Telemetry          | Uses same underlying stack; Polly telemetry enriched with `error.type`, `request.name`, `request.dependency.name` via `AddHttpResilienceTelemetry` and collected by OpenTelemetry.NET | No advanced/custom telemetry helpers beyond the minimal enricher |
| Advanced scenarios | One config, many apps; per-authority; hedging + rate limit; custom fallback; custom pipeline delegate                                                                                 | No chaos / test utilities                                        |
| Documentation      | Options, enums, and effects documented                                                                                                                                                | —                                                                |


**Bottom line:** HttpResilience.NET is **well aligned** with Polly and Microsoft for HTTP resilience: configuration-driven, type-safe options (enums), config-driven pipeline type and order, per-authority selection, custom fallback, and an extension point for extra strategies. It now includes **minimal telemetry enrichment** via `AddHttpResilienceTelemetry` (Polly metrics enriched with `error.type`, `request.name`, `request.dependency.name`, collected by `OpenTelemetry.NET`). It still does **not** add chaos/testing helpers or an explicit reusable resilience pipeline abstraction; and the **inner** order of standard/hedging strategies is fixed by Microsoft's handlers.

For **how to consume** the package, see the [README](../README.md). For **option-by-option behavior and use cases**, see [IMPLEMENTATION.md](IMPLEMENTATION.md).