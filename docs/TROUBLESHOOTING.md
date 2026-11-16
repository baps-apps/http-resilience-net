# Troubleshooting

## Startup and registration

### `OptionsValidationException` at startup

Intended: the message names the path, value, expectation and reason. A root unsafe-method rule already broken
in the section throws from `AddHttpResilience` itself.

| Message contains | Fix |
| --- | --- |
| `Retry:MaxRetries ... set Retry:Enabled to false` | `0` is invalid; use `Retry:Enabled: false`. |
| `strictly less than Timeout:Total` | `Timeout:Attempt` must be shorter than `Timeout:Total`. |
| `strictly greater than Timeout:Total` | `Timeout:Client` must exceed `Timeout:Total`. |
| `at least double Timeout:Attempt` | Raise `CircuitBreaker:SamplingDuration`. |
| `between 00:00:00.0100000 and 1.00:00:00` | A timeout outside what the platform accepts. |
| `RateLimiter:PermitLimit` / `TokenLimit` / `TokensPerPeriod` | Required when the rate limiter is enabled; no default. |
| `PipelineSelection:Authorities` | `ByAuthority` and hedged clients need a list; or a standard client in `Mode: None` states one. |
| `which has no retry strategy` / `which has no hedging strategy` | `Retry` keys on a hedged client, or `Hedging` keys on a standard one. Remove them. |
| `Expected true at the root section` | A `DisableForUnsafeHttpMethods` flag is `false` at the root. Set it on the one client that needs it. |
| `is invalid at the root section` | The root `Retry:RetryableMethods` names an unsafe method. Move it to the client. |
| `set in the delegate passed to services.AddHttpResilience(configure)` | The same two root rules, broken in code. |
| `true whenever Retry:RetryableMethods is set` / `Retry:RetryableMethods replaces this flag` | `Retry:DisableForUnsafeHttpMethods` beside a list in force (maybe inherited). Remove the flag, or give the client an empty list. |
| `ConcurrencyLimiter:Limit ... at most ConcurrencyLimiter:Backstop` | Raise `Backstop` together with `Limit`. |
| `Retry:MaxAttempts ... Expected Retry:MaxRetries` | Use `Retry:MaxRetries`, which counts retries after the first attempt (2 = 3 requests). |

### `... no registered HTTP client reads this section`

A `Clients:{name}` section nobody reads. Usually a typo, a renamed client, or a typed client:
`AddHttpClient<IOrdersApi, OrdersApi>()` reads `Clients:IOrdersApi`. Pass the name
(`.AddHttpResilienceHandler("Orders")`) or rename the section. The message lists the sections that are read.
Shared file across services? Set `HttpResilience:AllowUnusedClientSections: true`.

### `... -- unknown key. Expected ...`

The key is not part of the schema, so it would bind to nothing and the default would stay in force. Usually a
typo; the message names the nearest real key. A startup switch (`ValidateClientsOnStart`,
`AllowUnusedClientSections`, `AllowUnknownKeys`) stated in a client section is reported too: those belong at the
root. If one configuration file is shared by services on different versions of this package and holds a key the
older one does not know, state the version the file was written for as `HttpResilience:SchemaVersion` (`"3.1"`):
the older service then starts and logs the key (Warning event 18). `HttpResilience:AllowUnknownKeys: true` turns
the check off entirely.

### `HttpResilience:SchemaVersion -- value '...' names a newer major version`

The file was written for a newer major version of this package, which may rename keys or change what they mean.
Upgrade the package, or give services on different majors their own file.

### `... uses HttpResilience, but services.AddHttpResilience was never called`

Call `AddHttpResilience(configuration)` or `AddHttpResilience(options => ...)` once, anywhere.

### `Resilience is already configured for HTTP client ...`

`AddHttpResilienceHandler` / `AddHttpResilienceHedgingHandler` called twice for one client, often a shared
registration helper plus the app. Remove one; use `AddResilienceHandler` for an extra strategy.

### `... cannot be used inside ConfigureHttpClientDefaults`

