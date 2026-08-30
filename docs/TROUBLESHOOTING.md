# Troubleshooting

## `InvalidOperationException: Call services.AddHttpResilience(configuration) before...`

`AddHttpResilience` on the service collection registers the configuration section that per-client registrations build on. Call it once, before any `AddHttpClient(...).AddHttpResilience(...)`.

## Startup throws `OptionsValidationException`

Intended. The message names the configuration path, the value, the expectation and the reason. Common ones:

| Message fragment | Cause |
| --- | --- |
| `Retry.MaxRetries ... set Retry.Enabled to false` | `0` is not a valid attempt count; the underlying strategy requires at least 1. |
| `strictly less than Timeout.Total` | The platform rejects an attempt timeout equal to the total. |
| `at least double Timeout.Attempt` | `CircuitBreaker:SamplingDuration` is too short to observe completed attempts. |
| `cannot fit in the total budget` | The retry schedule needs more time than `Timeout:Total` allows, so the configured retries would never run. |
| `RateLimiter.PermitLimit` | Required when the limiter is enabled; there is no safe default. |
| `strictly greater than Timeout.Total` | `Timeout:Client` is the outer backstop that covers queue wait and the response-body transfer *on top of* the total budget. At or below it, it truncates the pipeline. |
| `every key under Retry is bound and never read` | A client registered with `AddHedgedHttpResilience` states `Retry:*` keys of its own. The hedging pipeline has no retry strategy. Remove them, or use `AddHttpResilience`. |
| `this setting decides which handlers the client is built from` | A `Configure` or `PostConfigure` registered *after* the client changed one of the handful of settings that decide which handlers exist — `Enabled`, `RateLimiter:Enabled`, `ConcurrencyLimiter:Enabled`, `PipelineSelection:Mode`, `Connection:Enabled`, `Connection:AllowAutoRedirect`. Every *other* setting does reach the pipeline that way; only these cannot. Move this one to the `configure` parameter or to the configuration section. |
| `PipelineSelection.Authorities` | `ByAuthority` requires an allow-list, so the pipeline count stays bounded. |

## `InvalidOperationException: Resilience is already configured for HTTP client ...`

`AddHttpResilience` (or `AddHedgedHttpResilience`) was called twice for the same client name. Two pipelines nest rather than merge: retries multiply instead of adding, and the total timeout is applied twice. Remove the duplicate call -- the usual cause is a shared registration extension that already adds resilience being called by an application that adds it again. A client that needs one extra strategy should add it with `AddResilienceHandler`.

## `HttpRequestException: A hedged client cannot send a request to ...`

The request's authority is not in `PipelineSelection:Authorities`. A hedged client allocates a circuit breaker, a concurrency limiter and a metric series per authority and never evicts them, so the set has to be fixed at deploy time. Add the authority, or register the client with `AddHttpResilience` instead.

It is an `HttpRequestException` because it is raised by a `DelegatingHandler` on the request path, not at registration — the exception type a caller already handles for a request that could not be sent.

## `services.Configure<HttpResilienceOptions>(...)` appears to be ignored

Usually it is not. Each pipeline reads `IOptionsMonitor<HttpResilienceOptions>.Get(name)` *inside* the delegate the platform invokes when it first builds that pipeline, which is after every `Configure` and `PostConfigure` has run — so a value changed there is the value the pipeline runs on, and it is the value `IOptionsMonitor` reports. Every limiter is built the same way, from live options inside its keyed factory.

There is one class of exception, and it fails startup rather than being ignored: the settings that decide **which handlers exist**. `IHttpClientFactory` composes a client's chain while the service collection is being built, so `Enabled`, `RateLimiter:Enabled`, `ConcurrencyLimiter:Enabled`, `PipelineSelection:Mode`, `Connection:Enabled` and the resolved `Connection:AllowAutoRedirect` cannot honour a later change. Changing one of those after the client is registered fails at startup with a message naming it — see the `this setting decides which handlers the client is built from` row above.

