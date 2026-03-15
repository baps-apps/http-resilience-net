# HttpResilience.NET – Implementation, Logic & Use Cases

This document describes the **implementation logic**, **usefulness**, and **use cases** for each part of the package. For how to consume the package (setup, configuration, code), see the [README](../README.md).

---

## Overview

The package builds on **Microsoft.Extensions.Http.Resilience** and **Polly** to provide a configuration-driven HTTP resilience pipeline: retry, circuit breaker, timeouts, optional rate limiting, fallback, and bulkhead. Options are grouped by **feature** (Connection, Timeout, Retry, CircuitBreaker, RateLimiter, Fallback, Hedging, Bulkhead) so consumers can reason about one concern at a time.

**Pipeline type** is set by **PipelineType** (Standard or Hedging). **Pipeline order** is either legacy (**PipelineOrder**: Fallback vs Bulkhead) or explicit **PipelineStrategyOrder** (array of strategy names). When all enabled: Fallback → Bulkhead → (RateLimiter when in list) → (Standard or Hedging) → Primary. The root **Enabled** flag turns the whole pipeline on or off; **RateLimiter**, **Fallback**, and **Bulkhead** each have their own **Enabled** for optional add-ons.

For telemetry, the package uses Polly's built-in telemetry pipeline and a small `MeteringEnricher` to add Microsoft-style tags such as `error.type`, `request.name`, and `request.dependency.name` on top of the standard resilience metrics. These metrics are emitted by the `Polly` meter (`resilience.polly.*` instruments) and can be collected by the shared `OpenTelemetry.NET` package.

---

## Root: Enabled

- **What it does:** Master switch. When `false` or omitted, no resilience or custom primary handler is applied—the extensions return the builder unchanged.
- **Options:** `true`, `false` (default).  
  **Effect:** `false` = no resilience pipeline and no custom primary handler; the extension returns the builder unchanged. `true` = full pipeline (timeouts, retry, circuit breaker, etc.) and primary handler from this package are applied.
- **Use case:**  
  When you make an API call and **Enabled** is `false`, your app uses a normal `HttpClient` with no retries, no circuit breaker, no timeouts from this package. When you set **Enabled** to `true`, the same call goes through the resilience pipeline (timeouts, retry, circuit breaker, etc.). So: turn it on in production where you need resilience; leave it off in local dev or when calling only trusted APIs so you don’t add extra behavior.

---

## PipelineType

- **What it does:** Chooses the core resilience pipeline: **Standard** (retry, circuit breaker, timeouts, optional rate limiting) or **Hedging** (multiple requests, first success wins; optional rate limiting when **RateLimiter:Enabled**).
- **Options:** `Standard` (default), `Hedging`. Config key: `PipelineType`.
- **Effect:** One extension (`AddHttpClientWithResilience`) applies either pipeline based on this value. No code change needed to switch between standard and hedging; set in config per environment or client.
- **Use case:** Use **Hedging** when the same API is behind a load balancer with multiple replicas and you care about tail latency; use **Standard** for typical API calls.

---

## Connection

- **What it does:** Configures the primary `SocketsHttpHandler`: connection pool size per server, idle/lifetime timeouts for pooled connections, and connect timeout for TCP/TLS.
- **Implementation:** `SocketsHttpHandlerFactory` reads these options and sets `MaxConnectionsPerServer`, `ConnectTimeout`, `PooledConnectionIdleTimeout`, `PooledConnectionLifetime`. **Redirect and authentication:** `AllowAutoRedirect`, `MaxAutomaticRedirections`, `Credentials`, and other security-related properties are **not** set by this package; .NET defaults apply. To change redirect or auth behavior, configure the primary handler after creation (e.g. via a custom factory) or use a custom handler in your application.
- **Options and effect:**
  - **MaxConnectionsPerServer:** Range 1–1000. Default: 10. **Effect:** Max concurrent TCP connections to a single host:port. Higher = more parallel requests to the same server; lower = less load on that server.
  - **PooledConnectionIdleTimeoutSeconds:** Range 1–3600. Default: 120. **Effect:** After this many seconds idle, a pooled connection is closed. Lower = free resources sooner; higher = fewer reconnects for steady traffic.
  - **PooledConnectionLifetimeSeconds:** Range 1–3600. Default: 600. **Effect:** A connection is recycled after this many seconds. Use to respect DNS/load-balancer changes.
  - **ConnectTimeoutSeconds:** Range 1–120. Default: 21. **Effect:** Max time to establish TCP/TLS. If exceeded, the connection attempt fails (no resilience retry for connect itself).
