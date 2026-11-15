# Runbook

All configuration changes below need a deploy and restart.

## Circuit breaker open

**Signal.** `http_resilience.circuit_breaker.state` = 1 with `http_resilience.circuit_breaker.state="open"` (or
`half_open`); callers get `BrokenCircuitException`.

1. Read the tags: `http_resilience.client.name` and `http_resilience.authority` (`shared` for a
   single-pipeline client; the host under `ByAuthority` or on a hedged client).
2. Confirm the dependency is actually unhealthy. The open breaker is a symptom.
3. If the dependency is healthy, the thresholds are wrong; see *Breaker thrashing*.

**Do not restart pods to clear it, and never gate a probe on it.** Each new pod sends up to
`MinimumThroughput` attempts at the failing dependency before its own breaker opens.

## Breaker thrashing

**Signal.** Repeated `OnCircuitOpened` / log event 2.

- `MinimumThroughput` too low, so a few failures trip it.
- `BreakDuration` too short for the dependency to recover.
- `SamplingDuration` too short for the dependency's latency.

Thresholds apply per replica (and per authority under `ByAuthority` or hedging). Size them from one replica's
traffic.

## Retry rate elevated

**Signal.** `OnRetry` above ~10% of requests.

1. Retries add load to a degrading dependency. Is it recovering or getting worse?
2. Compute what you send: `replicas × per-replica RPS × (1 + MaxRetries)` for retried methods.
3. If you are a large share of its traffic and it is getting worse, lower `Retry:MaxRetries` or set
   `Retry:Enabled: false` (keeps timeouts and the breaker).

## `RateLimiterRejectedException`

All three limiters throw it; the log event says which (each names the key to raise):

| Event | Limiter | Check |
| --- | --- | --- |
| 8 | rate limiter | `PermitLimit` is per replica and per client. Compare `replicas × clients × PermitLimit` with the real quota; autoscaling is the usual cause. |
| 9 | concurrency limiter | The dependency probably slowed down; `queued_requests` will show it. |
| 7 | backstop (default 1,000, per authority on hedged / `ByAuthority` clients) | Same. Raise `ConcurrencyLimiter:Backstop` only if the concurrency is legitimate. |

Do not raise `QueueLimit` to hide rejections: the wait is outside `Timeout:Total` and each queued request holds
memory. Raise the budget (after dividing by `replicas × clients`) or shed load.

## Limiter queue filling

**Signal.** `http_resilience.limiter.queued_requests` > 0, sustained.

`kind` says which: `rate` (waiting for a permit) or `concurrency` (waiting for a slot); `backstop` never
queues. The wait is **outside** `Timeout:Total`, bounded only by `Timeout:Client` and the caller's token, so it
shows as latency no pipeline timeout explains. `available_permits` at 0 means the next request queues.

## `TimeoutRejectedException`

A pipeline timeout fired. `strategy.name` on the `OnTimeout` event says which:

- `Standard-AttemptTimeout`: `Timeout:Attempt` is below the dependency's real latency. Compare with its p99.
- `Standard-TotalRequestTimeout`: the whole request, retries and backoff included, exceeded `Timeout:Total`.

A `TaskCanceledException` instead means the caller cancelled or, with an inner `TimeoutException`,
`Timeout:Client` fired. Neither counts as a breaker failure.

## Latency pinned at `Timeout:Client`, everything else green

The dependency sends headers and then stalls the body. The pipeline already returned (a fast success), so
nothing retries or trips. Confirm: `TaskCanceledException` with inner `TimeoutException`, no
`TimeoutRejectedException`, low `http.client.request.duration`. Fix in the caller:
`HttpCompletionOption.ResponseHeadersRead` with your own read deadline. See
[OPERATIONS.md](OPERATIONS.md#stalled-response-bodies).

## A POST reached the origin more than once

Not retried or hedged by default. Check the startup log for **event 10** for that client: it names the methods
and the key that allowed it (`Retry:RetryableMethods`, `Retry:DisableForUnsafeHttpMethods: false`, or
`Hedging:DisableForUnsafeHttpMethods: false`).

No event 10? Look for **event 12**: an extra platform handler (`AddStandardResilienceHandler` retries every
method by default). Neither? The repeat came from application code above the client.

## Startup fails with a validation error

Working as designed. The message names the path, value, expectation and reason:

```text
HttpResilience:Clients:Orders -- Timeout:Attempt: value '00:00:10' is invalid. Expected strictly less than
Timeout:Total (00:00:10). Reason: the attempt timeout must leave room for at least one full attempt inside
the total budget.
```

Fix the configuration. See [TROUBLESHOOTING.md](TROUBLESHOOTING.md#startup-and-registration).

## Hedged client refuses a new host

`InvalidOperationException` naming `PipelineSelection:Authorities`. Add the authority to the client's list and
redeploy.

## Throughput capped below expectations

Check `Connection:MaxConnectionsPerServer` (only with `Connection:Enabled`): the pool queues below the
pipeline, visible only as latency. Then check limiter `queued_requests`.

## Event 12: extra resilience handler

1. Find the client's registration. An extra handler from `AddResilienceHandler` is expected.
2. From `AddStandardResilienceHandler()`: two pipelines are nested, (MaxRetries + 1)² origin calls per request:
   9 instead of 3 at the defaults, up to **121** at `Retry:MaxRetries: 10`. Fix it before anything else on this
   list during an incident: it multiplies load on a failing dependency. Remove it; use
   `AddResilienceHandler` for extra strategies. (With a configure, or `AddStandardHedgingHandler`, the client
   fails to start instead, naming the changed property.)
3. Not from `ConfigureHttpClientDefaults` (.NET Aspire), which is replaced (event 17), nor from the client's own
   registration before this package's, which fails client creation: only one added after it is reported here.
4. Confirm with the origin's request count; retry metrics cannot tell nesting from ordinary retries.

## Event 20: Polly v7 handler

A `Microsoft.Extensions.Http.Polly` handler (`AddPolicyHandler`, `AddTransientHttpErrorPolicy`) is on the client,
or on every client through `ConfigureHttpClientDefaults`. Its retry nests with this package's (12 origin calls
for a v7 retry of three) and retries POST. Remove it and map its values to the client's section
([RECIPES.md](RECIPES.md#migrating-from-microsoftextensionshttppolly-polly-v7)).

## Turning resilience off (kill switch)

**The kill switch is not instant.** Root `HttpResilience:Enabled: false` is read only at startup, so it takes
effect pod by pod as a redeploy rolls out; until then old pods keep their pipelines. Plan for your rollout time,
not seconds, and do not reach for it as the first lever in an incident.

1. Prefer the narrowest change that stops the harm: `Retry:Enabled: false` on one client stops amplification
   and keeps timeouts and the breaker; a client's own `Enabled: false` turns off only that client. Both need the
   same restart.
2. Know what the kill switch removes on every client: retries, timeouts, the breaker and both limiters,
   including the concurrency backstop, and `Timeout:Client` (`HttpClient` falls back to its 100 s default).
   `Connection` settings still apply. A resilience handler from `ConfigureHttpClientDefaults` (.NET Aspire
   ServiceDefaults) is removed too (event 19), because the platform's standard handler would retry POSTs; a
   handler registered on the client itself in code stays.
3. Confirm per pod: each client logs startup Warning event 6 naming `HttpResilience:Enabled`.
4. Remove it with another redeploy once the incident is over; it overrides every client section and code.

## Escalation

Include: client name, the configuration section it reads (event 15 shows its effective settings), the breaker
gauge, retry and breaker metrics for the window, and whether the dependency is known to be degraded.