If a value genuinely does not appear to take effect and startup was clean, check the options *name*: these are named options keyed on `IHttpClientBuilder.Name`, so `services.Configure<HttpResilienceOptions>("Orders", …)` reaches the client registered as `AddHttpClient("Orders")` and the unnamed overload reaches none of them.

The clearest place for a value set in code is still the `configure` parameter, because it sits on the line that registers the client and is validated eagerly there:

```csharp
services.AddHttpClient("Orders")
    .AddHttpResilience(configure: options => options.Retry.MaxRetries = 1);
```

## A client is running on root defaults, ignoring its section

This now fails at startup rather than happening silently — see the next entry. If you are on a version that
still allows it: the section name defaults to the client's own name, so `AddHttpClient("Orders")` reads
`HttpResilience:Clients:Orders`. Check the spelling, and note that `AddHttpResilience(string.Empty)`
deliberately means "root values only".

## Startup fails: `... no registered HTTP client reads this section`

A section under `HttpResilience:Clients` that nothing reads. Three usual causes:

1. **A typo**, in the section or in the client name.
2. **A typed client.** Its name comes from `IHttpClientFactory`, and for `AddHttpClient<TClient, TImplementation>()`
   that is `TClient` — `AddHttpClient<IOrdersApi, OrdersApi>()` reads `Clients:IOrdersApi`, not
   `Clients:OrdersApi`. Either rename the section or pass the name: `AddHttpResilience("Orders")`.
3. **A client that was renamed or deleted** and left its section behind.

The message lists the sections that *are* read, which is usually enough to see which of the three it is. If
one configuration file is deliberately shared by services registering different clients, set
`HttpResilience:AllowUnusedClientSections` to `true` — root-only, and it defaults to failing.

## Startup fails: `Retry.MaxAttempts ... Expected the key Retry.MaxRetries instead`

`Retry:MaxAttempts` is not a key this schema reads. The key is `Retry:MaxRetries`, and it counts retries
*after* the first attempt: `MaxRetries: 2` sends three requests. It is refused rather than silently aliased,
because a file using the other name was probably written by someone who read it as a total, and that
arithmetic needs re-reading.

## Nothing in the logs, and no resilience applied

`Enabled` is opt-in and defaults to `false`. Look for the **Warning** each disabled client logs at startup, naming the exact key to set — `HttpResilience:Enabled`, or `HttpResilience:Clients:{name}:Enabled` for one client. It is emitted when the host validates options, before traffic is served, so it is in the deployment's own logs rather than somewhere later.

If there is no warning either, the client was never registered with `AddHttpResilience` — or nothing resolved the options, which in a non-hosted container (a raw `ServiceProvider`, as in the sample) happens at first client creation rather than at startup.

## `InvalidOperationException: AddHttpResilience was already called with configuration section ...`

The root registration was called twice with different sections. Clients registered before the second call would read the first section and clients after it the second, so the two would disagree with nothing to show it. Call it once, with one section. Calling it repeatedly with the *same* section is fine and does nothing — that is what lets a shared platform extension and an application both call it.

## `RateLimiterRejectedException` on a client that has no rate limiter

The standard resilience handler always carries a limiter in one fixed slot. With `RateLimiter:Enabled` false that slot holds a concurrency limiter, sized by `ConcurrencyLimiter:Backstop` (1,000 by default) with no queue, so above that many concurrent requests the excess is rejected rather than queued. The exception names a rate limiter because that is the strategy type; the cap is on concurrency.

With `RateLimiter:Enabled` **true**, that slot holds your rate limiter instead and the backstop moves to a handler of its own outside it — unless the client also sets `ConcurrencyLimiter:Limit`, in which case that cap is the tighter bound (validation holds it at or below `Backstop`) and the extra handler is skipped. In every case something is capping in-flight requests; which number to raise depends on which of the three is in force, and the log line names it.

