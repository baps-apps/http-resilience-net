## Troubleshooting

This document lists common issues when adopting `HttpResilience.NET` and how to diagnose and fix them.

---

### Startup fails with OptionsValidationException

**Symptom**: Application fails to start with `OptionsValidationException` for `HttpResilienceOptions`.

- **Likely causes**:
  - `HttpResilienceOptions:Enabled` is `true` (validation only runs when the feature is enabled).
  - A value is out of range (e.g. `Connection:MaxConnectionsPerServer = 0` or `> 1000`; `CircuitBreaker:MinimumThroughput` outside 1–100).
  - `Timeout:AttemptTimeoutSeconds` is greater than `Timeout:TotalRequestTimeoutSeconds`.
  - An enum value is invalid (e.g. `Retry:BackoffType = "Exponetial"` typo).
  - `PipelineStrategyOrder` is invalid:
    - Contains an unknown entry.
    - Does not contain exactly one of `Standard` or `Hedging`.
    - Contains duplicates.

- **What to do**:
  - Check the exception message; it is written to explain exactly which rule failed.
  - Compare your configuration against `IMPLEMENTATION.md` and `RECIPES.md`.
  - Fix typos (e.g. `"Exponential"` vs `"Exponetial"`) and ensure allowed ranges.
  - If you need to temporarily bypass validation (e.g. debugging), you can set `"HttpResilienceOptions:Enabled" = false`; this disables both the resilience pipeline and startup validation. Do **not** leave it disabled in production unintentionally.

---

### Requests behave as if there is no resilience

**Symptom**: Timeouts, retries, circuit breakers, etc. do not seem to be applied.

- **Checklist**:
  - Ensure `"HttpResilienceOptions:Enabled" = true`.
  - Confirm that you called:
    - `services.AddHttpResilienceOptions(configuration);`
    - `services.AddHttpClient("MyClient").AddHttpClientWithResilience(configuration);` or the section-based overload.
  - Verify that the named client you are using matches the registration (e.g. `CreateClient("MyClient")`).
  - Ensure that you are not overriding the `HttpClient`'s `Timeout` in a way that conflicts with pipeline timeouts.

---

### Timeouts fire sooner or later than expected

**Symptom**: Requests time out earlier than expected, or appear to hang longer than the configured total timeout.

- **Things to check**:
  - `Timeout:TotalRequestTimeoutSeconds` vs `Timeout:AttemptTimeoutSeconds`:
    - Each attempt is bounded by `AttemptTimeoutSeconds`.
    - All attempts together are bounded by `TotalRequestTimeoutSeconds`.
  - Retries:
    - Remember that retries + delays must fit within the **total** timeout.
  - Connect timeout:
    - `Connection:ConnectTimeoutSeconds` controls TCP/TLS establishment only; application-level timeouts apply after connection is acquired.

- **Guidance**:
  - Keep `AttemptTimeoutSeconds` lower than or equal to `TotalRequestTimeoutSeconds` (this is now validated).
  - For interactive calls, consider `TotalRequestTimeoutSeconds` in the range 5–30 seconds.

---

### Circuit breaker opens “too often” or “never”

**Symptom**: Circuit breaker opens aggressively on small bursts, or never opens despite many failures.

- **Parameters to tune**:
  - `CircuitBreaker:MinimumThroughput`:
    - Too low: breaker may open on a handful of failures.
    - Too high: breaker may never see enough traffic to open.
  - `CircuitBreaker:FailureRatio`:
    - Lower = more sensitive; higher = more tolerant.
  - `CircuitBreaker:SamplingDurationSeconds`:
    - Shorter window = more responsive but more “flappy”.
  - `CircuitBreaker:BreakDurationSeconds`:
    - How long the circuit stays open before trying again.

- **Use telemetry**:
  - Check failure rate per `request.dependency.name` and breaker-open events (see `OPERATIONS.md` for metrics).

---

### Unexpected fallback responses

**Symptom**: Responses with fallback status/message appear when you did not expect them, or you get exceptions instead of fallback.

- **Checklist**:
  - `Fallback:Enabled` must be `true`.
  - `Fallback:OnlyOn5xx`:
    - `true`: fallback only for 5xx and exceptions; 4xx pass through.
    - `false`: any non-success status may trigger fallback.
  - If you use a custom `IHttpFallbackHandler`:
    - Remember that if it returns `null`, the synthetic fallback response (status/body from options) is used.

---

### Rate limiter or bulkhead “blocking” too much traffic

**Symptom**: Many requests are failing quickly with limit errors, or waiting unexpectedly in queues.

- **Parameters to review**:
  - `RateLimiter:PermitLimit` and `WindowSeconds`:
    - Ensure they match your backend’s allowed throughput.
  - `RateLimiter:QueueLimit`:
    - `0` means fail immediately when limit is reached.
  - `Bulkhead:Limit` and `QueueLimit`:
    - Limit is concurrent in-flight requests.
    - Queue is how many can wait.

- **Mitigation**:
  - Increase limits cautiously, monitoring downstream health.
  - For critical APIs, prefer smaller queues to avoid long waits.

---

### Telemetry tags missing or incomplete

**Symptom**: You do not see `error.type`, `request.name`, or `request.dependency.name` on resilience metrics.

- **Checklist**:
  - Did you call `services.AddHttpResilienceTelemetry();`?
  - Does your telemetry bootstrap include the Polly meter (`metrics.AddMeter("Polly")`)?
  - Are you looking at the right aggregation window and environment?

- **Notes**:
  - `request.name` prefers `ResilienceContext.OperationKey` when set; otherwise uses pipeline/strategy names.
  - `request.dependency.name` requires access to the `HttpRequestMessage.RequestUri` (available for HTTP outcomes).

