# Production checklist

## Idempotency — do this first

- [ ] No client sets `Retry:RetryableMethods` to include any method other than GET, HEAD, OPTIONS or TRACE unless that endpoint deduplicates on an idempotency key. Non-standard verbs count: `MOVE`, `PURGE`, `MERGE` and friends are not repeated by default. The root section is refused outright for unsafe entries, so every one of these is in a `Clients:{name}` section and reviewable per client.
- [ ] Startup logs were read for **event ID 10**. It is the fleet inventory of every client that can duplicate a mutation, and it names the key each one came from.
- [ ] Any request on a client with a mutating method in `RetryableMethods` carries re-playable content — buffered, or rebuilt per attempt. A non-seekable stream delivers an empty body on every retry, without throwing.
- [ ] No client sets `Retry:DisableForUnsafeHttpMethods` or `Hedging:DisableForUnsafeHttpMethods` to `false` without a named owner and a written reason. (Neither can be set at the root at all — startup fails.)
- [ ] The startup log has been read and **every event ID 10 line is expected**. One line per client that can repeat a mutating request, naming the methods and the key. An unexpected line is a duplicate-side-effect risk shipped by accident.
- [ ] Any client registered with `AddHedgedHttpResilience` sends only idempotent requests, and its endpoint tolerates *simultaneous* duplicates.

## Amplification budget