Look for the Warning that accompanies it (event ID 7): it names the client, the current backstop and the configuration key to raise, because the exception type alone cannot be told apart from a configured rate limit.

Raise `ConcurrencyLimiter:Backstop` if the client genuinely needs more in flight at once, or find out why requests are piling up — a backstop rejection usually means the dependency slowed down.

## `ConcurrencyLimiter:Limit` seems to be ignored above 1,000

It was, and it now fails at startup instead. The client's own cap is applied outside the handler and the backstop inside it, so a `Limit` above `Backstop` was never reached: the excess was rejected by the inner limiter rather than queued by the outer one. Raise `Backstop` alongside `Limit`.

## A hedged client is duplicating a slow POST

It should not, and there is a test for exactly this. If you see it, check `Hedging:DisableForUnsafeHttpMethods` has not been set to `false` for that client — and read the diff that set it, because the endpoint must deduplicate under *simultaneous* arrival.

## `InvalidOperationException: ... primary handler is a ... rather than a SocketsHttpHandler`

`Connection:Enabled` is set for this client, which also disables `IHttpClientFactory` handler rotation on the basis that `PooledConnectionLifetime` bounds connection age instead. A primary handler with no such setting leaves nothing recycling the pool or re-resolving DNS. Supply a `SocketsHttpHandler` — or set `Connection:Enabled` to `false` for this client. This is the usual answer when a test or sample stubs the primary handler.

**What a supplied `SocketsHttpHandler` keeps, precisely.** It is configured rather than replaced, so a client certificate, a proxy, an SSL callback and everything else on it survive. Four properties are overwritten unconditionally, because the schema always states them: `ConnectTimeout`, `PooledConnectionIdleTimeout`, `PooledConnectionLifetime` and `EnableMultipleHttp2Connections`. Two are overwritten only when the schema has something to say: `MaxConnectionsPerServer` when it is set, and `AllowAutoRedirect` when it is stated or resolves to `false`. An earlier version of this page said the handler's other settings were preserved without qualification, and `AllowAutoRedirect` was written unconditionally — so a handler hardened with `AllowAutoRedirect = false` had redirects switched back on by `Connection:Enabled` alone. If you have tuned any of the first four yourself, leave `Connection:Enabled` false rather than have this disagree with you.

By default this surfaces while the host starts, not on the first request: `AddHttpResilience` registers a
hosted service that creates every client it configured. If you are seeing it from a live request instead,
either `HttpResilience:ValidateClientsOnStart` is `false` for this process, or the application is not running
on a generic host — an `IHostedService` does not run under a bare `ServiceCollection` plus
`BuildServiceProvider`.

## `RateLimiterRejectedException` from a hedged client under high concurrency

The hedging handler carries a per-endpoint concurrency limiter with no queue, sized by `ConcurrencyLimiter:Backstop`. Above that many concurrent requests to one authority, attempts are rejected. This is separate from `RateLimiter:*`, which is per client.

The same is true of a **standard** client under `PipelineSelection:Mode = ByAuthority`: the platform instantiates the whole pipeline per key, so each authority gets a backstop limiter of its own. A client with N listed authorities is bounded at `(N + 1) × Backstop` concurrent requests, counting the shared pipeline — the number is a per-authority cap under that mode, not a per-client one. `RateLimiter:*` does not multiply this way; its limiter is one instance per client. **Enabling one also stops the backstop multiplying**: it displaces the backstop out of the per-authority pipelines into a single handler of its own, so the bound drops back to `1 × Backstop` for the whole client.

## A retry waited far longer than `Retry:BaseDelay`

`Retry:UseRetryAfterHeader` is on by default, so a `Retry-After` response header replaces the computed backoff and there is no cap on the value an origin may name. The wait is still bounded — the total timeout wraps the retry loop, so a `Retry-After: 3600` surfaces as a `TimeoutRejectedException` at `Timeout:Total` rather than as an hour-long request. What it costs meanwhile is the concurrency slot or rate-limit permit the request is holding. Set `Retry:UseRetryAfterHeader` to `false` for a client whose origin cannot be trusted to name a sane value.

