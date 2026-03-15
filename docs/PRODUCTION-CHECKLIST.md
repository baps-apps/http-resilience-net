# Production Checklist

Use this checklist before deploying applications that use `HttpResilience.NET` to production.

---

## Configuration

- [ ] **Resilience enabled where intended**  
  Set `HttpResilienceOptions:Enabled = true` for any environment or client that should use the resilience pipeline. If `Enabled` is `false`, no retries, circuit breaker, or timeouts from this package are applied and **startup validation is skipped entirely**, so invalid values in `HttpResilienceOptions` will not fail-fast.

- [ ] **Timeouts set**  
  Configure `Timeout:TotalRequestTimeoutSeconds` and `Timeout:AttemptTimeoutSeconds` so callers never wait indefinitely. Keep `AttemptTimeoutSeconds` ≤ `TotalRequestTimeoutSeconds`. See [TROUBLESHOOTING.md](TROUBLESHOOTING.md) and [IMPLEMENTATION.md](IMPLEMENTATION.md).

- [ ] **Circuit breaker tuned**  
  Review `CircuitBreaker:MinimumThroughput` (1–100), `FailureRatio`, `SamplingDurationSeconds`, and `BreakDurationSeconds` for each dependency so the circuit opens when appropriate without flapping. See [OPERATIONS.md](OPERATIONS.md) for alerts and dashboards.

- [ ] **No secrets in config**  
  Do not put secrets in hostnames, `Fallback:ResponseBody`, or other resilience config. See [SECURITY-GOVERNANCE.md](SECURITY-GOVERNANCE.md).

- [ ] **Config change control**  
  Treat `HttpResilienceOptions` as operational control plane; restrict who can change it and run changes through lower environments first. Be especially careful when toggling `Enabled` to `false`, as this also disables validation.

---

## Telemetry and operations

- [ ] **Telemetry registered**  
  Call `services.AddHttpResilienceTelemetry();` so resilience metrics are enriched with `error.type`, `request.name`, and `request.dependency.name`.

- [ ] **Polly meter collected**  
  Ensure your metrics pipeline includes the Polly meter (e.g. `metrics.AddMeter("Polly")`) and exports to your backend (Prometheus, Azure Monitor, etc.). See [OPERATIONS.md](OPERATIONS.md).

- [ ] **Alerts and runbooks**  
  Configure alerts (e.g. high failure rate per dependency, circuit breaker thrashing, excessive retries) and follow the [Runbook](RUNBOOK.md) when they fire. See [OPERATIONS.md](OPERATIONS.md#alerts).

---

## Application wiring

- [ ] **Options registered before clients**  
  Call `AddHttpResilienceOptions(configuration)` (or the section overload) once at startup before registering any `HttpClient` with `AddHttpClientWithResilience`.

- [ ] **Correct client name**  
  Ensure the code uses the same named client as registered (e.g. `CreateClient("MyClient")` matches the name passed to `AddHttpClient("MyClient", ...)`).

---

## References

- [OPERATIONS.md](OPERATIONS.md) – Telemetry, dashboards, alerts, performance
- [TROUBLESHOOTING.md](TROUBLESHOOTING.md) – Common issues and fixes
- [SECURITY-GOVERNANCE.md](SECURITY-GOVERNANCE.md) – Security and governance
- [VERSIONING.md](VERSIONING.md) – Versioning and upgrade guidance
- [RUNBOOK.md](RUNBOOK.md) – What to do when resilience alerts fire

---

## Optional future improvements

- **Multi-targeting:** Support additional .NET versions (e.g. net8.0, net9.0) if broader adoption requires it.
- **API reference:** Generate and publish API docs (e.g. DocFX) from XML comments.
- **Runbook:** Expand [RUNBOOK.md](RUNBOOK.md) as new alerts or procedures are added.
