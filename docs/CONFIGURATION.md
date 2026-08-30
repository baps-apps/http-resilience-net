# Configuration reference

Every key the schema reads, its default, and why the default is what it is.

The root section is `HttpResilience`. Per-client overrides live at `HttpResilience:Clients:{name}` and are
bound on top of the root, so a client states only what it changes.

Two keys are **root only** and are read from the raw section rather than bound, because they are statements
about the process rather than about a client: `AllowUnusedClientSections` and `ValidateClientsOnStart`.
Binding them would make them look inheritable per client, which they are not.

Three keys run the other way — they are ordinary per-client options that the **root** refuses: both
`DisableForUnsafeHttpMethods` flags may not be `false` there, and root `Retry:RetryableMethods` may name only
safe methods. Each is checked against the raw root section at registration as well as against the bound
options, so no hosting model can skip it.

All durations are `TimeSpan` strings (`"00:00:20"`). `Enabled` is opt-in and defaults to `false`.

See [README.md](../README.md) for the concepts behind these keys and
[ARCHITECTURE.md](ARCHITECTURE.md#validation) for how and when each rule runs.

## Keys

| Section | Key | Default | Notes |
| --- | --- | --- | --- |
| | `Enabled` | `false` | Opt-in. Governs the resilience pipeline only; `Connection` applies independently. A client with it `false` logs one **Warning** at startup naming the key. |
| | `AllowUnusedClientSections` | `false` | Root only. When `false`, a section under `Clients` that no registered client reads fails startup. Set it to `true` only when one configuration file is deliberately shared by services registering different clients. |
| | `ValidateClientsOnStart` | `true` | Root only. Creates every client this package configured, once, at host start, so a client whose handler chain cannot be built fails the deployment rather than the first request that reaches it. Set it to `false` to defer that to first use. Setting it to `false` while also calling `ValidateHttpResilienceClientsOnStart()` fails startup, naming both. |
| `Timeout` | `Total` | `00:00:20` | Bound for the attempts. Stops applying at response headers. |
| | `Attempt` | `00:00:05` | One attempt, up to response headers. Must be strictly less than `Total`. |
| | `Client` | `Total` + 30 s | `HttpClient.Timeout`. The outer backstop: queue wait plus attempts plus response-body transfer. Must be strictly greater than `Total`. The only place the schema can validate this bound, so setting `HttpClient.Timeout` through `ConfigureHttpClient` fails at client creation — including a deliberate 100 seconds. One shape escapes that guard: a typed client's constructor — see [README.md](../README.md#timeouts). |
| `Retry` | `Enabled` | `true` | The supported way to switch retries off. |
| | `MaxRetries` | `2` | Retries **after** the first attempt, so the default is three requests. 1–10. Not range-checked while `Enabled` is false. `Retry:MaxAttempts` is refused at startup rather than aliased to this key: it counts retries, not attempts, so a file using that name has arithmetic that is off by one. |
| | `BaseDelay` | `00:00:00.500` | Scaled by `BackoffType`. **Tuned for same-cluster calls, and lower than the platform's 2 s.** With the default `Exponential` and `MaxRetries: 2` all three attempts land inside about 1.5 s before jitter, which is fine against a healthy in-cluster dependency and aggressive against one in a cold start, a GC pause or a rolling restart — in every replica at once. Raise it to `00:00:01` or more for anything crossing a region or a cold-start boundary, and check the retry-budget rule still passes. |
| | `BackoffType` | `Exponential` | `Constant`, `Linear` or `Exponential`. |
| | `UseJitter` | `true` | Keep on: without it every replica retries on the same schedule. |
| | `UseRetryAfterHeader` | `true` | Parsing delegated to the platform. The header **replaces** the computed delay, so the retry-budget validation below covers the configured schedule only: an origin naming a large `Retry-After` can spend the whole of `Timeout:Total` on one wait, holding its concurrency slot or permit meanwhile. Bounded by `Timeout:Total`, never unbounded — see [TROUBLESHOOTING.md](TROUBLESHOOTING.md). |
| | `DisableForUnsafeHttpMethods` | `true` | Retries only GET, HEAD, OPTIONS, TRACE. Anything else, standard or not, is left alone. **Cannot be set to `false` at the root** — per client only — and cannot be *stated at all* beside a `RetryableMethods` list in force, because the list replaces it. A client with it `false` logs one **Warning** at startup naming the key. |
| | `RetryableMethods` | `null` | Explicit allow-list; replaces the flag above, and the only way to retry a non-standard method. Inheritable from the root, unlike the flag — but **the root may name only safe methods**; an unsafe entry there is refused and belongs per client. A client section **replaces** this list rather than adding to the root's, and an **empty list** returns that client to the default safe-method guard. Cannot be combined with a stated `DisableForUnsafeHttpMethods`, in either direction. Content must be re-playable. Naming any method other than GET/HEAD/OPTIONS/TRACE logs one **Warning** at startup. |
| `CircuitBreaker` | `FailureRatio` | `0.1` | Proportion of failures in the sampling window. |
| | `MinimumThroughput` | `100` | **Attempts**, not caller requests — the breaker is inside the retry loop, so one failing request contributes `1 + MaxRetries`. Per replica. Below this rate there is effectively no breaker. |
| | `SamplingDuration` | `00:00:30` | Must be at least double `Timeout:Attempt`. |
| | `BreakDuration` | `00:00:05` | |
| `Connection` | `Enabled` | `false` | Applies even when the pipeline is disabled. |
| | `MaxConnectionsPerServer` | unset | Unset means the runtime default (unlimited). |
| | `PooledConnectionLifetime` | `00:02:00` | Bounds DNS staleness. Factory rotation is disabled when `Enabled`. |
| | `PooledConnectionIdleTimeout` | `00:01:00` | Must be **strictly less than** `PooledConnectionLifetime`, or it can never fire. |
| | `ConnectTimeout` | `00:00:03` | Covers TCP **and** the TLS handshake. Must be **strictly less than** `Timeout:Attempt`, or a slow connect can only ever be reported as an attempt timeout. Raise it with `Timeout:Attempt` for a distant dependency. |
| | `EnableMultipleHttp2Connections` | `true` | **The runtime default is `false`.** This is the one place the schema changes a platform default rather than surfacing one. A single HTTP/2 connection caps the client at the server's `MAX_CONCURRENT_STREAMS`, which behind a load balancer is often 100 — a throughput cliff with no configuration key attached to it. Set it back to `false` if a downstream counts connections rather than streams. |
| | `AllowAutoRedirect` | unset | `true` (the runtime default) for a standard client, `false` for a hedged one. The only setting that bounds where requests actually go — see [README.md](../README.md#a-hedged-client-does-not-follow-redirects). Applied even with `Connection:Enabled` false when it resolves to `false`. **Written onto a primary handler you supplied only when you state it here, or when it resolves to `false`.** Unstated-and-`true` is the pipeline kind talking, not a person, so it does not overwrite a handler hardened with `AllowAutoRedirect = false`. If the primary handler is neither a `SocketsHttpHandler` nor an `HttpClientHandler` there is nowhere to write it: that logs **Warning event 13** naming the client rather than failing, because the handler that reaches that branch in practice is a test stub, which follows no redirects. |
| `RateLimiter` | `Enabled` | `false` | Process-local, and per client. Fleet rate is `replicas × clients × PermitLimit` — see [README.md](../README.md#rate-limiting-is-process-local). |
| | `Algorithm` | `FixedWindow` | `FixedWindow`, `SlidingWindow`, `TokenBucket`. |
| | `PermitLimit` | required | Required by `FixedWindow` and `SlidingWindow`. No default: it is a contract with a specific downstream. |
| | `Window` | `00:00:01` | `FixedWindow` and `SlidingWindow`. |
| | `SegmentsPerWindow` | `8` | `SlidingWindow` only. Higher is smoother and costs a little more memory. |
| | `TokenLimit` | required | `TokenBucket` only — bucket capacity, so the largest burst an idle client may make. |
| | `TokensPerPeriod` | required | `TokenBucket` only — tokens added each `ReplenishmentPeriod`, so the sustained rate as opposed to the burst. |
| | `ReplenishmentPeriod` | `00:00:01` | `TokenBucket` only. |
| | `QueueLimit` | `0` | Fails fast. Queued requests hold their content buffers in memory. Capped at 1,000. |
| `ConcurrencyLimiter` | `Enabled` | `false` | The client's own cap, applied outside the handler. |
| | `Limit` | required | How much of *your* capacity may wait on one dependency. Must be at most `Backstop`. |
| | `QueueLimit` | `0` | Capped at 1,000. |
| | `Backstop` | `1000` | The platform's own limiter slot, surfaced. No queue: above it, requests are rejected. With no rate limiter it sits *in* that slot, which is one limiter **per pipeline** — so a hedged client or one using `PipelineSelection:Mode = ByAuthority` gets this cap per authority, not per client. A rate limiter takes the slot, and the backstop is then re-added as its own handler outside it, which is **per client** even under `ByAuthority`. That extra handler is skipped in the one case where it would be redundant — the client also sets `ConcurrencyLimiter:Limit`, which validation holds at or below the backstop and which therefore bounds concurrency more tightly. **A concurrency bound is never absent**; in that one case it is the client's number rather than this one. |
| `Hedging` | `Delay` | `00:00:02` | Only used by `AddHedgedHttpResilience`, which rejects `Retry:*` keys on the same client. |
| | `MaxHedgedAttempts` | `1` | 1–10. Directly multiplies outbound load. |
| | `DisableForUnsafeHttpMethods` | `true` | Covers the timer path as well as the outcome path. **Cannot be set to `false` at the root** — per client only. A hedged client with it `false` logs one **Warning** at startup. |
| `PipelineSelection` | `Mode` | `None` | `None` or `ByAuthority`. Isolates circuit breakers, not limiters. |
| | `Authorities` | `null` | Required for `ByAuthority`, and for every client registered with `AddHedgedHttpResilience`. A client section **replaces** this list rather than adding to the root's. A **root** list is inherited by every client and is inert — not an error — on a standard client under `Mode: None`; a client that states one itself under `Mode: None` fails at registration. |


## Cross-property rules

The validator checks relationships, not just ranges. Each message names the path, the value, the expectation
and the reason.

| Rule | Why |
| --- | --- |
| `Timeout:Attempt` < `Timeout:Total` | An attempt that may outlive the whole budget cannot be bounded by it. |
| `Timeout:Client` > `Timeout:Total` | `Timeout:Client` is the outer backstop; equal or lower truncates the pipeline. |
| `Connection:ConnectTimeout` < `Timeout:Attempt` | Otherwise a slow connect can only ever be reported as an attempt timeout. |
| `Connection:PooledConnectionIdleTimeout` < `PooledConnectionLifetime` | Otherwise the idle timeout can never fire. |
| `CircuitBreaker:SamplingDuration` >= 2 x `Timeout:Attempt` | A window shorter than two attempts cannot observe a failure rate. |
| The retry schedule fits `Timeout:Total`, with 1.5x headroom when `UseJitter` is on | A schedule whose last retry is cut off is a schedule that does not do what it says. Headroom is a factor, not a bound: `Retry-After` replaces the computed delay entirely and can spend the whole budget on one wait. |
| The hedging schedule fits `Timeout:Total` | `Hedging:Delay` x `MaxHedgedAttempts` has to leave room for an attempt, or the last hedged attempt can never start. |
| `ConcurrencyLimiter:Limit` <= `ConcurrencyLimiter:Backstop` | The backstop is the outer bound; a limit above it is unreachable. |
| `RateLimiter` required keys, per `Algorithm` | `FixedWindow` and `SlidingWindow` need `PermitLimit`; `TokenBucket` needs `TokenLimit` and `TokensPerPeriod`. There is no default for any of them, because each is a contract with a specific downstream. |
| `PipelineSelection:Authorities` required for `Mode: ByAuthority` and for every hedged client | Both mint a pipeline per authority, so the set has to be fixed at deploy time. |
| A hedged client states no `Retry:*` keys | The hedging pipeline has no retry strategy, so those keys would bind to nothing. |

Two rules judge the configuration *file* rather than a value and run once at startup against the root:

- **An unused `Clients:{name}` section fails startup.** Inert configuration reads exactly like configuration
  in force. Escape hatch: `AllowUnusedClientSections`, root only, defaults to failing.
- **A renamed key fails startup rather than binding to nothing.** `Retry:MaxAttempts` still binds, to a
  tombstone, so a stale file is refused instead of silently ignored.

The binder ignores keys it does not recognize, so a misspelled *property* inside a section that is read is
still silent: `Clients:Orders:Timeout:Totl` binds to nothing and says nothing. That gap is not closed. What
is closed is the case where an entire client's configuration is inert.

## Configuration reload

Resilience configuration is read once at startup. Changing it requires a restart.

This is deliberate. Rebuilding handler pipelines under load would mean disposing live strategies while
requests are in flight. The options are registered with `Configure` rather than `Bind`, so no reload token
exists and `IOptionsMonitor` can never report a value that arrived from a configuration reload and is not in
effect.

Within a process, `IOptionsMonitor<HttpResilienceOptions>.Get(name)` reports what the pipeline runs, because
the pipeline is built from that same options instance rather than from a copy of it — the rate limiter
included, which is created from live options inside its keyed factory. So a consumer's `Configure` or
`PostConfigure` reaches the pipeline exactly as it does for the platform's own options.

Two limits on that. A consumer who configures `HttpStandardResilienceOptions` directly is reaching past this
schema to the platform's own extension point; it works, and keeping it consistent with this schema is the
consumer's business. And a few options decide *which handlers exist*, which `IHttpClientFactory` fixes at
registration — `Enabled`, `RateLimiter:Enabled`, `ConcurrencyLimiter:Enabled`, `PipelineSelection:Mode`,
`Connection:Enabled` and the resolved `Connection:AllowAutoRedirect`. Changing one of those after
registration fails startup rather than appearing to work.
