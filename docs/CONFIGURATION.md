# Configuration reference

The root section is `HttpResilience` (or the section passed to `AddHttpResilience(section)`; paths below are
relative to it). A client reads `HttpResilience:Clients:{name}` bound on top of the root, so it states only
what it changes. Lists replace the root's list; they are not merged.

- Durations are `TimeSpan` strings (`"00:00:20"`).
- Configuration is read once; changes need a restart.
- Order of application: root section → root delegates (`AddHttpResilience(options => ...)`) → client section →
  the client's own `configure` delegate → root kill switch.

Three checks fail startup by default and have root-only opt-outs: an unknown key (`AllowUnknownKeys`, or a newer
`SchemaVersion`, which makes it a warning), an unused client section (`AllowUnusedClientSections`) and a client
whose handler chain cannot be built (`ValidateClientsOnStart`). A file shared by many services usually needs the
first two: [RECIPES.md](RECIPES.md#one-configuration-file-shared-by-many-services).

## Keys

| Section | Key | Default | Notes |
| --- | --- | --- | --- |
| | `Enabled` | `true` | `false` in a client section turns that client off. `false` at the root is a kill switch: every client is off, whatever its section or code says. Turns off the pipeline and `Timeout:Client` (`HttpClient` keeps its 100 s default); `Connection` still applies. A disabled client logs startup **Warning event 6** naming the key, and its other values are not range-checked. |
| | `AllowUnusedClientSections` | `false` | Root only; also settable in code (`services.Configure<HttpResilienceStartupOptions>(...)`, which wins over configuration). `false`: a `Clients` section no registered client reads fails startup. Set `true` when one file serves services that register different clients. Must be `true` or `false`. |
| | `AllowUnknownKeys` | `false` | Root only; also settable in code. `false`: a key the schema does not have, at the root or in a client section, fails startup naming the key and the nearest real one (`Retry:Enable` → `Enabled`), unless `SchemaVersion` names a newer minor. `true` turns the check off entirely, typos included. Must be `true` or `false`. |
| | `SchemaVersion` | (none) | Root only; configuration only. The package version the file was written for, as `major.minor` (`3`, `3.1`, `3.1.0` accepted). **Newer minor of this major**: unknown keys are a startup **Warning event 18** listing them, not a failure (a minor only adds optional keys), so a typo in that file is not caught either. **Same, older or absent**: unknown keys fail startup. **Newer major**: fails startup (a major may rename keys). Not a version: fails startup. |
| | `ValidateClientsOnStart` | `true` | Root only; also settable in code. Creates every configured client at host start, so a broken handler chain fails the deployment, after a self-check that the resolved `Microsoft.Extensions.Http` still runs handler actions in the order this package relies on. `false` skips both and defers creation to first use (options are still validated at start). Must be `true` or `false`. |
| `Timeout` | `Total` | `00:00:20` | All attempts and backoff, up to response headers. 10 ms–1 day. Limiter queue wait is **not** included. |
| | `Attempt` | `00:00:05` | One attempt, up to response headers. 10 ms–1 day, less than `Total`. |
| | `Client` | `Total` + 30 s | `HttpClient.Timeout`: queue wait, all attempts and the response body. Greater than `Total`, at most `int.MaxValue` ms. A different value assigned through `ConfigureHttpClient` fails client creation; one assigned in a typed client's constructor cannot be detected. |
| `Retry` | `Enabled` | `true` | The switch to turn retries off (`MaxRetries` cannot be 0). While off, `MaxRetries`, `BaseDelay`, `MaxDelay` and `BackoffType` are neither checked nor applied; `RetryableMethods` and its rules still are. |
| | `MaxRetries` | `2` | Retries **after** the first attempt: 2 means 3 requests. 1–10. `Retry:MaxAttempts` is not a key: it fails startup naming this one. |
| | `BaseDelay` | `00:00:00.500` | Scaled by `BackoffType`. 0–1 minute. Suits same-cluster calls; use `00:00:01`+ across regions. |
| | `BackoffType` | `Exponential` | `Constant`, `Linear` or `Exponential`. |
| | `UseJitter` | `true` | Keep on, so replicas do not retry in lockstep. |
| | `MaxDelay` | `00:00:05` | The longest wait before a retry; a longer computed backoff is cut to it. At least `BaseDelay`, greater than 0, at most 1 day. |
| | `UseRetryAfterHeader` | `true` | A `Retry-After` header **replaces** the computed delay when it is at most `MaxDelay`. A longer one **ends the retries**: the origin's 429/503 goes straight back to the caller instead of holding it (and its limiter slot). Raise `MaxDelay` (and `Timeout:Total`) for a dependency that asks for longer waits. |
| | `DisableForUnsafeHttpMethods` | `true` | Retry only GET, HEAD, OPTIONS, TRACE (an allow-list). `false` retries every method. **Not allowed `false` at the root.** A client may not state it while a `RetryableMethods` list is in force (own or inherited). `false` logs startup **Warning event 10**. |
| | `RetryableMethods` | empty | When non-empty, only these methods are retried (list `GET` too if you want it) and the flag above is ignored. Valid method tokens, upper-cased (`post` = `POST`); request methods must match exactly. **The root may list only safe methods.** An empty client list restores the safe-method guard. An unsafe entry logs startup **Warning event 10**. |
| `CircuitBreaker` | `FailureRatio` | `0.5` | Failure share in the window that opens the circuit. Above 0, at most 1. High enough that a steady low error rate, which retries absorb, does not open it. |
| | `MinimumThroughput` | `20` | **Attempts**, not requests, per replica, within `SamplingDuration`, before the ratio is evaluated. At least 2. With fewer attempts the breaker never opens; startup event 11 states the rate each client needs. |
| | `SamplingDuration` | `00:00:30` | 500 ms–1 day, and at least 2 × `Timeout:Attempt`. |
| | `BreakDuration` | `00:00:05` | How long the circuit stays open. 500 ms–1 day. |
| `Connection` | `Enabled` | `false` | Configures the primary `SocketsHttpHandler` and turns off the factory's handler rotation. Applies even to a disabled client. A supplied `SocketsHttpHandler` is kept, and fails client creation if it sets one of the keys below to a value other than the resolved one. An `HttpClientHandler` with nothing set is replaced; one carrying any setting fails client creation. Any other type fails client creation. The keys below are validated only when this is `true`. |
| | `MaxConnectionsPerServer` | unset | Unset keeps the runtime default (unlimited). At least 1. Requests over it queue in the pool, invisible to the pipeline. |
| | `PooledConnectionLifetime` | `00:02:00` | Max connection age; bounds DNS staleness. Greater than 0. Always written. |
| | `PooledConnectionIdleTimeout` | `00:01:00` | Greater than 0, less than `PooledConnectionLifetime`. Always written. |
| | `ConnectTimeout` | `00:00:03` | TCP + TLS. Less than `Timeout:Attempt`, at most `int.MaxValue` ms. Always written. |
| | `EnableMultipleHttp2Connections` | `true` | The runtime default is `false`: one HTTP/2 connection is capped by the server's stream limit (often 100). Always written. |
| | `AllowAutoRedirect` | unset | Unset: standard clients follow redirects (runtime default), hedged clients do not. `false` (stated or resolved) is applied even with `Connection:Enabled` off; if the primary handler has no such setting, **Warning event 13**. Written onto your own handler only when stated or `false`. Recommended `false` at the root of shared configuration: a followed redirect re-sends custom headers (API keys) to the new host. A client that needs redirects states `true`. |
| `RateLimiter` | `Enabled` | `false` | **Per process, per client: cannot enforce a cluster-wide quota.** Fleet rate is `replicas × clients × PermitLimit`. One permit covers a request and all its retries or hedges. |
| | `Algorithm` | `FixedWindow` | `FixedWindow`, `SlidingWindow` or `TokenBucket`. |
| | `PermitLimit` | required | `FixedWindow` / `SlidingWindow`: permits per `Window`. |
| | `Window` | `00:00:01` | `FixedWindow` / `SlidingWindow`. Greater than zero. |
| | `SegmentsPerWindow` | `8` | `SlidingWindow` only. At least 1. |
| | `TokenLimit` | required | `TokenBucket` only: bucket size (largest burst). |
| | `TokensPerPeriod` | required | `TokenBucket` only: tokens added per `ReplenishmentPeriod`. |
| | `ReplenishmentPeriod` | `00:00:01` | `TokenBucket` only. Greater than zero. |
| | `QueueLimit` | `0` | 0 fails fast. Max 1,000. Queued requests hold their content in memory and wait outside `Timeout:Total`. |
| `ConcurrencyLimiter` | `Enabled` | `false` | The client's own in-flight cap, outside the platform handler: one slot per request including retries. Per client. |
| | `Limit` | required | Max requests in flight. At most `Backstop`. |
| | `QueueLimit` | `0` | 0 fails fast. Max 1,000. The wait is outside `Timeout:Total`. |
| | `Backstop` | `1000` | Always-on concurrency limit, no queue, 1–10,000. Standard client: per pipeline (per authority under `ByAuthority`); with a rate limiter, one per client; omitted when the rate limiter **and** `ConcurrencyLimiter` are both enabled (`Limit` is the bound). Hedged client: per authority. Rejections log **Warning event 7**. |
| `Hedging` | `Delay` | `00:00:02` | Hedged clients only. Wait before the next attempt. Zero or more; zero sends all attempts at once. Below 10 ms logs startup **Warning event 21**. |
| | `MaxHedgedAttempts` | `1` | Extra attempts: 1 means up to 2 requests. 1–10. |
| | `DisableForUnsafeHttpMethods` | `true` | Hedge only GET, HEAD, OPTIONS, TRACE, including hedges started by the delay timer. **Not allowed `false` at the root.** `false` logs startup **Warning event 10**. |
| `PipelineSelection` | `Mode` | `None` | Standard clients: `None` (one pipeline) or `ByAuthority` (one per listed authority, so circuit breakers, and without a rate limiter the backstop, are isolated per host; rate and concurrency limiters stay per client). Hedged clients are always per authority. |
| | `Authorities` | empty | `scheme://host[:port]`. Required for `ByAuthority` and for every hedged client. Unlisted hosts share one pipeline (standard) or are refused with `InvalidOperationException` (hedged; not `HttpRequestException`, so no retry policy repeats it). Hosts match on `Uri.IdnHost` ignoring a trailing dot; scheme and port must match exactly. A root list is ignored by standard clients in `Mode: None`. |

## Cross-key rules

Checked at startup; a violation fails with the key, value, expectation and reason.

| Rule | Why |
| --- | --- |
| `Timeout:Attempt` < `Timeout:Total` < `Timeout:Client` | Each must fit inside the next. |
| `CircuitBreaker:SamplingDuration` ≥ 2 × `Timeout:Attempt` | A shorter window cannot observe a failure rate. |
| Hedged: `Hedging:Delay` × `MaxHedgedAttempts` + `Timeout:Attempt` ≤ `Timeout:Total` | The last hedge must be able to finish. |
| `ConcurrencyLimiter:Limit` ≤ `ConcurrencyLimiter:Backstop` | A higher limit is unreachable. |
| With `Connection:Enabled`: `ConnectTimeout` < `Timeout:Attempt`, `PooledConnectionIdleTimeout` < `PooledConnectionLifetime` | Otherwise the smaller setting never takes effect. |
| `Retry:DisableForUnsafeHttpMethods: false` never beside a non-empty `Retry:RetryableMethods` | The list replaces the flag. |

Judged against the **client's own section** (root values are shared by both kinds of client):

| Rule | Why |
| --- | --- |
| No `Retry` keys on a hedged client; no `Hedging` keys on a standard client | That pipeline never reads them. |
| No `PipelineSelection:Authorities` on a standard client in `Mode: None` | Per-host isolation would silently not happen. |
| No `Retry:DisableForUnsafeHttpMethods` while a `RetryableMethods` list is in force | The list replaces the flag. |

Root-only rules, applied to the root section **and** to `AddHttpResilience(options => ...)`:

- Neither `DisableForUnsafeHttpMethods` may be `false`.
- `Retry:RetryableMethods` may name only GET, HEAD, OPTIONS and TRACE.

Root `Retry` values are judged by each standard client, root `Hedging` values by each hedged client. A bad value
fails every client that reads it, named under that client; with no client of that kind it is inert.

Not a failure: a retry schedule that can outlast `Timeout:Total` logs startup Warning event 14.

Not detected: a misspelled key inside a section that is read (`Clients:Orders:Timeout:Totl`) is ignored by the
binder.

## Differences from `AddStandardResilienceHandler`

This package configures the platform's own handlers with different defaults. A client moved from
`AddStandardResilienceHandler()` or `AddStandardHedgingHandler()` with no `HttpResilience` section behaves as the
right-hand column. `PlatformDefaultsComparisonTests` pins both columns, and the weekly platform-drift run executes
it against the newest platform release.

**Standard client** (`AddHttpResilienceHandler` vs `AddStandardResilienceHandler`):

| What | Platform default | This package | Key |
| --- | --- | --- | --- |
| Total timeout | 30 s | **20 s** | `Timeout:Total` |
| Attempt timeout | 10 s | **5 s** | `Timeout:Attempt` |
| Retries after the first attempt | 3 (4 requests) | **2** (3 requests) | `Retry:MaxRetries` |
| Base retry delay | 2 s | **500 ms** | `Retry:BaseDelay` |
| Longest wait before a retry | no cap | **5 s** | `Retry:MaxDelay` |
| `Retry-After` longer than that cap | waited for, whatever its length, until the total timeout ends the request | **ends the retries**: the 429 or 503 is returned at once | `Retry:MaxDelay`, `Retry:UseRetryAfterHeader` |
| Methods retried | **every method**, POST included | GET, HEAD, OPTIONS, TRACE | `Retry:DisableForUnsafeHttpMethods`, `Retry:RetryableMethods` (per client only) |
| `HttpClient.Timeout` | infinite | **`Timeout:Total` + 30 s** (50 s) | `Timeout:Client` |
| Circuit breaker opens at | 10% failures of at least **100** attempts in 30 s (3.3 attempts/s per replica) | **50%** of at least **20** (0.67 attempts/s) | `CircuitBreaker:FailureRatio`, `CircuitBreaker:MinimumThroughput` |

**Hedged client** (`AddHttpResilienceHedgingHandler` vs `AddStandardHedgingHandler`):

| What | Platform default | This package | Key |
| --- | --- | --- | --- |
| Total timeout | 30 s | **20 s** | `Timeout:Total` |
| Attempt timeout (per endpoint) | 10 s | **5 s** | `Timeout:Attempt` |
| Methods hedged | **every method** | GET, HEAD, OPTIONS, TRACE, including hedges started by the delay timer | `Hedging:DisableForUnsafeHttpMethods` (per client only) |
| Authorities | any; one pipeline per authority, never evicted | **listed only**; others refused with `InvalidOperationException` | `PipelineSelection:Authorities` (required) |
| Redirects | followed (runtime default) | **not followed** | `Connection:AllowAutoRedirect` |
| Circuit breaker (per endpoint) opens at | 10% failures of at least **100** attempts in 30 s | **50%** of at least **20** | `CircuitBreaker:FailureRatio`, `CircuitBreaker:MinimumThroughput` |

The same on both: exponential backoff with jitter, `Retry-After` honoured within the cap, the breaker's 30 s
window and 5 s break, a concurrency limit of 1,000 with no queue in the limiter slot
(`ConcurrencyLimiter:Backstop`), hedging after 2 s with 1 extra attempt, and the factory's connection handling
unless `Connection:Enabled`.

Why the shorter budget: every retry the defaults allow fits inside `Timeout:Total` (3 × 5 s + 0.5 s + 1 s =
16.5 s ≤ 20 s). The platform's schedule (4 × 10 s + 2 s + 4 s + 8 s = 54 s) cannot complete in its 30 s, so its
later retries are cut off. Why the different breaker: the platform's needs 3.3 attempts per second in one
replica before it can open, which most internal clients never reach; 20 attempts at 50% opens on a dependency
that is down (about seven fully failing requests in 30 s) and not on a steady low error rate that retries absorb.
The unsafe-method guards and the authority list are safety rules, not tuning.

To keep the platform's timing, set the root values below. The safe-method guards cannot be switched off at the
root; a client that relies on retrying a POST states that in its own section
([Retrying a POST safely](RECIPES.md#retrying-a-post-safely)).

```json
{
  "HttpResilience": {
    "Timeout": { "Total": "00:00:30", "Attempt": "00:00:10" },
    "Retry": { "MaxRetries": 3, "BaseDelay": "00:00:02", "MaxDelay": "00:00:30" },
    "CircuitBreaker": { "FailureRatio": 0.1, "MinimumThroughput": 100 }
  }
}
```

- `MaxDelay` equal to `Total`: no wait can outlast `Total` anyway. One difference remains: a `Retry-After`
  longer than 30 s returns the 429 or 503 at once, where the platform waits and then throws
  `TimeoutRejectedException`.
- Startup logs **Warning event 14** for these values, because the schedule is longer than `Total`. That reports
  the platform's own behavior; it is not a fault.
- `HttpClient.Timeout` stays finite (`Timeout:Client`, here 60 s). It cannot be infinite.

## Reading and overriding values in code

`IOptionsMonitor<HttpResilienceOptions>.Get("Orders")` returns exactly what the pipeline runs.
`services.Configure<HttpResilienceOptions>("Orders", ...)` and `PostConfigure` reach the pipeline too,
including `Enabled` and the limiter switches. A `Configure` registered **before** `AddHttpResilienceHandler`
runs before binding, so configuration wins over it; `PostConfigure` always runs last. The clearest option is
the registration's own parameter: `.AddHttpResilienceHandler(options => ...)`.

Root values for every client go in `AddHttpResilience(options => ...)`, alone or after a section:
`AddHttpResilience(configuration, options => ...)` for the default `HttpResilience` section, or
`AddHttpResilience(configuration.GetSection("Platform:Http"), options => ...)` for another one.

The platform options behind a client (`HttpStandardResilienceOptions` named `{client}-standard`, or
`HttpStandardHedgingResilienceOptions` named `{client}` for a hedged client) hold its validated pipeline and are
shared by every registration on the client. Code that changes them after this package writes them (`Configure`,
`PostConfigure` or `ConfigureAll` of those types, `AddStandardResilienceHandler(configure)` on the client, or a
second `AddStandardHedgingHandler`) fails client creation, and so host start, naming each changed property: it
would replace validated values and could make a POST repeatable. Use `AddResilienceHandler` for anything the
schema does not cover.