That builder has no client name. Call it on each `AddHttpClient(...)`. If the defaults also add
`AddStandardResilienceHandler()` (.NET Aspire), leave it: it is replaced on each client this package configures
([RECIPES.md](RECIPES.md#a-net-aspire-service-servicedefaults)).

### `HttpResilience.NET's platform self-check failed on Microsoft.Extensions.Http ...`

The application resolved a `Microsoft.Extensions.Http` / `Microsoft.Extensions.Http.Resilience` release that
builds this package's canary clients differently: most likely it does not run handler or client actions in
registration order, which this package relies on and the platform does not document. Pin those two packages to
the versions this release was tested with (the newest in its `Directory.Packages.props`, or the last that passed)
and report the versions the message names. `ValidateClientsOnStart: false` skips the check, but the clients may
then run misordered pipelines; treat it as a stopgap.

### `AddHttpResilience was already called with configuration section ...`

Called with two different sections. Use one. The same section twice is fine.

### `AddHttpResilience was called more than once with configuration section ... whose values differ`

Two calls passed the same section path from different configuration objects (a library building its own
`ConfigurationBuilder`, say), and their values differ at the key named, so only the first call's would be read.
Pass the application's `IConfiguration` to every call. Different objects with identical values are accepted.

### `... primary handler is a ... rather than a SocketsHttpHandler`

`Connection:Enabled` needs a `SocketsHttpHandler` (or an `HttpClientHandler` with nothing set, which is
replaced). Supply one, or set `Connection:Enabled: false` for that client (the usual fix for a test stub). Seen
on a live request rather than at startup? `ValidateClientsOnStart` is `false`, or there is no generic host.

### `... primary handler is an HttpClientHandler configured with ...`

`Connection:Enabled` would replace the `HttpClientHandler` with a new `SocketsHttpHandler`, dropping the
settings the message lists (a client certificate, a certificate-validation callback, a proxy, credentials).
Move them to a `SocketsHttpHandler`: `ClientCertificates` and the callback go in `SslOptions`
(`ClientCertificates`, `RemoteCertificateValidationCallback`), and `Proxy`, `Credentials` and `CookieContainer`
keep their names. Or set `Connection:Enabled: false` for that client.

### `... its SocketsHttpHandler sets ... Connection:Enabled would overwrite those values`

The handler you supplied sets a connection setting (`ConnectTimeout`, `PooledConnectionIdleTimeout`,
`PooledConnectionLifetime`, `EnableMultipleHttp2Connections`, or `MaxConnectionsPerServer` when the key is
stated) to a value other than the resolved one. Move the value to the client's `Connection` section and remove it
from the handler, or set `Connection:Enabled: false` for that client. The handler's other settings
(`SslOptions`, `Proxy`, `Credentials`) are kept.

### Startup Warning: `retry schedule ... can exceed Timeout:Total` (event 14)

If every attempt timed out, later retries would be cut off. Fine if `Timeout:Total` is meant as a cap;
otherwise raise it or lower `MaxRetries` / `Timeout:Attempt`.

### Startup Warning: `hedging ... starts the next attempt after ...` (event 21)

`Hedging:Delay` is below 10 ms, so a hedged attempt starts before almost any response can arrive and the
dependency receives `MaxHedgedAttempts` + 1 requests for nearly every call. Set the delay to about the
dependency's p95 latency, so only the slowest requests are hedged. Keep it only if the dependency is sized for the
full fan-out.

## Configuration not taking effect

- **No resilience and no logs?** Look for startup Warning event 6, which names the key that disabled the client
  (`HttpResilience:Enabled` is the kill switch). No event 6 either: the client was not registered with
  `AddHttpResilienceHandler`, or there is no generic host (no startup notices).
- **Client running on root values?** It reads the section named after the client; check spelling and the
  typed-client rule. A client with no section of its own reads the root values. Event 15 logs each client's
  effective settings.
- **`services.Configure<HttpResilienceOptions>(...)` ignored?** Use the named overload:
  `Configure<HttpResilienceOptions>("Orders", ...)`. A `Configure` registered before `AddHttpResilienceHandler`
  runs before binding, so configuration overrides it; use `PostConfigure` or the registration's `configure`
  parameter.
- **Changed configuration, no effect?** Restart. There is no reload.

## Retries

### Not retrying

- The method is not GET, HEAD, OPTIONS or TRACE (matched exactly: `get` is unknown). Opt in with
  `Retry:RetryableMethods`.
- A `RetryableMethods` list omits the method; check the **root** too (an unstated client list inherits it).
- `Retry:Enabled` is `false`.
- The outcome is not transient. Retried: 408, 429, 5xx, `HttpRequestException`, attempt timeouts.
- The circuit is open.

### Retrying a POST

Startup event 10 names the client and the key that allows it. No event 10? See event 12 (a nested platform
handler), then application code.

### A retried POST fails with `HttpRequestException`, yet the origin received it

The content was not replayable: a non-seekable `StreamContent` is consumed by the first attempt, and the retry
cannot read it again (inner `InvalidOperationException`: the stream was already consumed). The origin received
the request once; a caller that retries on this exception sends it again. Call `LoadIntoBufferAsync()` first.

### A retry waited much longer than `BaseDelay`

`Retry-After` replaces the backoff when it is at most `Retry:MaxDelay` (default 5 s; `UseRetryAfterHeader`,
default `true`). The request holds its limiter slot while it waits. Lower `MaxDelay`, or turn the header off for
an untrusted origin.

### A 429 or 503 came back without being retried

The origin's `Retry-After` was longer than `Retry:MaxDelay` (default 5 s), which ends the retries and returns its
response at once. Raise `MaxDelay`, and `Timeout:Total` with it, if the caller should wait that long.

### The origin sees 3x or more the requests the retry count explains

Look for event 12: `AddStandardResilienceHandler` registered after this package's handler nests a second
pipeline (9 origin calls for one failing GET). Use `AddResilienceHandler` instead. Look for event 20: a Polly v7
handler nests too (12 calls for a v7 retry of three). .NET Aspire's handler from `ConfigureHttpClientDefaults` is
replaced (event 17), and one registered on the client before this package's call fails client creation.

### The origin sees one request where retries were expected

Look for event 16 (Warning): a registration after `AddHttpResilienceHandler` removed this package's handlers, so
the client has no retries, timeouts or circuit breaker. The usual cause is `RemoveAllResilienceHandlers()` called
after this package's method; remove it, since this package already replaces the defaults' handlers. The check
tracks this package's own handlers, so it fires even if a handler of yours took their place.

## Hedging

- **`InvalidOperationException: A hedged client cannot send a request to ...`**: the host is not in
  `PipelineSelection:Authorities`. Add it and redeploy.
- **A slow POST was duplicated**: `Hedging:DisableForUnsafeHttpMethods` is `false` for that client (event 10).
  The endpoint must then deduplicate *simultaneous* requests.

## Limiters

### `RateLimiterRejectedException` with no rate limiter configured

A concurrency limit always exists: `ConcurrencyLimiter:Backstop` (1,000, no queue), per pipeline. The
exception names a rate limiter because of the strategy type. Event 7 names the client and key. Raise the
backstop only if the concurrency is legitimate; usually the dependency slowed down.

With N listed authorities under `ByAuthority` (no rate limiter), or on a hedged client, the backstop applies per
authority, so the client can have up to `(N + 1) × Backstop` in flight (N on a hedged client).

### `RateLimiterRejectedException` from the rate limiter

Event 8. A permit covers a request and all its retries, so the budget really ran out: compare
`replicas × clients × PermitLimit` with the downstream quota.

## Timeouts

### Request took longer than `Timeout:Total`

- Limiter queue wait is outside it; reduce `QueueLimit`.
- It stops at response headers; the body is bounded only by `Timeout:Client`.

### `TaskCanceledException`

With an inner `TimeoutException`: `Timeout:Client` fired (queue wait or body). Without: the caller cancelled.
Pipeline timeouts are `TimeoutRejectedException`.
`HttpResilienceFailure.Classify` tells them apart ([Handling failures](RECIPES.md#handling-failures)).

### `HttpClient.Timeout` set in code fails client creation

```text
HTTP client 'Orders' sets HttpClient.Timeout to 00:00:02 in code, but HttpResilience resolves it
to 00:00:50. Set Timeout:Client in configuration instead ...
```

Move the value to `Timeout:Client`. Any differing value fails, including 100 s; to keep 100 s set
`Timeout:Client: "00:01:40"`.

### Times out far below `Timeout:Total`, no message from this package

`The request was canceled due to the configured HttpClient.Timeout of N seconds elapsing`, with N not your
`Timeout:Client`: a typed client's constructor set `client.Timeout`. It runs after the factory and cannot be
detected. Remove it.

## Connections

- **Low throughput**: `Connection:MaxConnectionsPerServer` queues inside the pool, invisible to the pipeline.
- **Connections cycle faster than `PooledConnectionLifetime`**: `PooledConnectionIdleTimeout` (1 min), the
  server closing them, or your own `PostConfigure<HttpClientFactoryOptions>` registered after this package's.
- **DNS never refreshes**: `Connection:Enabled` is off for that client (the package then sets neither the pool
  settings nor the handler lifetime), or another `IHttpMessageHandlerBuilderFilter` registered **before** this
  package's overrides the handler (it runs last).

## Telemetry

- **Nothing at all**: the package exports nothing itself. Wire an SDK ([README.md](../README.md#telemetry)).
- **Metrics missing**: register both meters (`"Polly"`, `"HttpResilience.NET"`). `AddHttpResilienceTelemetry()`
  adds a tag, not a meter. With `OpenTelemetry.NET` 2.7.0+ both are registered; earlier versions need them in
  `OpenTelemetryOptions:Meters`. Instrument names are in [OPERATIONS.md](OPERATIONS.md#metrics).
- **No breaker series**: the gauges are published when the first handler chain is built, then every breaker of
  every enabled client is reported (as `closed` until it changes). No series means no client has been created
  yet, the client is disabled, or the meter is not registered.
- **No limiter series**: a limiter's series appears once it exists (the limiter-slot backstop on its first
  request); `rate` and `concurrency` only when enabled.
- **Retries invisible in traces**: add `AddHttpClientInstrumentation()` to the **tracing** builder; each
  attempt is a `System.Net.Http` span.
- **`error.type` has status codes but no exceptions**: call `AddHttpResilienceTelemetry()`.
