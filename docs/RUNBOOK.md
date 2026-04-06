# Runbook — Resilience Alerts and Incidents

What to do when resilience alerts fire. Pair with alerts defined in [OPERATIONS.md](OPERATIONS.md).

---

## Alert: High failure rate to a dependency

**Condition:** Failure rate to a `request.dependency.name` exceeds threshold (e.g. > 2% over 5 minutes).

1. **Identify** the dependency from `request.dependency.name` in the alert.
2. **Check dependency health** — logs, health endpoints, recent deployments, network/DNS issues.
3. **Check metrics** — `error.type` breakdown (e.g. `HttpStatusCode.502`, `System.TimeoutException`).
4. **Mitigate:**
   - Consider enabling Fallback (or tightening `OnlyOn5xx`) for a controlled response.
   - Review timeout settings if timeouts dominate.
   - Do **not** disable the circuit breaker — it protects the dependency from overload.
5. **Follow up** — coordinate with the dependency team; review CircuitBreaker and Retry settings.

---

## Alert: Circuit breaker thrashing

**Condition:** Circuit opens and closes frequently for the same dependency.

1. **Confirm** via dashboards — open/close events for same `request.dependency.name`.
2. **Likely causes:**
   - Dependency is flapping (intermittent success/failure).
   - `BreakDurationSeconds` too short — circuit closes before backend stabilizes.
   - `MinimumThroughput` or `FailureRatio` too sensitive.
3. **Mitigate:**
   - Fix or stabilize the dependency first.
   - Increase `BreakDurationSeconds` (e.g. 10–30s).
   - Increase `MinimumThroughput` or `FailureRatio` to reduce sensitivity.
4. **Follow up** — document settings and rationale; consider enabling Fallback.

---

## Alert: Excessive retries or hedged attempts

**Condition:** Sharp increase in retry/hedge count for a `request.name` or dependency.

1. **Identify** the operation from `request.name` and `request.dependency.name`.
2. **Check** for chronic slowness or errors — correlate with latency and error metrics.
3. **Mitigate:**
   - If dependency is failing: same steps as "High failure rate" above.
   - Reduce `MaxRetryAttempts` or `MaxHedgedAttempts` temporarily to reduce load.
   - Ensure timeouts are set so callers don't wait excessively.
4. **Follow up** — restore settings after stability returns; document config changes.

---

## Alert: Health check degraded

**Condition:** `AddHttpResilienceHealthChecks` returns `Degraded`.

1. **Identify** which client(s) have open circuits — check the health check data dictionary.
2. **Check** the failing dependency's health (same as "High failure rate" steps 2–3).
3. **Mitigate** — address the underlying dependency issue. The health check returns Healthy automatically once all circuits close.
4. **Note:** If using Kubernetes readiness probes, a Degraded health check may remove the pod from the load balancer. Ensure this is the desired behavior for your service.

---

## General guidance

- **Config changes** — treat resilience config as operational control; test in lower environments first.
- **Disabling resilience** — set `Enabled: false` as a temporary bypass during debugging. This also disables validation, so re-enable once resolved.