## A request took longer than `Timeout:Total`

Two reasons, and they are different.

`Timeout:Total` starts when the request is **admitted**. Time spent queued for a rate-limit permit or a concurrency slot is outside it, bounded by `Timeout:Client` and the caller's `CancellationToken`. Reduce the queue limits, or measure from the caller if the SLO covers queueing.

`Timeout:Total` also **stops at response headers**. Every strategy lives in the handler chain, and the chain returns as soon as the headers arrive; the body is buffered afterwards by `HttpClient` itself. A large or slow download therefore adds time no pipeline timeout can see. `Timeout:Client` bounds it; for streaming, use `HttpCompletionOption.ResponseHeadersRead` and impose your own read deadline.

## Configuration changes have no effect

Resilience configuration is read once at startup. Restart the process. There is no reload token, so a changed configuration file cannot leave `IOptionsMonitor` reporting a value the pipeline is not running.

## Retries are not happening

- The method is not GET, HEAD, OPTIONS or TRACE. Only those four are retried by default, so POST and PATCH are excluded and so is any non-standard verb — `MOVE`, `PURGE`, `MERGE`. Name it in `Retry:RetryableMethods` to opt in.
- `Retry:RetryableMethods` is set and does not include the method — the allow-list replaces the default guard entirely. Check the **root** section as well as the client's: the list is inherited when a client states none. Event ID 10 at startup names where it actually came from.
- `Retry:Enabled` is false.
- The status is not transient. Retried: 408, 429, 5xx, `HttpRequestException`, attempt timeouts. Not retried: 4xx other than 408 and 429.
- The circuit is open, so requests fail fast without attempting.

## Retries are happening on a POST

See the same list in reverse, plus a retry loop in application code above the client.

## A retried POST arrives with an empty body

The content was not replayable. A retry re-sends the same `HttpRequestMessage`, and a `StreamContent` over a non-seekable stream has already been consumed by the first attempt — so the retries deliver nothing, without throwing. Buffer it first with `await content.LoadIntoBufferAsync()`, or build fresh content per attempt. `StringContent`, `ByteArrayContent` and `JsonContent` are already buffered and replay correctly.

## `RateLimiterRejectedException` on a request that should have fit

A permit covers one logical request including its retries, so a single retrying caller cannot exhaust its own budget. If you see this, the budget genuinely ran out: check `replicas × PermitLimit` against the real downstream quota.

## `TaskCanceledException` with no inner exception

Either the caller cancelled, or `Timeout:Client` fired. Pipeline timeouts surface as `TimeoutRejectedException` instead, so a bare cancellation is one of those two.

`Timeout:Client` is `HttpClient.Timeout`, and it defaults to `Timeout:Total` plus 30 seconds. It bounds the two things `Timeout:Total` cannot: time queued for a limiter permit or slot, which happens before admission, and the response-body transfer, which happens after the handler chain has already returned the headers. Hitting it means one of those ran long. `Microsoft.Extensions.Http.Resilience` sets this to infinite on its own; this package puts a finite value back so a stalled body cannot hold a connection forever.

## Health check reports Degraded but the service is fine

That is the intended meaning: a *dependency* is unhealthy. The check is tagged `dependency` and must not gate liveness or readiness.

## Health endpoint returns 200 while circuits are open

Working as configured, and a trap worth knowing. The check reports `Degraded` at worst — never `Unhealthy` —
and ASP.NET Core's default `ResultStatusCodes` maps both `Healthy` and `Degraded` to **200**. So an alert on
the status code of `/healthz/deps` never fires.

Alert on `http.resilience.circuit_breaker.state` instead. If you want the diagnostic endpoint itself to answer
503, opt in with `ResultStatusCodes` on that endpoint only — never on `/healthz/live` or `/healthz/ready`,
where a 503 would have Kubernetes restart or drain a healthy pod because something downstream is failing.