- **Use case:**  
  When you make an API call, your app first needs a TCP connection to the server. **Connection** controls how many connections are kept to each server, how long idle ones stay open, and how long to wait when *establishing* a new connection. For example: if the server is down, **ConnectTimeoutSeconds** means “give up after X seconds” instead of hanging. If you call the same API a lot, **MaxConnectionsPerServer** lets more requests share connections instead of opening new ones every time.

---

## Timeout

- **What it does:** Two timeouts: **total request timeout** (whole call, including retries) and **per-attempt timeout** (single try; when exceeded, that try is aborted and may be retried).
- **Implementation:** Maps to `TotalRequestTimeout` and `AttemptTimeout` in the resilience options. Per-client override via `requestTimeoutSeconds` in the extension methods.
- **Options and effect:**
  - **TotalRequestTimeoutSeconds:** Range 1–600. Default: 30. **Effect:** Hard limit for the whole operation (all attempts and retries). When exceeded, the request fails. Caller never waits longer than this.
  - **AttemptTimeoutSeconds:** Range 1–300. Default: 10. **Effect:** Limit for a *single* attempt. When exceeded, that attempt is aborted and may be retried (subject to total timeout and retry count).
- **Use case:**  
  When you make an API call, **AttemptTimeoutSeconds** is the limit for *one* try: if the server doesn’t respond in that time, that attempt is cancelled and (if retry is on) another try may happen. **TotalRequestTimeoutSeconds** is the limit for the *entire* operation (all tries together). So the user or caller never waits longer than the total timeout; and each attempt is cut short by the attempt timeout so you don’t waste time on a single stuck request.

---

## Retry

- **What it does:** Retries failed attempts (transient failures) with configurable count, delay, backoff type, jitter, and optional use of the `Retry-After` header.
- **Implementation:** Maps to `HttpRetryStrategyOptions` (MaxRetryAttempts, Delay, BackoffType, UseJitter, ShouldRetryAfterHeader). Backoff: Constant, Linear, or Exponential.
- **Options and effect:**
  - **MaxRetryAttempts:** Range 0–10. Default: 3. **Effect:** Number of retries after the first failure. 0 = no retries; higher = more chances before failing.
  - **BaseDelaySeconds:** Range 1–60. Default: 2. **Effect:** Base delay between retries (seconds). Actual delay depends on **BackoffType** (e.g. exponential: 2s, 4s, 8s).
  - **BackoffType:** Options: `Constant`, `Linear`, `Exponential` (default). **Effect:** How delay grows: Constant = same delay every time; Linear = delay × attempt; Exponential = delay × 2^attempt (standard for backing off).
  - **UseJitter:** Options: `true` (default), `false`. **Effect:** When true, random jitter is added to delays so many clients don’t retry at once (reduces thundering herd).
  - **UseRetryAfterHeader:** Options: `true` (default), `false`. **Effect:** When true, if the response has a `Retry-After` header (e.g. 429/503), that value is used for the next retry delay.
- **Use case:**  
  When you make an API call and it fails (e.g. network blip or server returns 503), **Retry** can automatically try again a few times instead of failing immediately. You choose how many retries (**MaxRetryAttempts**), how long to wait between them (**BaseDelaySeconds**, **BackoffType**), and whether to add randomness (**UseJitter**) so many clients don’t all retry at the same second. If the API sends a **Retry-After** header (e.g. “try again in 60 seconds”), **UseRetryAfterHeader** lets the pipeline respect that. Set **MaxRetryAttempts** to **0** if you don’t want any retries.

  **Guidance:**

  - For **interactive/UI paths**, keep `MaxRetryAttempts` low (0–3) and `BaseDelaySeconds` modest (1–3 seconds) so users are not blocked for long.
  - For **background or batch jobs**, you can afford higher retry counts and longer delays, but always ensure the combination of retries and delays still fits within `Timeout:TotalRequestTimeoutSeconds`.
  - When combining **hedging** and **retries**, remember that both features increase load on the dependency. Use conservative retry counts in hedged pipelines to avoid excessive amplification of traffic.

---

## CircuitBreaker

