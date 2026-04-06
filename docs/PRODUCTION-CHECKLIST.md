# Production Checklist

Use this checklist before deploying applications that use HttpResilience.NET to production.

---

## Configuration

- [ ] `HttpResilienceOptions:Enabled = true` for environments that need resilience. When `false`, no pipeline is applied and startup validation is skipped.
- [ ] `PipelineOrder` set with the required strategies (must include exactly one of `Standard`/`Hedging`).
- [ ] `Timeout:TotalRequestTimeoutSeconds` and `AttemptTimeoutSeconds` configured. Keep `AttemptTimeoutSeconds` ≤ `TotalRequestTimeoutSeconds`.
- [ ] `CircuitBreaker` tuned (`MinimumThroughput`, `FailureRatio`, `BreakDurationSeconds`) for each dependency.
- [ ] No secrets in config — hostnames, `Fallback:ResponseBody`, or other resilience fields. See [SECURITY-GOVERNANCE.md](SECURITY-GOVERNANCE.md).
- [ ] Config change control in place — treat `HttpResilienceOptions` as operational control plane.

## Telemetry and operations

- [ ] `services.AddHttpResilienceTelemetry()` called for metrics enrichment.
- [ ] `services.AddHttpResilienceHealthChecks()` called for circuit breaker health monitoring.
- [ ] Polly meter included in metrics pipeline (`metrics.AddMeter("Polly")`).
- [ ] Alerts configured (high failure rate, circuit breaker thrashing, excessive retries). See [OPERATIONS.md](OPERATIONS.md).

## Application wiring

- [ ] `AddHttpResilienceOptions(configuration)` called once at startup before any `AddHttpClientWithResilience` calls.
- [ ] Named client matches registration (e.g. `CreateClient("MyClient")` matches `AddHttpClient("MyClient", ...)`).

## References

- [OPERATIONS.md](OPERATIONS.md) — Telemetry, dashboards, alerts
- [TROUBLESHOOTING.md](TROUBLESHOOTING.md) — Common issues
- [SECURITY-GOVERNANCE.md](SECURITY-GOVERNANCE.md) — Security guidance
- [RUNBOOK.md](RUNBOOK.md) — Incident response