## Health check shows fewer breakers than expected

A breaker that has never left Closed fires no transition callback, so it has no entry. Absence means healthy.

## Throughput lower than expected

`Connection:MaxConnectionsPerServer` queues requests inside the connection pool, below the pipeline, where the wait is invisible to retry and timeout telemetry. Unset (the default) means unlimited.

## Connections cycling more often than `PooledConnectionLifetime`

When `Connection:Enabled` is true the package sets the factory handler lifetime to infinite so the two rotation mechanisms do not compound. If you see faster cycling, something else is calling `SetHandlerLifetime` on the same client after `AddHttpResilience`.

## Connections never cycling, and DNS never refreshing

Connection settings are applied by an `IHttpMessageHandlerBuilderFilter` that runs after every `ConfigurePrimaryHttpMessageHandler` registration, so a call added later cannot leave rotation disabled around a handler with the runtime's infinite `PooledConnectionLifetime`. (Another `IHttpMessageHandlerBuilderFilter` registered *before* this one still runs after it — filters compose in reverse — so a consumer with one of those should set `Connection:Enabled` to false and own the pool.) If you are seeing it, check `Connection:Enabled` is actually true for that client — with it false, the package sets neither the lifetime nor the handler.

## No telemetry at all — no metrics, no HTTP spans