- **What it does:** Stops sending requests to a failing dependency when the failure ratio in a sampling window exceeds a threshold; after a break duration, it allows a trial request (half-open).
- **Implementation:** Maps to `HttpCircuitBreakerStrategyOptions`: MinimumThroughput, FailureRatio, SamplingDuration, BreakDuration. Applied in both standard and hedging pipelines (per-endpoint in hedging). Configuration keys use the same names as the options (MinimumThroughput, FailureRatio, SamplingDurationSeconds, BreakDurationSeconds) for consistent binding from configuration or JSON.
- **Options and effect:**
  - **MinimumThroughput:** Range 1–100. Default: 100. **Effect:** Minimum requests in the sampling window before the circuit can open. Prevents opening on low traffic (e.g. 2 failures out of 3 calls).
  - **FailureRatio:** Range 0.01–1.0. Default: 0.1. **Effect:** Fraction of requests that may fail (e.g. 0.1 = 10%). Above this in the window, the circuit opens.
  - **SamplingDurationSeconds:** Range 1–600. Default: 30. **Effect:** Time window (seconds) over which failure ratio is computed. Shorter = faster to open; longer = less flapping.
  - **BreakDurationSeconds:** Range 1–300. Default: 5. **Effect:** How long (seconds) the circuit stays open before one trial request (half-open). Gives the backend time to recover.
- **Use case:**  
  When you make many API calls and the backend starts failing (e.g. 50% of requests fail in the last 30 seconds), the **circuit breaker** “opens”: it stops sending new requests for a while (**BreakDurationSeconds**) instead of hammering a broken service. After that time, it sends one trial request; if that succeeds, it closes the circuit and traffic flows again. So your app fails fast when the dependency is down instead of wasting time and resources on requests that are likely to fail.

---

## RateLimiter (optional)

- **What it does:** Limits how many requests can be sent per time window. When **RateLimiter:Enabled** is `true`, applied in both **standard** and **hedging** pipelines (in hedging, a separate rate-limit handler wraps the hedging handler).
- **Implementation:** Uses `System.Threading.RateLimiting`: FixedWindow, SlidingWindow, or TokenBucket. Permit limit, window/period, and queue limit configurable per algorithm.
- **Options and effect:**
  - **Enabled:** Options: `true`, `false` (default). **Effect:** When true, rate limiting is applied in both standard and hedging pipelines. When false, no rate limit.
  - **PermitLimit:** Range 1–100000. Default: 1000. **Effect:** Max requests allowed per window. Combined with **WindowSeconds** (e.g. 1000 per 1s = 1000 req/s).
  - **WindowSeconds:** Range 1–3600. Default: 1. **Effect:** Length of the rate-limit window in seconds. For SlidingWindow, window is split by **SegmentsPerWindow**.
  - **QueueLimit:** Range 0–10000. Default: 0. **Effect:** When limit is exceeded, how many requests can wait (0 = fail immediately; &gt;0 = queue up to that many).
  - **Algorithm:** Options: `FixedWindow` (default), `SlidingWindow`, `TokenBucket`. **Effect:** FixedWindow = simple “X permits per window”; SlidingWindow = smoother, avoids boundary spikes; TokenBucket = sustained average rate with burst capacity.
  - **SegmentsPerWindow:** Range 1–100. Default: 2. **Effect:** Only for SlidingWindow: how many segments the window is split into.
  - **TokenBucketCapacity**, **TokensPerPeriod**, **ReplenishmentPeriodSeconds:** Used only for TokenBucket. **Effect:** Bucket size, refill amount, and refill interval (e.g. 1000 tokens, 1000 per 1s = 1000 req/s sustained).
- **Use case:**  
  When you make API calls and the backend (or your quota) only allows e.g. 100 requests per second, **RateLimiter** ensures you don’t exceed that. Works with both standard and hedging pipelines. If you send too many in a window, extra requests either wait in a queue (if **QueueLimit** &gt; 0) or fail. You choose the algorithm (e.g. **FixedWindow** for simple “X requests per second”, **TokenBucket** for a smoother average rate).

---

## Fallback (optional)

