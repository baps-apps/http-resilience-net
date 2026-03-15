## Operations, Telemetry, and Dashboards

This document explains how to **operate** `HttpResilience.NET` in production: telemetry, dashboards, alerts, and safe defaults.

For implementation details and option semantics, see `IMPLEMENTATION.md`. For recipes and troubleshooting, see `RECIPES.md` and `TROUBLESHOOTING.md`. For a pre-go-live checklist, see [PRODUCTION-CHECKLIST.md](PRODUCTION-CHECKLIST.md).

---

### Telemetry model

- **Metrics source**: All resilience metrics are emitted by **Polly's meter** (`resilience.polly.*` instruments).
- **Enricher**: `AddHttpResilienceTelemetry` registers `HttpResilienceMeteringEnricher`, which adds:
  - `error.type`
  - `request.name`
  - `request.dependency.name`
- **Collection**: Your hosting app (or shared `OpenTelemetry.NET` package) should:
  - Add the Polly meter using the constant: `metrics.AddMeter(HttpResilienceTelemetryExtensions.PollyMeterName);` (or `"Polly"`).
  - Export metrics to your backend (Prometheus, Azure Monitor, etc.).

Code sketch:

```csharp
using HttpResilience.NET.Extensions;

services.AddHttpResilienceOptions(configuration);
services.AddHttpResilienceTelemetry();

// In your OpenTelemetry bootstrap (example only):
services.AddOpenTelemetry()
    .WithMetrics(builder =>
    {
        builder
            .AddMeter(HttpResilienceTelemetryExtensions.PollyMeterName)
            .AddAspNetCoreInstrumentation()
            .AddRuntimeInstrumentation();
        // configure exporter(s) here
    });
```

---

### Safe-by-default telemetry

- **No bodies or headers**: `HttpResilienceMeteringEnricher` never inspects or records request/response bodies or headers.
- **URIs truncated to authority**: `request.dependency.name` is derived from `scheme://host[:port]` (e.g. `https://api.example.com`). Paths and query strings are not exported.
- **Error classification only**:
  - Exceptions: `error.type = full CLR type name` (e.g. `System.TimeoutException`).
  - Non-success status: `error.type = "HttpStatusCode.<numeric>"` (e.g. `HttpStatusCode.500`).

This is usually acceptable in regulated environments because:

- No PII, secrets, tokens, or payload data are emitted by default.
- Dependency identifiers and error types are high-level operational signals.

If your organization requires stricter rules, you can:

- Avoid collecting the `Polly` meter at all in certain environments.
- Add your own custom metering enricher **instead of** (or in addition to) `HttpResilienceMeteringEnricher`.

**Named client in metrics:** The enricher does not add a tag that identifies the **named HttpClient** (e.g. `"MyClient"`) by default. To correlate metrics to a specific named client, set `ResilienceContext.OperationKey` when you have access to the resilience context (e.g. when using the pipeline directly with a known key per client). When using `HttpClient` from `IHttpClientFactory`, the pipeline creates the context internally; use a consistent logical name per client where possible (e.g. via request headers or application-level tagging) so you can group by `request.dependency.name` and other dimensions. When an explicit operation key is provided to the pipeline, the enricher uses it for `request.name`; otherwise `request.name` falls back to pipeline/strategy names. For dashboards, group or filter by `request.name` and `request.dependency.name`.

---

### Recommended dashboards

Use the tags added by the enricher to build dashboards. Typical widgets:

- **Failures by dependency**
  - Metric: `resilience.polly.pipeline.events` (or similar per your backend).
  - Group by: `request.dependency.name`, `error.type`.
  - Filter: result status/outcome = failure.

- **Circuit breaker activity**
  - Metric: counter for circuit-breaker open/half-open events.
  - Group by: `request.dependency.name`.
  - Use to detect unhealthy downstreams and noisy endpoints.

- **Retry volume and success ratio**
  - Metric: retry attempts vs successful completions.
  - Group by: `request.name` and `request.dependency.name`.
  - High retry volume with low success rate is a sign of trouble.

- **Hedging activity**
  - Metric: hedged attempt count and “first-success” latencies.
  - Group by: `request.name`.
  - Use to justify hedging settings (tail latency vs extra load).

Examples (pseudo queries, to be adapted to your metrics backend):

- Rate of failed outcomes per dependency:

> `sum(rate(resilience_polly_outcomes_total{outcome="failure"}[5m])) by (request_dependency_name, error_type)`

- Circuit breaker open count:

> `sum(rate(resilience_polly_circuit_open_total[5m])) by (request_dependency_name)`

---

### Alerts

Recommended starter alerts:

- **High failure rate to a dependency**
  - Condition: failure rate to a single `request.dependency.name` exceeds a threshold (e.g. > 2% over 5 minutes).
  - Purpose: catch downstream outages or partial degradations early.

- **Circuit breaker thrashing**
  - Condition: circuit opens/closes for the same dependency many times in a short window.
  - Purpose: identify unstable backends or badly tuned breaker settings.

- **Excessive retries or hedged attempts**
  - Condition: sharp increase in retry/hedge count for a given `request.name`.
  - Purpose: detect chronic slowness/flakiness before it becomes an outage.

Pair these alerts with the **[Runbook](RUNBOOK.md)**, which describes what to do when each alert fires (e.g. check dependency health, tune circuit breaker, enable fallback).

---

### Performance characteristics and cost

`HttpResilience.NET` is a thin configuration layer over `Microsoft.Extensions.Http.Resilience` and Polly:

- **Per-request overhead**:
  - A small number of delegating handlers and strategy invocations around the inner `SocketsHttpHandler`.
  - For most workloads this is negligible compared to network I/O.
- **Extra load from retries/hedging**:
  - Retries increase request volume to the dependency.
  - Hedging increases **concurrent** volume when extra hedged attempts are sent.

Practical guidance:

- Keep `Retry.MaxRetryAttempts` small (e.g. 0–3) for interactive traffic.
- Use hedging sparingly and only where tail latency matters and the backend can handle extra load.
- Always set **timeouts** (total + per-attempt) so callers do not wait indefinitely.

---

### Rate limiter and bulkhead lifetimes

- **Where they live**:
  - Rate limiters and bulkheads are created **per HttpClient pipeline**, not per request.
  - A limiter instance is shared across all calls from that named client within the process.
- **Lifecycle**:
  - Instances are created when the pipeline is built and live as long as the DI container / `IHttpClientFactory` does.
  - Under normal use you do not need to dispose them manually; the hosting container will dispose the pipeline when shutting down.

Implications:

- Rate and concurrency limits are **global per client instance** in the process (not per call).
- If you need different quotas for different call categories, use:
  - Different named clients with different `HttpResilienceOptions` (or different sections), or
  - `PipelineSelection:Mode = ByAuthority` if quotas should be per-host.