The package references no OpenTelemetry package and exports nothing on its own: it publishes on a `Meter` and an `ILogger` and stops there. Adding it to a service with no SDK wired produces no telemetry and no error. See [README.md](../README.md#telemetry) for the wiring.

## Metrics missing

Register **both** meters — `metrics.AddMeter(HttpResilienceTelemetryExtensions.PollyMeterName)` for retries and breaker events, and `metrics.AddMeter(HttpResilienceTelemetryExtensions.MeterName)` for breaker state and limiter saturation.

On `OpenTelemetry.NET` package, check the version first: **2.7.0 and later register both meters unconditionally**, so a missing series there is not a meter registration problem — look at the exporter, the collector, or whether `AddHttpResilience` ran at all. On **2.6.x and earlier** only `Caching.NET` was registered by default; add `"Polly"` and `"HttpResilience.NET"` to `OpenTelemetryOptions:Meters`.

`AddHttpResilienceTelemetry()` only adds the `error.type` dimension for exception outcomes; it does not register a meter. The two are not substitutes and the SDK drops an unregistered meter's measurements silently — no log line and no exception, which is why this looks like the package emitting nothing.

If the meter is registered and a query still returns nothing, check the instrument name. Polly publishes `resilience.polly.strategy.events`, `resilience.polly.strategy.attempt.duration` and `resilience.polly.pipeline.duration`; there is no `resilience.polly.strategy.attempts`. See [OPERATIONS.md](OPERATIONS.md#metrics).

## Retries invisible in traces

This package creates no spans, on purpose. Retried attempts appear as sibling `System.Net.Http` spans under the caller's span, which requires `AddHttpClientInstrumentation()` on the **tracing** builder — registering it only under `WithMetrics` gets durations and no spans. Counting the sibling spans is how you answer "was it retried, and how many times".

## `error.type` shows status codes but no exceptions

`AddHttpResilienceTelemetry()` was not called. The platform tags `error.type` for failed responses on its own; the exception half of that dimension is what this package adds. Without it, connection failures and timeouts appear under `exception.type` only.

## A client name matches a schema property

Fine. Per-client sections live under `HttpResilience:Clients`, so `Retry`, `Timeout` and `Enabled` are ordinary client names.

## Origin sees three times the requests the retry count explains

Check the logs for event **12** at Information: *"client 'X' has N resilience handlers where this package added M"*.

A second `AddStandardResilienceHandler` on a client that already calls `AddHttpResilience` nests two pipelines
rather than merging them, and nested pipelines multiply. Measured: three configured attempts become **nine**
origin calls, and the total timeout is applied twice. Nothing throws, and no retry metric distinguishes it from
a genuinely retried request — the outer pipeline's retries are real retries.

```csharp
// Nests. Origin sees 9 calls for one GET.
services.AddHttpClient("Orders").AddHttpResilience().AddStandardResilienceHandler();

// Composes. Origin sees 3.
services.AddHttpClient("Orders").AddHttpResilience()
    .AddResilienceHandler("extra", p => p.AddTimeout(TimeSpan.FromSeconds(9)));
```

A second `AddHttpResilience` fails at startup; this does not, because the package cannot tell a nested platform
handler from the `AddResilienceHandler` it recommends — both add a `ResilienceHandler`, and the difference is an
internal field. Hence a notice rather than a failure.

## `HttpClient.Timeout` set in code fails at client creation

```text
HTTP client 'Orders' has HttpClient.Timeout set to 00:00:02 in code, and HttpResilience resolves it
to 00:00:50. ... State it as Timeout:Client instead.
```

Deliberate. `ConfigureHttpClient` actions are last-wins, so a timeout set there would otherwise beat this
package and truncate the pipeline below `Timeout:Total` — with a bare `TaskCanceledException` carrying none of the
pipeline's context, which is the exact condition validation refuses when the same value is written as
`Timeout:Client`. Move it to `Timeout:Client`, which is validated to be strictly greater than `Timeout:Total`.

Other uses of `ConfigureHttpClient` — default headers, base address — are untouched.

## A client times out far below `Timeout:Total`, with no message from this package

```text
System.Threading.Tasks.TaskCanceledException: The request was canceled due to the
configured HttpClient.Timeout of 1 seconds elapsing.
```

The phrase **"the configured HttpClient.Timeout of N seconds"** is the tell: that wording comes from
`HttpClient` itself, and `N` is not what `Timeout:Client` resolves to. Something assigned
`HttpClient.Timeout` somewhere the guard above cannot reach. There are exactly two such places.

**A typed client's constructor.** The common one, because it is the documented .NET idiom for configuring a
typed client:

```csharp
public OrdersApi(HttpClient client)
{
    client.Timeout = TimeSpan.FromSeconds(1);   // wins over everything
}
```

The guard is an `IPostConfigureOptions<HttpClientFactoryOptions>`, which is the last phase
`IHttpClientFactory` runs *while building* the client. A typed client's constructor runs after that, on the
instance the factory hands back, so no filter, validator or options phase can see it. Nothing fails and
nothing logs. Move the value to `Timeout:Client`, where it is validated against `Timeout:Total`.
`ConsumerBoundaryTests.ATypedClientsConstructorTimeout_TruncatesThePipeline_AndNoGuardCanSeeIt` pins this.

Exactly 100 seconds through `ConfigureHttpClient` is not a second such shape: it is
refused like any other conflicting value, because the guard establishes "nothing assigned one" with an action
at index 0 of `HttpClientActions` rather than inferring it from the framework's default. If a client needs
precisely 100 seconds, state `Timeout:Client: 00:01:40` and keep `Timeout:Total` beneath it.

The constructor shape is not fixable from inside this package, so it is stated instead. See
docs/ARCHITECTURE.md.

## Limiter gauges report nothing for a client with no rate limiter

Expected, and the one hole in the limiter telemetry. `http.resilience.limiter.*` reports the limiters this
package owns an instance of: `kind=rate`, `kind=concurrency`, and `kind=backstop` only when a rate limiter has
displaced the backstop into a handler of its own.

The undisplaced backstop lives inside the platform's limiter slot, where Polly constructs it — one per
pipeline, which is what makes it per authority under `ByAuthority`. Supplying an instance so its statistics
could be read would turn that per-authority bound into a per-client one. Set `ConcurrencyLimiter:Limit` if you
need a concurrency number you can watch.
