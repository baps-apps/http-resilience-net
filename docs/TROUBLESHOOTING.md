# Troubleshooting

Common issues when adopting HttpResilience.NET and how to diagnose them.

---

## Startup fails with OptionsValidationException

**Likely causes** (validation only runs when `Enabled = true`):

- A value is out of range (e.g. `MaxConnectionsPerServer = 0`).
- `AttemptTimeoutSeconds` > `TotalRequestTimeoutSeconds`.
- An enum value is misspelled (e.g. `"Exponetial"` instead of `"Exponential"`).
- `PipelineOrder` is invalid: unknown entry, missing `Standard`/`Hedging`, duplicates, or contains both.

**What to do:**

- Check the exception message — it explains which rule failed.
- Compare config against [IMPLEMENTATION.md](IMPLEMENTATION.md).
- To temporarily bypass: set `Enabled: false` (disables both pipeline and validation). Don't leave disabled in production.

---

## Requests behave as if there is no resilience

- Ensure `Enabled = true` in config.
- Confirm both calls were made: `AddHttpResilienceOptions(configuration)` then `AddHttpClientWithResilience(configuration)`.
- Verify the named client matches (e.g. `CreateClient("MyClient")` matches `AddHttpClient("MyClient", ...)`).
- Check that `HttpClient.Timeout` isn't overriding pipeline timeouts.

---

## Timeouts fire sooner or later than expected

- Each attempt is bounded by `AttemptTimeoutSeconds`.
- All attempts together are bounded by `TotalRequestTimeoutSeconds`.
- Retries + delays must fit within the total timeout.
- `ConnectTimeoutSeconds` controls TCP/TLS establishment only.
- Keep `AttemptTimeoutSeconds` ≤ `TotalRequestTimeoutSeconds` (validated at startup).

---

## Circuit breaker opens too often or never

**Too often:**

- `MinimumThroughput` too low — opens on a handful of failures.
- `FailureRatio` too low — too sensitive.
- `SamplingDurationSeconds` too short — more reactive but flappy.

**Never opens:**

- `MinimumThroughput` too high for actual traffic volume.
- `FailureRatio` too high — too tolerant.

Use telemetry (`error.type`, `request.dependency.name`) and health checks to debug. See [OPERATIONS.md](OPERATIONS.md).

---

## Unexpected fallback responses

- Ensure `Fallback.Enabled = true` and `"Fallback"` is in `PipelineOrder`.
- `OnlyOn5xx = true` means 4xx responses pass through; only 5xx and exceptions trigger fallback.
- If using a custom `IHttpFallbackHandler` that returns `null`, the synthetic response (StatusCode/ResponseBody from config) is used as backstop.

---

## Rate limiter or bulkhead blocking too much

- Review `PermitLimit` / `WindowSeconds` against actual throughput.
- `QueueLimit = 0` means immediate rejection at limit.
- `Bulkhead.Limit` caps concurrent in-flight requests, not total.
- Increase limits cautiously while monitoring downstream health.

---

## Telemetry tags missing

- Ensure `AddHttpResilienceTelemetry()` was called.
- Ensure your metrics pipeline includes the Polly meter: `metrics.AddMeter("Polly")`.
- `request.name` prefers `ResilienceContext.OperationKey` when set; otherwise uses pipeline/strategy names.
- `request.dependency.name` requires `HttpRequestMessage.RequestUri` (available for HTTP outcomes).

---

## Health check shows Degraded

- One or more circuit breakers are open or half-open.
- Check which client by inspecting the health check data dictionary.
- Investigate the failing dependency (see [RUNBOOK.md](RUNBOOK.md)).
- The health check returns Healthy once all circuits close.
