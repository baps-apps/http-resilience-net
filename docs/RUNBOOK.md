# Runbook – Resilience Alerts and Incidents

This runbook describes what to do when **alerts** related to HttpResilience.NET fire. Pair it with the alerts defined in [OPERATIONS.md](OPERATIONS.md).

---

## Alert: High failure rate to a dependency

**Condition:** Failure rate to a single `request.dependency.name` exceeds threshold (e.g. > 2% over 5 minutes).

### Steps

1. **Identify the dependency**  
   Use the alert payload or dashboard: note `request.dependency.name` (e.g. `https://orders-api.internal`).

2. **Check dependency health**  
   - Logs and health endpoints of the downstream service.  
   - Recent deployments or config changes.  
   - Network/DNS issues between your app and the dependency.

3. **Check your metrics**  
   - Failure rate and `error.type` (e.g. `HttpStatusCode.502`, `System.TimeoutException`).  
   - Compare to baseline; check for a specific status code or exception type dominating.

4. **Mitigation**  
   - If the dependency is known to be degraded: consider enabling **Fallback** (or tightening **Fallback:OnlyOn5xx**) so callers get a controlled response instead of repeated failures.  
   - If timeouts dominate: review **Timeout:TotalRequestTimeoutSeconds** and **AttemptTimeoutSeconds**; avoid lowering them too much or callers may see more timeouts.  
   - Do **not** disable the circuit breaker unless advised; it protects the dependency from overload.

5. **Follow-up**  
   - Coordinate with the owning team of the dependency.  
   - After the incident, review **CircuitBreaker** and **Retry** settings (see [TROUBLESHOOTING.md](TROUBLESHOOTING.md)).

---

## Alert: Circuit breaker thrashing

**Condition:** Circuit opens and closes for the same dependency many times in a short window.

### Steps

1. **Confirm thrashing**  
   Check dashboards: circuit open/close events for the same `request.dependency.name` in a short period.

2. **Likely causes**  
   - Dependency is flapping (intermittent success/failure).  
   - **BreakDurationSeconds** too short: circuit closes before the backend is stable.  
   - **MinimumThroughput** or **FailureRatio** too sensitive: small bursts open the circuit, then low traffic closes it.

3. **Mitigation**  
   - Prefer fixing or stabilizing the dependency first.  
   - If tuning is needed: increase **BreakDurationSeconds** (e.g. 10–30 s) so the circuit stays open longer.  
   - Optionally increase **MinimumThroughput** or **FailureRatio** so the circuit does not open on small bursts (see [IMPLEMENTATION.md](IMPLEMENTATION.md)).

4. **Follow-up**  
   - Document the final settings and rationale.  
   - Consider enabling or tuning **Fallback** so callers get a consistent experience when the circuit is open.

---

## Alert: Excessive retries or hedged attempts

**Condition:** Sharp increase in retry or hedged attempt count for a given `request.name` or dependency.

### Steps

1. **Identify the operation**  
   Use `request.name` and `request.dependency.name` from the alert or dashboard.

2. **Check for chronic slowness or errors**  
   - High attempt count with low success rate: dependency may be degraded or timing out.  
   - Correlate with latency and error metrics.

3. **Mitigation**  
   - If the dependency is slow or failing: same as “High failure rate” (check dependency, consider Fallback).  
   - If retries are too aggressive: temporarily reduce **Retry:MaxRetryAttempts** (e.g. to 1) or **Hedging:MaxHedgedAttempts** to reduce load on the dependency.  
   - Ensure **Timeout:TotalRequestTimeoutSeconds** and **AttemptTimeoutSeconds** are set so callers do not wait excessively.

4. **Follow-up**  
   - After stability returns, restore or tune retry/hedging settings.  
   - Document any config changes in change control.

---

## General

- **Config change control:** Treat resilience config as operational control; restrict who can change it and test in lower environments first (see [PRODUCTION-CHECKLIST.md](PRODUCTION-CHECKLIST.md) and [SECURITY-GOVERNANCE.md](SECURITY-GOVERNANCE.md)).  
- **Disabling resilience:** In rare cases (e.g. debugging), set **HttpResilienceOptions:Enabled** to `false` for the affected client/environment so requests bypass the pipeline. When `Enabled` is `false`, options are still bound but **no validation or fail-fast behavior runs**, so you must treat this as a temporary bypass and re-enable once the incident is resolved.