- **What it does:** On total failure (after retries/hedging), returns a **synthetic response** (status code and optional body) or a **custom response** from an **IHttpFallbackHandler**. Only when **Fallback:Enabled** is `true`.
- **Implementation:** Polly fallback handler when the inner pipeline fails or returns non-success. Restrict to 5xx + exceptions via **OnlyOn5xx**. When a custom **IHttpFallbackHandler** is passed to `AddHttpClientWithResilience`, it is invoked first; if it returns a response, that is used; otherwise the synthetic response from options is used. The synthetic response has **RequestMessage** set from the failed outcome's request when the outcome was a failed `HttpResponseMessage`, so logging and telemetry can correlate the fallback response to the original request.
- **Options and effect:**
  - **Enabled:** Options: `true`, `false` (default). **Effect:** When true, on total failure the pipeline returns a synthetic (or custom) response instead of throwing. When false, failures propagate as exceptions.
  - **StatusCode:** Range 400–599. Default: 503. **Effect:** HTTP status code of the synthetic response when no custom handler is used or the custom handler returns null.
  - **OnlyOn5xx:** Options: `true`, `false` (default). **Effect:** When true, fallback only for 5xx responses and exceptions; 4xx are returned as-is. When false, fallback for any non-success (including 4xx).
  - **ResponseBody:** Optional string. Default: null. **Effect:** Plain-text body of the synthetic response; null = empty body.
- **Custom fallback:** Implement **IHttpFallbackHandler** (e.g. call another URL, return cached value, custom logic). Pass an **instance** to `AddHttpClientWithResilience(..., fallbackHandler: handler)`. Resolve the handler from DI when configuring the client if needed (e.g. in a context where you have `IServiceProvider`). **HttpFallbackContext** provides the failed outcome (result or exception).
- **Use case:**  
  When you make an API call and *everything* fails, the pipeline can return a synthetic response (e.g. 503) or your custom handler can return a cached/default response. **OnlyOn5xx** means “only do this for server errors and exceptions; if the API returns 400 Bad Request, let that through.”

---

## Hedging

- **What it does:** Sends multiple requests (original + hedged attempts after a delay) and uses the **first successful** response. **Chosen by PipelineType** set to `Hedging` in config (same extension `AddHttpClientWithResilience`).
- **Implementation:** Configures `HttpStandardHedgingResilienceOptions`: TotalRequestTimeout, Hedging.Delay, Hedging.MaxHedgedAttempts, and per-endpoint Timeout and CircuitBreaker. When **RateLimiter:Enabled** is true, a rate-limit handler is also applied (hedging + rate limiting supported).
- **Options and effect:**
  - **DelaySeconds:** Range 0–60. Default: 2. **Effect:** Seconds to wait before sending an extra hedged request. 0 = send hedges immediately with the first request; higher = give the first attempt a chance before adding load.
  - **MaxHedgedAttempts:** Range 0–10. Default: 1. **Effect:** Number of *extra* attempts (total = 1 + this). 0 = no hedging (only first request); 1 = one backup request; higher = more parallel attempts for lower tail latency.
- **Use case:**  
  When you make an API call and you care a lot about **latency** (e.g. the same API is behind a load balancer with several replicas), set **PipelineType** to **Hedging** in config. The pipeline then sends a first request, then after **DelaySeconds** maybe sends a second (and more) to another replica. Whichever responds first successfully wins; the others are cancelled.

---

## Bulkhead (optional)

- **What it does:** Limits **concurrent** outbound requests. Only when **Bulkhead:Enabled** is `true`. Extra requests wait in queue up to **QueueLimit** or fail.
- **Implementation:** Polly concurrency limiter (limit + queue limit) around the inner pipeline.
- **Options and effect:**
  - **Enabled:** Options: `true`, `false` (default). **Effect:** When true, concurrent outbound requests are capped at **Limit**. When false, no concurrency limit.
  - **Limit:** Range 1–1000. Default: 100. **Effect:** Max number of requests that can be in flight at once. Extra requests wait (if **QueueLimit** &gt; 0) or fail immediately.
  - **QueueLimit:** Range 0–10000. Default: 0. **Effect:** Max requests that can wait for a slot. 0 = no queue (fail when at limit); &gt;0 = queue up to this many before failing.
- **Use case:**  
  When you make many API calls at once (e.g. 1000 users each calling the same backend), **Bulkhead** caps how many of those calls can be “in flight” at the same time (e.g. **Limit** = 50). The 51st request either waits in a queue (if **QueueLimit** &gt; 0) or fails. So one slow or broken backend doesn’t tie up all your threads or connections; the rest of your app can still do other work. Think of it as “only N calls to this API at a time.”

---

## PipelineOrder

- **What it does:** When both **Fallback** and **Bulkhead** are enabled and **PipelineStrategyOrder** is not set, controls whether Fallback runs first (then Bulkhead) or Bulkhead first (then Fallback). Ignored when **PipelineStrategyOrder** is set.
- **Options:** `FallbackThenConcurrency` (default), `ConcurrencyThenFallback`.  
  **Effect:** **FallbackThenConcurrency** = Fallback handler is outermost, then Bulkhead, then the rest of the pipeline. **ConcurrencyThenFallback** = Bulkhead is outermost, then Fallback. Usually keep default so fallback wraps everything.
