# Runbook

## Alert: circuit breaker open

**Symptom.** `http.resilience.circuit_breaker.state` is above 0; callers see `BrokenCircuitException`.

Not "the health endpoint returns 503" — it does not. `Degraded` maps to HTTP **200** in ASP.NET Core's default
`ResultStatusCodes`, so the status code of `/healthz/deps` is green with every circuit in the process open. The
gauge is the signal; the endpoint is for triage.

1. Read `/healthz/deps`. The `data` payload names each breaker as `client -> authority`, so you know which dependency and, under `ByAuthority`, which host.
2. Confirm the dependency is genuinely unhealthy before touching anything here. An open breaker is a symptom.
3. If the dependency is healthy and the breaker is wrong, the thresholds are mistuned — see breaker thrashing below.

**Do not** restart pods to clear breaker state. Breakers are process-local; a rolling restart resets every one of them simultaneously and sends a synchronized burst at a dependency that is already failing.

## Alert: retry rate elevated

**Symptom.** Retry attempts exceed ~10% of requests.

1. You are amplifying load on something that is degrading. Check whether the dependency is recovering or getting worse.
2. If it is getting worse and you are a significant share of its traffic, reduce `Retry:MaxRetries` or set `Retry:Enabled` to false and deploy. Both require a restart.
3. Compute what you are actually sending: `replicas × RPS × (1 + MaxRetries)`. See [OPERATIONS.md](OPERATIONS.md#retry-amplification).

## Alert: breaker thrashing

**Symptom.** Repeated open/close transitions.

Usually one of:

- `MinimumThroughput` too low for the traffic, so a handful of failures trips it.
- `BreakDuration` too short, so trial traffic hits a dependency that has not recovered.
- `SamplingDuration` too short relative to the dependency's latency.

Remember these thresholds are per replica. A value tuned for aggregate traffic will trip far too easily once divided across the fleet.

## Alert: rate limiter rejections

**Symptom.** `RateLimiterRejectedException` reaching callers.

1. `PermitLimit` is a per-replica number. Check `replicas × PermitLimit` against the real downstream quota — the most common cause is a limit that was set to the global quota and then multiplied by autoscaling.
2. If the replica count grew, the per-replica limit needs to shrink.
3. Do not raise `QueueLimit` to hide it. A persistently full queue means the downstream needs more capacity or the caller needs to shed load. It is capped at 1,000 for that reason.
4. If the client has `RateLimiter:Enabled` false, this is the concurrency backstop, not a rate limit: concurrency reached `ConcurrencyLimiter:Backstop` and there is no queue above it. Check whether the dependency slowed down before raising the number.

## Symptom: `TimeoutRejectedException`

The pipeline's attempt or total timeout fired.

- Attempt timeouts: `Timeout:Attempt` is tighter than the dependency's real latency. Compare against its p99.
- Total timeouts: the whole logical request, including retries and backoff, exceeded `Timeout:Total`.

If you see a bare `TaskCanceledException` instead, the caller cancelled — that is not a pipeline timeout, and it is deliberately not counted as a circuit breaker failure.

## Symptom: startup fails with a validation error

Working as designed. The message names the configuration path, the value, what was expected and why:

```text
HttpResilience:Clients:Orders -- Timeout.Total: value '00:00:10' is invalid.
Expected at least 00:00:10.5000000. Reason: 3 attempts of 00:00:03 plus
00:00:01.5000000 of Exponential backoff cannot fit in the total budget, so the
configured retries would be cut short and never run.
```

Fix the configuration. Every one of these rules exists because the alternative is a failure on the first live request instead.

## Symptom: a POST reached the origin more than once

This package does not retry or hedge POST, PUT, PATCH or DELETE by default. Check for:

**Start with the startup logs.** Every client that can repeat a mutating request logs event ID 10 at Warning when the host starts, naming the client, the methods and the configuration key that allowed it. If that line is present for this client, the cause is in the line. If it is absent, this package did not do it.

- `Retry:RetryableMethods` naming the method (event 10 names it, and names the section it is stated in — which may be the root, not the client's)
- `Retry:DisableForUnsafeHttpMethods` set to false on that client's section (event 10 names it; it cannot be set at the root — startup fails)
- `Hedging:DisableForUnsafeHttpMethods` set to false on a client registered with `AddHedgedHttpResilience` (event 10 names it)
- a retry loop in application code above the client — **the remaining cause when event 10 is absent**

## Symptom: p99 pinned at `Timeout:Client`, but retries and the circuit breaker look normal

The dependency is answering headers and then stalling or trickling the response body.

`Timeout:Total` stops applying when headers arrive, so the body transfer is bounded only by `Timeout:Client`. When that fires, the pipeline sees caller cancellation — which is deliberately not a breaker failure and not retryable — so the breaker stays closed, nothing is retried, and Polly records each attempt as a fast success.

1. Confirm: bare `TaskCanceledException` with no `TimeoutRejectedException` alongside it, and `http.client.request.duration` at `Timeout:Client` while `resilience.polly.strategy.attempt.duration` stays low.
2. Check the dependency's own response-body timing, not its time-to-first-byte.
3. Mitigate in the caller: `HttpCompletionOption.ResponseHeadersRead` and your own deadline on reading the stream. Lowering `Timeout:Client` shortens the hold but does not make the failure visible to the pipeline.

See [OPERATIONS.md](OPERATIONS.md#the-failure-the-resilience-signals-cannot-see).

## Symptom: a hedged client rejects requests to a new host

`HttpRequestException` naming `PipelineSelection:Authorities`. Hedged clients allocate a breaker, a limiter and a metric series per authority for the life of the process, so their destinations are fixed at deploy time. Add the authority to the client's list and restart.

## Symptom: throughput capped below expectations

Check `Connection:MaxConnectionsPerServer`. Requests queue inside the connection pool, below the resilience pipeline, so the wait shows up as latency without appearing in retry or timeout telemetry. Unset means unlimited, which is the default.

## Escalation

Include: the client name, the configuration section it binds, the dependency health payload, retry and breaker metrics for the window, and whether the dependency is independently known to be degraded.

## Alert: limiter queue filling

**Symptom.** `http.resilience.limiter.queued_requests` is above 0 and sustained, for any `kind`.

1. Read the `kind` tag. `rate` means requests are waiting for a permit; `concurrency` means they are waiting for a slot; `backstop` never queues.
2. **That wait is outside `Timeout:Total`.** Only `Timeout:Client` and the caller's `CancellationToken` bound it, so a filling queue shows up as latency no pipeline timeout explains and no retry counter reflects.
3. Check `available_permits` for the same `kind`. At 0 the next request queues; the queue depth is how far behind you are.
4. Do **not** raise `QueueLimit` to make the rejections stop. That moves the latency somewhere nothing in this package can bound and holds a request buffer per queued call. Either the dependency slowed down, or the budget is genuinely too small — raise `PermitLimit` or `ConcurrencyLimiter:Limit`, having divided by `replicas × clients` first.

## Notice: two nested pipelines (event 12)

**Symptom.** Log line at Information: *"client 'X' has N resilience handlers where this package added M"*.

1. Find the client's registration. If the extra handler came from `AddResilienceHandler`, this is expected and correct — the notice cannot tell the two apart, which is why it is Information and not a Warning.
2. If it came from `AddStandardResilienceHandler` or `AddStandardHedgingHandler` on a client that already calls `AddHttpResilience`, two pipelines are **nested**. Retries multiply rather than add: three configured attempts become nine origin calls, and the total timeout is applied twice.
3. Remove the platform call. Use `AddResilienceHandler` for anything the schema does not express.
4. Check the origin's traffic for the affected client before and after. A nested pipeline is a 3× amplifier that no retry metric distinguishes from a genuinely retried request.
