# Changelog

All notable changes to **HttpResilience.NET** are documented in this file.

This project follows [Semantic Versioning](https://semver.org/). See [docs/VERSIONING.md](docs/VERSIONING.md)
for what counts as MAJOR, MINOR and PATCH here.

## [2.0.0] - 2026-08-29

A rewrite. The package now **configures** `Microsoft.Extensions.Http.Resilience` and owns no strategy of its
own. Every public API and every configuration key changed; nothing from 1.0.0 binds or compiles unchanged.

Services still on 1.0.0: [docs/V1.md](docs/V1.md) documents that version on its own terms.

### Removed

- **Fallback**, in every form: `IHttpFallbackHandler`, `HttpFallbackContext`, `FallbackOptions` and the
  synthetic-response keys. A fallback turns a failure into a success from the caller's point of view, which is
  an application decision rather than a transport one.
- **`PipelineOrder`.** Pipeline ordering is now fixed and defined by the platform's own standard handlers.
  Anything outside that shape belongs in a separate `AddResilienceHandler`, which composes rather than nests.
- **The custom-pipeline overloads** taking `Action<ResiliencePipelineBuilder<HttpResponseMessage>>` or
  `Action<IHttpClientBuilder>`. They bypassed options validation, and one silently ignored the
  `IHttpFallbackHandler` it accepted. The platform's `AddResilienceHandler` is the supported escape hatch.
- **`AddHttpResilienceOptions` and `AddHttpClientWithResilience`**, with all overloads. See *Changed* below
  for what replaces them.
- **`Bulkhead`.** Replaced by `ConcurrencyLimiter`, which is the platform's own name for the same control.

### Changed — breaking

- **Root configuration section is `HttpResilience`**, was `HttpResilienceOptions`. Per-client overrides live
  at `HttpResilience:Clients:{name}` and are bound on top of the root, so a client states only what it
  changes. 1.0.0 had no inheritance: each client pointed at a whole independent section.
- **Registration is two calls with new names**, and the extensions moved to the
  `Microsoft.Extensions.DependencyInjection` namespace:

  | 1.0.0 | 2.0.0 |
  | --- | --- |
  | `services.AddHttpResilienceOptions(configuration)` | `services.AddHttpResilience(configuration)` |
  | `builder.AddHttpClientWithResilience(configuration)` | `builder.AddHttpResilience(name?)` |
  | `PipelineOrder: [ "Hedging" ]` | `builder.AddHedgedHttpResilience(name?)` |
  | `services.AddHttpResilienceHealthChecks(...)` | unchanged in name; `failureStatus` parameter removed |

- **All durations are `TimeSpan` strings**, was integer seconds. `"Timeout": { "TotalRequestTimeoutSeconds": 30 }`
  becomes `"Timeout": { "Total": "00:00:30" }`.
- **Keys renamed**, with defaults changed where the old default was wrong for same-cluster calls:

  | 1.0.0 key | 2.0.0 key | Default |
  | --- | --- | --- |
  | `Timeout:TotalRequestTimeoutSeconds` (30) | `Timeout:Total` | `00:00:20` |
  | `Timeout:AttemptTimeoutSeconds` (10) | `Timeout:Attempt` | `00:00:05` |
  | `Retry:MaxRetryAttempts` (3) | `Retry:MaxRetries` | `2` |
  | `Retry:BaseDelaySeconds` (2.0) | `Retry:BaseDelay` | `00:00:00.500` |
  | `RateLimiter:WindowSeconds` | `RateLimiter:Window` | `00:00:01` |
  | `RateLimiter:TokenBucketCapacity` | `RateLimiter:TokenLimit` | required |
  | `RateLimiter:ReplenishmentPeriodSeconds` | `RateLimiter:ReplenishmentPeriod` | `00:00:01` |
  | `RateLimiter:SegmentsPerWindow` (2) | unchanged | `8` |
  | `Bulkhead:Limit` | `ConcurrencyLimiter:Limit` | required when enabled |
  | `Connection:PooledConnectionLifetimeSeconds` (600) | `Connection:PooledConnectionLifetime` | `00:02:00` |
  | `Connection:PooledConnectionIdleTimeoutSeconds` (120) | `Connection:PooledConnectionIdleTimeout` | `00:01:00` |
  | `Connection:ConnectTimeoutSeconds` (21) | `Connection:ConnectTimeout` | `00:00:03` |
  | `Connection:MaxConnectionsPerServer` (10) | unchanged | unset (runtime default) |

  `Retry:MaxAttempts` is bound to a tombstone that fails startup naming `Retry:MaxRetries`, rather than being
  aliased: it counts retries *after* the first attempt, so a file written against the other name has
  arithmetic that is off by one.
- **`RateLimiter:PermitLimit`, `TokenLimit` and `TokensPerPeriod` have no defaults** and are required by their
  algorithm. Each is a contract with a specific downstream, and 1.0.0's default of 1,000 was a number nobody
  chose.
- **Only RFC 9110 safe methods are retried or hedged.** 1.0.0 applied no method filter at all, so a transient
  failure re-sent a POST body to the origin. This is an allow-list — GET, HEAD, OPTIONS, TRACE — rather than a
  deny-list of the five familiar mutating verbs, so an unrecognized method such as `MOVE` or `PURGE` is not
  repeated either. Opting in is per client and explicit; see *Added*.
- **Validation replaces data annotations** with rules that can express `TimeSpan` ranges and cross-property
  relationships, and that run for every named options instance rather than only the default one. Every message
  names the config path, the value, the expectation and the reason.
- **Configuration is restart-only.** Options are registered with `Configure` rather than `Bind`, so no reload
  token exists and `IOptionsMonitor` cannot report a value that is not in effect. 1.0.0 bound the section and
  appeared to support reload while pipelines were built once.
- **`AddHttpResilienceHealthChecks` no longer takes a `failureStatus`.** The check reports **Degraded** at
  worst, unconditionally — the ceiling is the guarantee that a dependency outage cannot restart a healthy pod.

### Added

- **`AddHedgedHttpResilience`**, a separate registration for the hedging pipeline. It requires a
  `PipelineSelection:Authorities` allow-list, because the hedging handler keeps a circuit breaker, a
  concurrency limiter and a metric series per request authority for the life of the process.
- **Per-client opt-in for repeating mutating requests**: `Retry:RetryableMethods` (an explicit allow-list) and
  `Retry:DisableForUnsafeHttpMethods` / `Hedging:DisableForUnsafeHttpMethods`. Neither flag may be `false` at
  the root, and the root may not name an unsafe method in `RetryableMethods`: one key there would decide it for
  every client in the process, including clients registered afterwards that state nothing.
- **`Timeout:Client`**, a finite `HttpClient.Timeout`. The platform's standard handler sets it to infinite, and
  pipeline timeouts stop applying at response headers, so a response body that stalls afterwards was bounded by
  nothing. It defaults to `Timeout:Total` plus **30 seconds** and is applied after every `ConfigureHttpClient`
  registration, so a conflicting assignment in code fails at client creation instead of silently winning. The
  allowance covers limiter queue wait and the response body only, since `Timeout:Total` already covers every
  attempt up to headers; a pre-release build used one minute, which was three times the whole default attempt
  budget for body bytes alone and was inherited unstated by every client that adopted the package.
- **`ConcurrencyLimiter:Backstop`** (default 1,000), surfacing the concurrency limiter the platform's standard
  handler always carries in its limiter slot. Unconfigured, that limit was a scaling cliff that reported itself
  as `RateLimiterRejectedException` from a rate limiter nobody had enabled.
- **`ConcurrencyLimiter:Enabled` / `Limit` / `QueueLimit`**, the client's own concurrency bound, applied
  outside the pipeline so one slot covers a whole logical request including its retries.
- **`PipelineSelection:Authorities`**, required by `Mode: ByAuthority` and by every hedged client, so the
  number of pipelines and metric series is fixed at deploy time rather than by request data.
- **`Connection:AllowAutoRedirect`**, resolved to `false` for hedged clients and applied even with
  `Connection:Enabled` off. Redirects are resolved inside `SocketsHttpHandler`, below every
  `DelegatingHandler`, so an authority allow-list cannot see the second hop.
- **`Retry:Enabled`**, the supported way to switch retries off without setting a count to zero.
- **`AllowUnusedClientSections`** and **`ValidateClientsOnStart`**, both root-only. The first controls whether
  a `Clients:{name}` section no client reads fails startup; the second controls the hosted service that creates
  every configured client at host start, so a client whose handler chain cannot be built fails the deployment
  rather than the first request hours later. Both default to the safe direction.
- **`ValidateHttpResilienceClientsOnStart()`**, the code equivalent of the second key. Stating the key `false`
  beside the call fails startup naming both.
- **`AddHealthChecks().AddHttpResilience()`**, matching the shape the rest of the health-check ecosystem uses.
  Both registrations are idempotent under one name, so a shared platform extension and the application using it
  may both call them.
- **Three `ObservableGauge`s** on a new `HttpResilience.NET` meter (`HttpResilienceTelemetryExtensions.MeterName`):
  `http.resilience.circuit_breaker.state`, `http.resilience.limiter.available_permits` and
  `http.resilience.limiter.queued_requests`. Polly counts breaker *events*, which cannot answer "is it open"
  once a scrape is missed, and limiter statistics were never read.
- **Twelve source-generated log events** under the category `HttpResilience`, including three emitted once at
  startup: a client registered with `Enabled: false` (Warning), a client that can repeat a mutating request
  (Warning), and the traffic a client's circuit breaker needs before it can open at all (Information).
- **Trimming and Native AOT support.** Option binding goes through the configuration binding source generator,
  and `tests/HttpResilience.NET.AotSmoke` publishes a Native AOT binary in CI and asserts bound values at run
  time — a trimmed reflective binder leaves a client on defaults it never configured rather than failing.
- **Benchmarks** under `benchmarks/`, with the raw reports checked in beside the summaries in
  `docs/benchmarks/` and a CI gate comparing them.

- **Event 13**, a Warning naming any client whose redirect bound could not be applied because its primary
  handler is neither a `SocketsHttpHandler` nor an `HttpClientHandler` and so has no `AllowAutoRedirect` to
  set. Previously that returned in silence. It reports rather than throwing, unlike the `Connection:Enabled`
  path: the handler that reaches this branch in practice is a test stub, which resolves no redirects and
  cannot breach the bound, and the way out of a throw would have been to state `AllowAutoRedirect: true`.
- **`LICENSE`** is packed into the `.nupkg` via `PackageLicenseFile`. The package previously shipped with no
  license metadata at all, which `pack` does not warn about, so the nuspec asserted nothing and a consumer's
  legal or SCA review had only the copyright line to read. The terms are proprietary and internal; an SPDX
  `PackageLicenseExpression` would still be wrong, because it names an open-source grant.

### Fixed

- **Handler ordering was inverted.** 1.0.0 added handlers by iterating `PipelineOrder` from the last entry to
  the first, and `IHttpClientFactory` treats the first handler added as the outermost — so a multi-strategy
  order composed backwards and the limiters ran inside the retry loop, each retry consuming its own permit.
  Ordering is now fixed, and pinned by a test that counts origin calls.
- **A second registration on one client nested two pipelines**, multiplying retries rather than adding them.
  It now fails at startup. A consumer calling the platform's own `AddStandardResilienceHandler` on a client
  that already has a pipeline cannot be refused — the two are indistinguishable through public API — so that
  case is reported at Information (event 12) with the count.
- **Connection settings could be silently removed.** 1.0.0 applied them through
  `ConfigurePrimaryHttpMessageHandler`, which is last-wins, so any later registration took the handler and
  every setting with it. They are now applied from an `IHttpMessageHandlerBuilderFilter` that runs after every
  registration: a `SocketsHttpHandler` you supplied is kept and configured, the factory's default
  `HttpClientHandler` is replaced, and anything else fails at client creation with a message saying why.
- **`Connection:Enabled` reversed a redirect bound set on a consumer's own handler.** The connection settings
  were applied from the *resolved* `AllowAutoRedirect`, and for a standard client that states nothing that
  resolves to the runtime default of `true` — so a `SocketsHttpHandler` hardened with
  `AllowAutoRedirect = false` had redirects switched back on by a connection-pool switch, silently, while
  `docs/TROUBLESHOOTING.md` said the handler's other settings were preserved. The runtime strips
  `Authorization` across a redirect and re-sends `X-Api-Key` and every other custom credential header
  verbatim, so this was a credential-disclosure path opened by the resilience package. `AllowAutoRedirect` is
  now written onto the primary handler only when the schema states it or resolves it to `false`; `false`
  always wins, because it is the hedged client's destination bound. `MaxConnectionsPerServer` was already
  conditional; the other four connection properties are still applied unconditionally and the documentation
  now enumerates exactly which.
- **The rate limiter was built from the options instance captured at registration**, so a consumer's
  `PostConfigure` raising `PermitLimit` was reported by `IOptionsMonitor` and was not in effect. Every limiter
  is now created from live options inside its keyed factory.
- **Per-authority pipeline selection had no allow-list**, so a destination derived from request data
  permanently allocated a pipeline, a circuit breaker and a metric series per authority, with nothing to evict
  them.
- **Hedging repeated mutating requests down the timer path.** Polly starts a hedged attempt either because an
  attempt failed or because the delay elapsed while attempts were still running, and only the first consults an
  outcome predicate — so a guard written as `ShouldHandle` alone let a slow POST reach the origin with its body
  `1 + MaxHedgedAttempts` times. The timer path is closed separately, by suppressing the attempt itself.
- **`error.type` was missing on exception outcomes.** `AddHttpResilienceTelemetry()` adds it, carrying the
  exception type name, for exception outcomes only and never where the platform has already set it.

## [1.0.0] - 2026-05-09

Initial release. Superseded by 2.0.0; see [docs/V1.md](docs/V1.md) for using this version.