- **Use case:**  
  When you have both fallback and bulkhead on and don’t use **PipelineStrategyOrder**, this decides order. For full control over order (including RateLimiter and core), use **PipelineStrategyOrder** instead.

---

## PipelineStrategyOrder (optional)

- **What it does:** Explicit order of outer strategies from **outermost to innermost**. Allowed values: `Fallback`, `Bulkhead`, `RateLimiter`, `Standard`, `Hedging`. The list must contain **exactly one** of `Standard` or `Hedging`. When null or empty, **PipelineOrder** and **PipelineType** determine behavior.
- **Options:** Array of strategy names, e.g. `[ "Fallback", "Bulkhead", "RateLimiter", "Standard" ]`. Config key: `PipelineStrategyOrder`.
- **Effect:** Handlers are added in reverse order (innermost first), so the first element is the outermost. Enables e.g. RateLimiter between Bulkhead and Standard, or a custom order without changing code.
- **Use case:**  
  When you need a specific pipeline order (e.g. Fallback → Bulkhead → RateLimiter → Hedging), set **PipelineStrategyOrder** in config. Each strategy is only added if enabled (Fallback.Enabled, Bulkhead.Enabled, RateLimiter.Enabled).

> **Inner pipeline order (advanced, code-first)**
>
> For most apps, the inner order of the standard pipeline (total timeout, attempt timeout, retry, circuit breaker, rate limiter) comes from Microsoft's `AddStandardResilienceHandler` via `HttpStandardResilienceOptions`. When you need **full control** over the inner order (e.g. retry outside circuit breaker, different timeout placement), use the advanced overload:
>
> ```csharp
> services.AddHttpClient("MyClient", _ => { })
>     .AddHttpClientWithResilience(
>         configuration,
>         requestTimeoutSeconds: 30,
>         fallbackHandler: null,
>         configureInnerPipeline: inner =>
>         {
>             inner
>                 .AddRetry(new HttpRetryStrategyOptions { /* ... */ })
>                 .AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions { /* ... */ })
>                 .AddTimeout(new HttpTimeoutStrategyOptions { /* ... */ });
>         });
> ```
>
> This overload bypasses `AddStandardResilienceHandler` and lets you construct the inner pipeline directly with `ResiliencePipelineBuilder<HttpResponseMessage>`, mirroring Polly/Microsoft code-first control. In this mode, config keys like `PipelineType`, `PipelineOrder`, and `PipelineStrategyOrder` are not applied to the inner pipeline; treat it as an escape hatch for advanced scenarios.
>
> When using a **per-tenant or per-client** configuration section with a custom inner pipeline, use the overload that accepts **IConfigurationSection** so the primary handler (connection pool, timeouts) is built from that section: `AddHttpClientWithResilience(tenantSection, requestTimeoutSeconds: null, fallbackHandler: null, configureInnerPipeline: inner => { ... })`.

---

## PipelineSelection (optional)

- **What it does:** When **Mode** is `ByAuthority`, a **separate pipeline instance** is used per request authority (scheme + host + port). Same options apply; state (e.g. circuit breaker) is per authority.
- **Options:** **Mode:** `None` (default), `ByAuthority`. Config key: `PipelineSelection:Mode`.
- **Effect:** **None** = one pipeline per named client. **ByAuthority** = one pipeline instance per distinct authority so e.g. circuit breakers don’t share state across different hosts.
- **Use case:**  
  When the same HttpClient is used to call multiple hosts (e.g. dynamic URLs or several downstream services), set **PipelineSelection:Mode** to **ByAuthority** so each host has its own circuit breaker and resilience state.

---

## Custom pipeline (code)

- **What it does:** The overload `AddHttpClientWithResilience(..., configurePipeline: b => { })` lets you add **extra resilience handlers** (outermost) after the built-in pipeline. Use to add custom Polly strategies (e.g. logging, custom retry).
- **Use case:**  
  When you need strategies not covered by options (e.g. a custom handler that logs failures), pass **configurePipeline** and call `b.AddResilienceHandler("name", ...)` on the builder. Handlers added there execute first (outermost).

---

## Validation and type-safe options

