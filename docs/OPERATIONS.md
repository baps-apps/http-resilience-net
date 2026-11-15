# Operations

## Everything is per process

Nothing coordinates across replicas.

| Control | Scope | Fleet-wide effect |
| --- | --- | --- |
| Retry | per process | **outbound load multiplied in every replica independently** |
| Circuit breaker | per process, per pipeline | each replica must see `MinimumThroughput` **attempts** before it can open |
| Rate limiter | per process, per client | `replicas × clients × PermitLimit` per window. **Cannot enforce a cluster-wide quota** |
| Concurrency limiter | per process, per client | `replicas × clients × Limit` |
| Concurrency backstop | per pipeline (see below) | `replicas × pipelines × Backstop` |
| Hedging | per process | fan-out multiplied in every replica |

Backstop scope. Standard client: one per pipeline (one per client, or one per listed authority plus `shared`
under `ByAuthority`); one per client once a rate limiter is enabled; none when the rate limiter and
`ConcurrencyLimiter` are both enabled (`Limit` is the bound). Hedged client: one per listed authority.

Budgets belong to a **named client**, not a downstream: two clients calling one host have two budgets. Divide
real budgets by `replicas × clients`, or enforce them at a gateway.

Limiters sit outside retries, so one permit covers a request and all its attempts. A downstream counts
attempts: at `MaxRetries: 2` a failing dependency can see 3x the permitted rate.

## Retry amplification

Do this arithmetic before raising `Retry:MaxRetries`. Only GET, HEAD, OPTIONS and TRACE are repeated unless a
client opts in.

```text
20 pods × 100 RPS = 2,000 RPS; the dependency starts returning 100% 5xx.

Retrying client:  MaxRetries 1 → 4,000 RPS (2x)    MaxRetries 2 → 6,000 RPS (3x, default)
                  MaxRetries 3 → 8,000 RPS (4x)    MaxRetries 10 → 22,000 RPS (11x, the cap)

Hedged client:    MaxHedgedAttempts 1 → 2x (default) ... 10 → 11x (the cap).
                  A failed attempt starts the next at once, so a failing dependency sees the full multiplier.
```

A client retries **or** hedges, never both. Different clients calling the same dependency add up. Keep
`UseJitter` on so retries do not arrive as one wave.

**Nested pipelines square the multiplier.** A bare `AddStandardResilienceHandler()` added after this package's
handler shares the client's options, so each of its attempts runs this package's attempts: (MaxRetries + 1)²
origin calls per request. It is reported (event 12, Information), not prevented:

```text
Nested:           MaxRetries 2 → 9x (default)    MaxRetries 10 → 121x (the cap)
                  20 pods × 100 RPS at the cap: 242,000 RPS against a failing dependency.
```

Both figures are measured (`ConsumerBoundaryTests`) with failures and backoff that fit inside `Timeout:Total`; a
slower failure is cut off earlier by the two total timeouts. Treat every event 12 as a possible nesting until
the registration shows it came from `AddResilienceHandler`.

## The circuit breaker needs traffic

`MinimumThroughput` (20) attempts must occur **in one replica** within `SamplingDuration` (30 s) before
`FailureRatio` (0.5) is evaluated. Each retry is an attempt.

```text
per replica:   20 attempts / 30 s = 0.67 failing attempts/s ≈ 0.22 failing requests/s at MaxRetries 2
               (about 7 fully failing requests in 30 s). Below that, the breaker never opens.
20 replicas:   ≥ 400 failing attempts (~135 requests) fleet-wide before every breaker is open.
               Under ByAuthority and on hedged clients, per authority as well.
```

Size `MinimumThroughput` from **one replica's** attempt rate; startup event 11 states the rate each client
needs. New pods start with closed breakers, so a rollout during an incident sends another surge.

## Metrics

