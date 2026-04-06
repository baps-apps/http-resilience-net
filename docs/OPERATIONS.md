# Operations, Telemetry, and Dashboards

How to **operate** HttpResilience.NET in production: telemetry, health checks, dashboards, and alerts.

For option semantics see [IMPLEMENTATION.md](IMPLEMENTATION.md). For recipes see [RECIPES.md](RECIPES.md). For a pre-go-live checklist see [PRODUCTION-CHECKLIST.md](PRODUCTION-CHECKLIST.md).

---

## Telemetry

### Metrics enrichment

`AddHttpResilienceTelemetry()` registers `HttpResilienceMeteringEnricher`, which adds tags to Polly metrics (`resilience.polly.*` instruments):

| Tag | Source |
|-----|--------|
| `error.type` | Exception type name or `HttpStatusCode.<code>` for non-success |
| `request.name` | `ResilienceContext.OperationKey` if set, else pipeline/strategy names |
| `request.dependency.name` | `scheme://host[:port]` from request URI |

**Safe by default:** No bodies, headers, paths, or query strings are exported.

### Collection

Include the Polly meter in your metrics pipeline:

```csharp
services.AddHttpResilienceOptions(configuration);
services.AddHttpResilienceTelemetry();

services.AddOpenTelemetry()
    .WithMetrics(builder =>
    {
        builder
            .AddMeter(HttpResilienceTelemetryExtensions.PollyMeterName) // "Polly"
            .AddAspNetCoreInstrumentation();
    });
```

### Structured logging

Resilience events are logged via high-performance `LoggerMessage` source generation (zero-allocation when disabled):

| Event | Level | When |
|-------|-------|------|
| Fallback activated | Warning | Fallback produces a response |
| Retry attempt | Warning | A retry is attempted |
| Circuit breaker opened | Warning | Circuit transitions to open |
| Circuit breaker half-open | Information | Circuit allows a trial request |
| Circuit breaker closed | Information | Circuit recovers |

These are wired automatically — no additional registration needed.

---

## Health checks

`AddHttpResilienceHealthChecks()` registers a health check that reports aggregate circuit breaker state:

```csharp
services.AddHttpResilienceHealthChecks();
```

| Circuit state | Health status |
|---------------|---------------|
| All closed | Healthy |
| Any open or half-open | Degraded |

Per-client state is included in the health check data dictionary. Suitable for Kubernetes readiness probes.

---

## Recommended dashboards

Use enricher tags to build dashboards:

- **Failures by dependency** — group by `request.dependency.name`, `error.type`
- **Circuit breaker activity** — open/half-open events by `request.dependency.name`
- **Retry volume** — retry attempts vs successful completions by `request.name`
- **Hedging activity** — hedged attempt count by `request.name`

Example queries (adapt to your backend):

```
sum(rate(resilience_polly_outcomes_total{outcome="failure"}[5m])) by (request_dependency_name, error_type)
sum(rate(resilience_polly_circuit_open_total[5m])) by (request_dependency_name)
```

---

## Alerts

Recommended starter alerts:

| Alert | Condition | Purpose |
|-------|-----------|---------|
| High failure rate | Failure rate per `request.dependency.name` > 2% over 5m | Catch downstream outages early |
| Circuit breaker thrashing | Frequent open/close cycles for same dependency | Identify unstable backends or bad tuning |
| Excessive retries/hedges | Sharp increase in retry/hedge count per `request.name` | Detect chronic slowness before outage |
| Health check degraded | `AddHttpResilienceHealthChecks` returns Degraded | Any circuit breaker is open |

Pair alerts with the [Runbook](RUNBOOK.md).

---

## Performance

HttpResilience.NET is a thin configuration layer over Microsoft.Extensions.Http.Resilience and Polly:

- **Per-request overhead** — small number of delegating handlers; negligible vs network I/O.
- **Retries/hedging** increase request volume to dependencies. Keep `MaxRetryAttempts` small (0–3) for interactive traffic.
- **Rate limiters and bulkheads** are created per pipeline (not per request) and live as long as the DI container.
- Rate/concurrency limits are **global per named client** in the process. Use different named clients or `ByAuthority` mode for different quotas.