- Options with a fixed set of values are **enums** in code for type safety: **RetryBackoffType** (Constant, Linear, Exponential), **RateLimitAlgorithm** (FixedWindow, SlidingWindow, TokenBucket), **PipelineOrderType** (FallbackThenConcurrency, ConcurrencyThenFallback), **PipelineSelectionMode** (None, ByAuthority), **ResiliencePipelineType** (Standard, Hedging). Config keys and values are unchanged (e.g. `"BackoffType": "Exponential"`, `"Algorithm": "FixedWindow"`).
- Options are validated at **startup** when using `AddHttpResilienceOptions`: data annotations (ranges) and `Enum.IsDefined` for those enums, plus `PipelineStrategyOrder` (exactly one of Standard/Hedging, only allowed names). Invalid configuration throws so misconfiguration fails fast.

---

## Summary table

| Section       | Always applied when root Enabled? | Use case focus                          |
|---------------|------------------------------------|-----------------------------------------|
| Connection    | Yes                                | Pool size, connect/timeouts             |
| Timeout       | Yes                                | Total vs per-attempt timeout             |
| Retry         | Yes (disable via MaxRetryAttempts=0) | Transient failures, backoff, jitter   |
| CircuitBreaker| Yes                                | Stop calling failing dependencies       |
| RateLimiter   | No (RateLimiter:Enabled)           | Quota / rate limits (standard and hedging) |
| Fallback      | No (Fallback:Enabled)              | Synthetic or custom response on total failure |
| Hedging       | When PipelineType is Hedging      | Lower tail latency, multiple replicas  |
| Bulkhead      | No (Bulkhead:Enabled)              | Limit concurrent outbound requests       |

---

## Telemetry and metrics enrichment

- **What it does:** Adds a minimal Microsoft-style telemetry enrichment layer on top of Polly telemetry, so resilience metrics carry standard tags for downstream analysis.
- **Implementation:** Uses Polly's `TelemetryOptions` and a custom `HttpResilienceMeteringEnricher`:
  - `HttpResilienceMeteringEnricher` derives from `Polly.Telemetry.MeteringEnricher`.
  - It is registered via `AddHttpResilienceTelemetry`, which configures:
    - `services.Configure<TelemetryOptions>(options => options.MeteringEnrichers.Add(new HttpResilienceMeteringEnricher()));`
  - The shared `OpenTelemetry.NET` package collects the `Polly` meter by calling `metrics.AddMeter("Polly")` in its metrics configuration, so all `resilience.polly.*` instruments (strategy events, attempt duration, pipeline duration) are exported alongside existing HTTP/server metrics.
- **Tags added by the enricher:**
  - **`error.type`**  
    - When an exception is present on the outcome: full CLR type name (e.g. `System.TimeoutException`, `System.Net.Http.HttpRequestException`).  
    - When there is no exception but the result is an `HttpResponseMessage` with non-success status: a string such as `HttpStatusCode.500`.
  - **`request.name`**  
    - Prefer `ResilienceContext.OperationKey` when set (friendly, logical operation name controlled by the caller).  
    - Otherwise, derived from Polly tags such as `pipeline.name` and `strategy.name` (e.g. `my-http-pipeline/Retry`).
  - **`request.dependency.name`**  
    - For HTTP outcomes, derived from the target URI using `scheme://host[:port]` (e.g. `https://api.example.com`, `http://orders.internal:8080`).  
    - If no HTTP request information is available, falls back to `pipeline.name` when present.
- **How to enable telemetry in an app:**
  - Register resilience options and telemetry:
    - `services.AddHttpResilienceOptions(configuration);`
    - `services.AddHttpResilienceTelemetry();`
  - Configure OpenTelemetry via the shared package (in the hosting app):
    - `builder.AddObservability();` (from `OpenTelemetry.NET`, which already sets up metrics, tracing, and exporters and adds the `Polly` meter).
  - Register HTTP clients using the existing extension:
    - `services.AddHttpClient("MyClient").AddHttpClientWithResilience(configuration);`
- **Effect:** All resilience events from standard and hedging handlers (retry, circuit breaker, timeout, rate limiter, fallback, hedging, bulkhead) emit Polly metrics enriched with `error.type`, `request.name`, and `request.dependency.name`. These can be used in dashboards and alerts for:
  - Grouping failures by dependency (`request.dependency.name`).
  - Grouping by logical operation (`request.name`).
  - Breaking down failure categories (`error.type` by exception type or HTTP status).

For **how to consume** the package (NuGet, configuration, code), see the [README](../README.md).