Wire the SDK as in [README.md](../README.md#telemetry). **A missing `AddMeter` fails silently**: run each query
below and see data before alerting on it. Dimensions are bounded; do not add any from URLs, tenants, users,
correlation IDs or exception messages.

| Metric | Type | Source | Useful dimensions |
| --- | --- | --- | --- |
| `resilience.polly.strategy.events` | counter | Polly | `pipeline.name`, `pipeline.instance`, `strategy.name`, `event.name`, `event.severity`, `exception.type`, `error.type` |
| `resilience.polly.strategy.attempt.duration` | histogram (ms) | Polly | the above, plus `attempt.number`, `attempt.handled` |
| `resilience.polly.pipeline.duration` | histogram (ms) | Polly | `pipeline.name`, `pipeline.instance`, `exception.type`, `error.type` |
| `http.client.request.duration` | histogram (s) | `System.Net.Http` | `server.address`, `server.port`, `http.request.method`, `http.response.status_code`, `error.type` |

- `event.name`: `OnRetry`, `OnHedging`, `OnTimeout`, `OnRateLimiterRejected`, `OnCircuitOpened`,
  `OnCircuitHalfOpened`, `OnCircuitClosed`. `strategy.name` separates `Standard-AttemptTimeout` from
  `Standard-TotalRequestTimeout`.
- `pipeline.instance` is empty for a single-pipeline client; under `ByAuthority` and on hedged clients it is
  the authority (`https://host[:port]`), or `shared` for unlisted hosts.
- `error.type` is set by the platform to the status code of **every** response, successes included (`"200"`):
  filter on the value. For exceptions, `AddHttpResilienceTelemetry()` adds the exception's full type name
  (`System.Net.Http.HttpRequestException`); without it, exceptions carry only `exception.type`.
- `http.client.request.duration` is per attempt and stops at response headers.

Prometheus names: dots become underscores, counters gain `_total`, `ms`/`s` become `_milliseconds`/`_seconds`
(`resilience_polly_strategy_events_total`, `http_resilience_circuit_breaker_state`).

### The package's gauges (meter `HttpResilience.NET`)

Observable gauges, read per collection, never on the request path. Published when the first handler chain is
built. The meter version is the package version.

| Instrument | Values | Dimensions |
| --- | --- | --- |
| `http_resilience.circuit_breaker.state` | 1 for the breaker's current state, 0 for the other two | `http_resilience.circuit_breaker.state` (`closed`, `open`, `half_open`), `http_resilience.client.name`, `http_resilience.authority`, plus `server.address` / `server.port` for a listed authority |
| `http_resilience.limiter.available_permits` | permits free now | `http_resilience.client.name`, `http_resilience.limiter.kind`, plus `http_resilience.authority` on a per-authority backstop, with `server.address` / `server.port` for a listed authority |
| `http_resilience.limiter.queued_requests` | requests waiting | same |

- **Every breaker of every enabled client is reported from the first collection**, as `closed`, so a missing
  series means a missing client, not a healthy one. Three series per breaker (the OpenTelemetry state-metric
  shape): select a state with the attribute, for example
  `http_resilience_circuit_breaker_state{http_resilience_circuit_breaker_state="open"} == 1`, and count breakers
  in a state with `sum`. `http_resilience.authority` is `shared` for a single-pipeline client and for a
  `ByAuthority` client's unlisted hosts; a hedged client has one breaker per listed authority.
- A limiter's series appears once the limiter exists (the limiter-slot backstop: on its first request). `kind`
  is `rate` (`RateLimiter:Enabled`), `concurrency` (`ConcurrencyLimiter:Enabled`) or `backstop` (scope above;
  per authority on hedged clients and on `ByAuthority` clients without a rate limiter). The backstop never
  queues.

## Alerts worth having

| Alert | Condition | Meaning |
| --- | --- | --- |
| Breaker open | `http_resilience.circuit_breaker.state{http_resilience.circuit_breaker.state="open"}` = 1 for 2 min | A dependency is failing fast. Tags name the client and host. |
| Breaker thrashing | > 3 `OnCircuitOpened` (or log event 2) in 10 min | Thresholds do not fit the traffic. |
| Retry rate | `OnRetry` events ÷ `resilience.polly.pipeline.duration` count, per `pipeline.name`, > 10% for 5 min | A dependency is degrading and retries add load. |
| Attempt timeouts rising | `OnTimeout` with `strategy.name = Standard-AttemptTimeout` (`StandardHedging-AttemptTimeout` when hedged) | `Timeout:Attempt` is tighter than real latency. |
| Limiter rejections | log events 7 (backstop), 8 (rate), 9 (concurrency); or `available_permits` at 0 | Budget exhausted. The log names the key to raise. |
| Limiter queueing | `queued_requests` > 0, sustained | Requests wait **outside** `Timeout:Total`. |
| Client timeouts | `TaskCanceledException` with inner `TimeoutException`, no `TimeoutRejectedException` | `Timeout:Client` fired: long queue wait or stalled body. |
| Unsafe methods repeatable | startup event 10 | A client can send a mutating request twice. Keep an inventory; alert on new entries. |
| Resilience disabled | startup event 6 | A client has no pipeline. The message names the key (check for a forgotten kill switch). |
| Extra resilience handler | event 12 (Information) | Fine from `AddResilienceHandler`; from `AddStandardResilienceHandler` retries multiply, (MaxRetries + 1)² up to 121x. Review. |
| Resilience handlers removed | event 16 (Warning) | The client runs without its pipeline. Usually `RemoveAllResilienceHandlers()` called after `AddHttpResilienceHandler`; remove the call. |

## Stalled response bodies

A dependency that sends headers quickly and then stalls the body pins callers at `Timeout:Client` while every
resilience signal stays green: the pipeline returned at the headers (a fast success), and `Timeout:Client`
cancels the body read outside every strategy (never retried, never a breaker failure).
`http.client.request.duration` and Polly's durations stop at headers too.

- **Watch** the calling service's own latency (for example `http.server.request.duration`).
- **Confirm**: `TaskCanceledException` with an inner `TimeoutException`, no `TimeoutRejectedException`.
- **Mitigate**: `HttpCompletionOption.ResponseHeadersRead` plus your own deadline on reading the stream.

## Logs

Source-generated, never request or response content. Categories: `HttpResilience.NET.Configuration` (6,
10–21), `HttpResilience.NET.Pipeline` (1–5), `HttpResilience.NET.Limiter` (7–9; 7 and 8 from the platform
handler's limiter slot are logged under `.Pipeline`).

| ID | Level | When |
| --- | --- | --- |
| 1 | Debug | retry attempt: delay, status, exception type (Polly already logs each retry at Warning) |
| 2 | Warning | circuit opened: client, authority, break duration |
| 3 / 4 | Information | circuit half-open / closed |
| 5 | Debug | hedged attempt started |
| 6 | Warning | startup: client disabled, naming the key (its own `Enabled` or the root kill switch) |
| 7 | Warning | concurrency backstop rejected a request; names the value and key |
| 8 | Warning | rate limiter rejected a request; names the permit key |
| 9 | Warning | concurrency limiter rejected a request; names limit, queue and key |
| 10 | Warning | startup: client may repeat unsafe methods; names the methods and the key |
| 11 | Information | startup: failing-attempt and request rate the breaker needs, per replica |
| 12 | Information | first chain build: more resilience handlers than the package added |
| 13 | Warning | first chain build: redirect bound not applied (primary handler has no `AllowAutoRedirect`) |
| 14 | Warning | startup: worst-case retry schedule exceeds `Timeout:Total` |
| 15 | Information | startup: one-line summary of each enabled client's effective settings |
| 16 | Warning | first chain build: a later registration removed resilience handlers this package added |
| 17 | Information | first chain build: resilience handlers registered before this package's (such as .NET Aspire's) were replaced |
| 18 | Warning | startup, once: `HttpResilience:SchemaVersion` names a newer minor version, and the file holds keys this version ignores (listed) |
| 19 | Information | first chain build: a disabled client's resilience handlers from `ConfigureHttpClientDefaults` (such as .NET Aspire's) were removed |
| 20 | Warning | first chain build: the client also has a Polly v7 handler (`Microsoft.Extensions.Http.Polly`), whose retry nests with this package's |
| 21 | Warning | startup: a hedged client's `Hedging:Delay` is below 10 ms, so nearly every request is sent `MaxHedgedAttempts` + 1 times; names the key |

- Startup notices are logged once, from final options, after validation and before any hosted service starts.
  A bare `ServiceProvider` without a host logs none.
- Events 7–9 are throttled to one per limiter per 10 s, with `SuppressedCount`. Count rejections from
  `resilience.polly.strategy.events` with `event.name = OnRateLimiterRejected`.
- Debug events are off by default: `"Logging": { "LogLevel": { "HttpResilience.NET.Pipeline": "Debug" } }`.

## Health checks

**Never gate a liveness or readiness probe on circuit breaker state.** An open circuit means a dependency is
unhealthy, not this process; restarting pods cuts capacity mid-outage and resets every breaker. Alert on the
gauge instead. The package registers no health check.

## Kubernetes

- **DNS.** With `Connection:Enabled`, `PooledConnectionLifetime` (2 min) bounds connection age. Without it,
  the factory's 2-minute handler rotation does.
- **Autoscaling** multiplies every per-process budget.
- **Hedged clients'** authorities are fixed at deploy time; adding one needs a config change and restart.
- **Shutdown.** Pass a token cancelled at shutdown (the request's, or the host's stopping token): cancellation
  stops retries immediately, so requests do not spend the grace period in backoff.
- **Configuration changes need a restart.**