- [ ] For each client registered with `AddHttpResilience`: `replicas × RPS × (1 + Retry:MaxRetries)` computed, and the dependency can absorb it while failing. See [OPERATIONS.md](OPERATIONS.md#retry-amplification).
- [ ] For each client registered with `AddHedgedHttpResilience`: `replicas × RPS × (1 + Hedging:MaxHedgedAttempts)`, for the share of requests slow enough to start a supplementary attempt. **The two do not compound on one client** — a client is registered with one call or the other, and the hedging pipeline has no retry strategy at all. They compound *across* clients: sum the arithmetic per client that reaches the host, rather than multiplying the two ceilings together.
- [ ] `Retry:UseJitter` left on.
- [ ] **`Retry:BaseDelay` reviewed against where this dependency actually is.** The default of `00:00:00.500` is deliberately below the platform's 2 s and is tuned for same-cluster calls: with the default `Exponential` and `MaxRetries: 2`, all three attempts land inside about 1.5 s before jitter. That is fine against a healthy in-cluster dependency and aggressive against one in a cold start, a GC pause or a rolling restart — in every replica at once. Raise it to `00:00:01` or more for anything crossing a region or a cold-start boundary, and confirm startup still passes: the retry-budget rule holds the whole schedule inside `Timeout:Total`, so a larger delay may need a larger total.

## Scope

- [ ] `RateLimiter:PermitLimit` set as `downstream quota ÷ (replicas × clients that reach the host)`, not the global quota, and the number revisited when autoscaling limits change. The budget belongs to a *named client*, so two clients calling one host hold two independent budgets.
- [ ] **`CircuitBreaker:MinimumThroughput` reachable by a *single replica's* traffic, or the breaker never engages — and the arithmetic was done, not assumed.** The rate this client needs is `MinimumThroughput / SamplingDuration` failing **attempts** per second, in one replica: at the defaults that is `100 / 30s` = **3.3 attempts/sec**, which at the default retry count is about 1.1 failing caller requests per second *per replica*. A service at 100 RPS across 20 pods and 8 clients does not reach that on most of them. **Event ID 11 states this number per client at startup — read it, at Information, and confirm each client's real per-replica rate exceeds it.** Below the rate the client has timeouts and no circuit breaker, and nothing in its telemetry says so, because a breaker that never opens emits exactly what a healthy one emits. Lower `MinimumThroughput` for a quiet client.
- [ ] `PipelineSelection:Mode = ByAuthority` used only with a populated `Authorities` allow-list.
- [ ] Every hedged client lists the authorities it may call, and the list is reviewed when a downstream host is added.
- [ ] Limiter budgets sized per client, not per host: `ByAuthority` isolates circuit breakers, not limiters.
- [ ] `ConcurrencyLimiter:Backstop` (default 1,000, no queue) is above this client's expected peak concurrency, or raised deliberately. It is the bound unless the client sets its own `Limit`, which validation holds at or below it; either way something caps in-flight requests. Note the scope changes with the shape: with no rate limiter it is one limiter per *pipeline*, so `ByAuthority` and hedged clients get it per authority; with a rate limiter it moves to a handler of its own and is per client.

## Timeouts

- [ ] `Timeout:Attempt` set from the dependency's p99, not a round number.
- [ ] `Timeout:Total` accommodates the retry schedule — startup validation enforces this, so a passing startup is the check.
- [ ] `QueueLimit` kept small wherever an SLO is written against `Timeout:Total`: queue wait happens before admission and is outside the budget.
- [ ] `Connection:ConnectTimeout` comfortably below `Timeout:Attempt`.
- [ ] `Timeout:Client` left at its default of `Timeout:Total` + 30 s unless this client has a deep limiter queue or downloads large bodies. It bounds the response-body transfer that `Timeout:Total` cannot reach. The allowance was one minute before 2.0.0; if you are porting numbers from a pre-release build, re-read them.
- [ ] Any client that streams large responses uses `HttpCompletionOption.ResponseHeadersRead` with its own read deadline, rather than raising `Timeout:Client` until it stops meaning anything.

## Connections

- [ ] `Connection:MaxConnectionsPerServer` left unset unless sized for a specific dependency.
- [ ] `Connection:PooledConnectionLifetime` suits how fast your service discovery moves endpoints.
- [ ] Any client with `Connection:Enabled` uses a `SocketsHttpHandler` as its primary handler, or none at all. Stubs and test handlers belong on clients with `Connection:Enabled` false.
- [ ] Any client that supplies its own `SocketsHttpHandler` **and** sets `Connection:Enabled` has had the overwrite list read: `ConnectTimeout`, `PooledConnectionIdleTimeout`, `PooledConnectionLifetime` and `EnableMultipleHttp2Connections` are overwritten unconditionally. A redirect bound set on your own handler is *not* overwritten, and neither is `MaxConnectionsPerServer` unless the schema states it.
- [ ] Startup logs were read for **event ID 13**. Each line is a client whose redirect bound could not be applied because its primary handler has no `AllowAutoRedirect`. A stub is expected and harmless; a handler wrapping a `SocketsHttpHandler` of its own is a hedged client whose authority allow-list a 3xx can step around.

## Registration hygiene

- [ ] **`HttpResilience:ValidateClientsOnStart` is not set to `false`.** It defaults to `true` and is the control that turns "this deployment fails" into the outcome instead of "a rare code path returns 500s hours later". Startup validation covers everything expressible as an options value; it cannot cover which primary handler a client ends up with, because that is decided by the service collection and does not materialize until `IHttpClientFactory` builds the chain. Turning it off restores the old failure mode deliberately, so it needs a named owner and a written reason like any other guard being switched off.
- [ ] The application runs on a **generic host**. The probe is an `IHostedService`, so a bare `ServiceCollection` plus `BuildServiceProvider` runs neither it nor `ValidateOnStart`.

- [ ] No client calls `AddStandardResilienceHandler` or `AddStandardHedgingHandler` **in addition** to `AddHttpResilience`. That nests two pipelines: retries multiply rather than add — three configured attempts become nine origin calls — and the total timeout is applied twice. It is reported at Information (event 12) and not refused, because the package cannot tell it from the `AddResilienceHandler` it recommends. **Grep the codebase for this; do not rely on the log.**
- [ ] Startup logs were read for **event ID 12**. Every line is either a known `AddResilienceHandler` or a nesting bug.
- [ ] No client sets `HttpClient.Timeout` through `ConfigureHttpClient`. Client creation fails if it does; the supported key is `Timeout:Client`.
- [ ] **No typed client assigns `HttpClient.Timeout` in its constructor.** This is the one shape the guard above cannot see — the constructor runs after `IHttpClientFactory` has finished building the client — and it silently truncates the pipeline with a bare `TaskCanceledException`. **Grep typed-client constructors; nothing logs it.** The tell in production is a `TaskCanceledException` naming a `HttpClient.Timeout` that is not what `Timeout:Client` resolves to.
- [ ] Anything the schema does not express is added with `AddResilienceHandler`, not by reaching for the platform's standard handler.

## Observability

- [ ] An OpenTelemetry SDK is wired in the **service** — this package references none and exports nothing on its own. See [README.md](../README.md#telemetry).
- [ ] `AddHttpResilienceTelemetry()` called. It adds the `error.type` tag and registers **no meter**; it is not a substitute for `AddMeter`.
- [ ] Both meter names registered: `HttpResilienceTelemetryExtensions.PollyMeterName` (`"Polly"`) **and** `HttpResilienceTelemetryExtensions.MeterName` (`"HttpResilience.NET"`). A meter name the SDK was not given is dropped silently — no log line, no exception, an empty dashboard.
- [ ] On `OpenTelemetry.NET` package: **2.7.0 or later registers both meters itself** — check this box by confirming the version, not by adding configuration. On 2.6.x or earlier, both names are in `OpenTelemetryOptions:Meters`.
- [ ] `AddHttpClientInstrumentation()` registered on **both** metrics and tracing. The per-attempt spans are how a retry is visible in a trace; this package emits no span of its own.
- [ ] A query against each of `resilience.polly.strategy.events`, `http.resilience.circuit_breaker.state` and `http.client.request.duration` was **run** and returned a series. Registration was not assumed from the code.
- [ ] Logs from category `HttpResilience` reach the backend, and events **6, 10 and 11** (emitted once at host start) are retained for at least a deployment cycle. They are startup-only and answer incident questions.
- [ ] No custom enrichment adds a dimension derived from URLs, tenants, users or correlation IDs.
- [ ] Alerts configured for retry rate, breaker open, breaker thrashing and limiter rejections.
- [ ] Breaker alerting reads `http.resilience.circuit_breaker.state`, **not** the status code of `/healthz/deps`. The check reports `Degraded` at worst and `Degraded` is HTTP 200 by default, so a status-code alert stays green with every circuit open.
- [ ] `http.resilience.limiter.queued_requests` is graphed for every client with a `QueueLimit` above 0, whatever the `kind`. That wait is outside `Timeout:Total`.
- [ ] Any client relying on `ConcurrencyLimiter:Backstop` for its concurrency bound is understood to have **no gauge** for it unless a rate limiter has displaced the backstop. Set `ConcurrencyLimiter:Limit` if the number needs watching.

## Health

- [ ] `AddHttpResilienceHealthChecks()` (or `AddHealthChecks().AddHttpResilience()`) called. Idempotent under one name, so a shared platform extension calling it too is safe.
- [ ] The `dependency` tag is **excluded** from liveness and readiness predicates.
- [ ] A separate diagnostic endpoint exposes it.

## Wiring

- [ ] `AddHttpResilience(configuration)` called before any client registration. Calling it more than once with the same section is safe; a second, different section fails at startup.
- [ ] Every client that should have a pipeline sets `Enabled: true` — the default is `false`, so an unconfigured client has none.
- [ ] The deployment's startup logs are checked for the `registered but disabled` Warning, which is the only signal that a client was left without a pipeline by accident.
- [ ] No client has resilience added twice — a shared registration extension plus an application call is the usual way in, and it now fails at startup.
- [ ] No section under `HttpResilience:Clients` is left over from a renamed or deleted client — startup validation enforces this, so a passing startup is the check. Note a **typed** client is named after `TClient`: `AddHttpClient<IOrdersApi, OrdersApi>()` reads `Clients:IOrdersApi`, so pass the section name explicitly if that is not what you want.
- [ ] The service starts cleanly — startup validation is the gate, and it runs before traffic is served.

## Releasing

- [ ] `PublicAPI.Unshipped.txt` moved into `PublicAPI.Shipped.txt`, and its diff reviewed as the public-API change list for this release.
- [ ] `PackageValidationBaselineVersion` set to the last published version **within this major**, or left unset if this is the first release of a new major — see [VERSIONING.md](VERSIONING.md).
- [ ] `CHANGELOG.md` entry classifies every change by [VERSIONING.md](VERSIONING.md) — a default change and a new validation rule are both MAJOR.

## Operating

- [ ] The team knows resilience configuration changes require a restart.
- [ ] The team knows an open breaker means a *dependency* is unhealthy, and that rolling restarts reset every breaker at once.
