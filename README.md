# HttpResilience.NET

Standard outbound HTTP resilience for .NET services: one validated configuration schema, safe defaults and a
fixed pipeline shape, on top of [`Microsoft.Extensions.Http.Resilience`](https://learn.microsoft.com/dotnet/core/resilience/http-resilience)
and [Polly](https://www.pollydocs.org/).

The package **configures** the platform's handlers. Retry, timeouts, circuit breaking, rate limiting and
connection pooling are Microsoft's and Polly's; this package supplies validated values for them.

- **Validated at startup.** Messages name the key, the value, what was expected and why.
- **Safe by default.** Only GET, HEAD, OPTIONS and TRACE are retried or hedged. Anything else needs a
  per-client opt-in, and every opted-in client logs a startup Warning.
- **Fixed pipeline order.** You set values, not strategy order.
- **Bounded telemetry.** No metric tag comes from request data.
- **Guards where consumers collide.** A second registration on a client throws; a conflicting
  `HttpClient.Timeout` fails client creation; an extra resilience handler is reported.

## Install

```bash
dotnet add package HttpResilience.NET --version 3.1.0
```

Published to the internal `baps-apps` GitHub Packages feed only ([Distribution](#distribution-and-license)).
Upgrading from 2.x: see `3.0.0` in [CHANGELOG.md](https://github.com/baps-apps/http-resilience-net/blob/main/CHANGELOG.md).
Moving from `AddStandardResilienceHandler` or `AddStandardHedgingHandler`: **the defaults differ** (20 s total
instead of 30 s, 2 retries instead of 3, a 5 s cap on retry waits, POST never retried or hedged). The
[side-by-side table](https://github.com/baps-apps/http-resilience-net/blob/main/docs/CONFIGURATION.md#differences-from-addstandardresiliencehandler)
lists every difference and the values that keep the platform's timing.

## Quick start

```csharp
builder.Services.AddHttpResilience(builder.Configuration);   // once, anywhere

builder.Services.AddHttpClient("Orders").AddHttpResilienceHandler();
```

Every value has a default and the `HttpResilience` section is optional. To change values, add it:

```json
{
  "HttpResilience": {
    "Timeout": { "Total": "00:00:20", "Attempt": "00:00:05" },
    "Retry": { "MaxRetries": 2 },
    "Clients": {
      "Orders": { "Timeout": { "Total": "00:00:10", "Attempt": "00:00:03" } }
    }
  }
}
```

- **Calling `AddHttpResilienceHandler()` is the opt-in.** `HttpResilience:Clients:{name}:Enabled: false`
  turns one client off; the root `HttpResilience:Enabled: false` turns every client off (a kill switch). Each
  disabled client logs a startup Warning naming the key.
- **Order does not matter.** Configuration is read when a client is first created, so clients may be
  registered before `AddHttpResilience`, and configuration sources added later are honoured.
- **Changes need a restart.** There is no reload.
- **Root values can be set in code**, alone or on top of configuration:

  ```csharp
  builder.Services.AddHttpResilience(builder.Configuration, options => options.Retry.MaxRetries = 3);
  ```

  Applied after the root section and before each client's section. It cannot switch off a safe-method guard;
  the kill switch is configuration-only.

Every key, default and rule: **[docs/CONFIGURATION.md](https://github.com/baps-apps/http-resilience-net/blob/main/docs/CONFIGURATION.md)**.

## What fails startup, and how to opt out

A mistake that would leave a client on defaults it was not meant to have fails the deployment. Read this before
sharing one configuration file across services.

| Fails startup | Why | Opt out |
| --- | --- | --- |
| A key the schema does not have (`Retry:Enable`) | The binder ignores it, so the default stays in force | `HttpResilience:SchemaVersion` newer than the package: a Warning instead (event 18). `HttpResilience:AllowUnknownKeys: true`: no check |
| A `Clients:{name}` section no registered client reads | A misnamed section (`OrdersApi` for `IOrdersApi`) configures nothing | `HttpResilience:AllowUnusedClientSections: true` |
| A client whose handler chain cannot be built | Fails at host start, not at the first request | `HttpResilience:ValidateClientsOnStart: false` |
| `HttpClient.Timeout` set in `ConfigureHttpClient` to another value | It would cut the pipeline short | None: set `Timeout:Client` instead ([RECIPES.md](https://github.com/baps-apps/http-resilience-net/blob/main/docs/RECIPES.md#setting-httpclienttimeout)) |
| A value out of range, or a rule broken (`Timeout:Attempt` ≥ `Total`) | The platform would reject it later, or it would do nothing | None: the message names the key, the value and the fix |

**One file shared by many services** (an organization-wide `appsettings.json`, a config server, Helm values):
each service registers only some clients, and services upgrade at different times. State the package version
the file was written for, so a service on an older 3.x logs the keys it does not know (Warning event 18) instead
of failing, while a typo in a file written for its own version still fails; and allow unused client sections:

```json
{ "HttpResilience": { "SchemaVersion": "3.0", "AllowUnusedClientSections": true } }
```

A `SchemaVersion` with a newer major (`4.0` read by 3.x) fails startup: a major may rename keys.

The three switches are root-only. Set in code, they win over configuration whatever the registration order:
`services.Configure<HttpResilienceStartupOptions>(o => o.AllowUnknownKeys = true)`. See
[RECIPES.md](https://github.com/baps-apps/http-resilience-net/blob/main/docs/RECIPES.md#one-configuration-file-shared-by-many-services).

## Per-client configuration

A client reads `HttpResilience:Clients:{name}` on top of the root, so it states only what it changes. Lists
(`Retry:RetryableMethods`, `PipelineSelection:Authorities`) **replace** the root's list; they are not merged.

**A typed client's name comes from `IHttpClientFactory`**; for the two-generic overload it is the interface:

| Registration | Section it reads |
| --- | --- |
| `AddHttpClient("Orders")` | `Clients:Orders` |
| `AddHttpClient<OrdersApi>()` | `Clients:OrdersApi` |
| `AddHttpClient<IOrdersApi, OrdersApi>()` | `Clients:`**`IOrdersApi`** |

Pass the section name to avoid surprises: `.AddHttpResilienceHandler("Orders")`. A client with no section of
its own reads the root values.

- **Register once per client.** A second `AddHttpResilienceHandler` or `AddHttpResilienceHedgingHandler` on
  the same client throws (nested pipelines multiply retries).
- **Not inside `ConfigureHttpClientDefaults`**: it has no client name, so the call throws. .NET Aspire
  ServiceDefaults need no change: their standard handler is replaced on each client this package configures
  ([RECIPES.md](https://github.com/baps-apps/http-resilience-net/blob/main/docs/RECIPES.md#a-net-aspire-service-servicedefaults)).

## Retrying a POST (or any non-safe method)

Only when the endpoint deduplicates on an idempotency key, and only for that client:

```json
{ "HttpResilience": { "Clients": { "Payments": { "Retry": { "RetryableMethods": [ "GET", "POST" ] } } } } }
```

- The list **replaces** the default guard: only the listed methods are retried. It is an allow-list, so
  unfamiliar methods (`MOVE`, `PURGE`) are never retried unless listed.
- The root cannot widen this for everyone: a root list may name only safe methods, and neither
  `DisableForUnsafeHttpMethods` flag may be `false` at the root.
- An empty client list (`"RetryableMethods": []`) restores the safe-method guard under an inherited list.
- **Content must be replayable.** A `StreamContent` over a non-seekable stream is delivered once, then the
  retry throws `HttpRequestException` although the origin already received the request, so a caller that
  retries on failure sends it again. Call `await content.LoadIntoBufferAsync()` first.

## Hedging

Hedging sends a second copy of a slow request and takes the first answer. It multiplies traffic, so it is
chosen in code, instead of (not with) `AddHttpResilienceHandler`:

```csharp
builder.Services.AddHttpClient("Search").AddHttpResilienceHedgingHandler();
```

```json
{
  "HttpResilience": {
    "Clients": {
      "Search": {
        "Hedging": { "Delay": "00:00:00.300", "MaxHedgedAttempts": 1 },
        "PipelineSelection": { "Authorities": [ "https://search.internal" ] }
      }
    }
  }
}
```

- **`Authorities` is required.** The platform keeps per-authority state for the life of the process, so a
  request to an unlisted authority fails with `InvalidOperationException` before it is sent (not
  `HttpRequestException`, which retry policies treat as transient).
- **Only safe methods are hedged by default**, including hedges started by the delay timer. Hedged attempts run
  at the same time, so an idempotency key may not prevent duplicates.
- **No retry.** `Retry` keys in a hedged client's own section fail startup.
- **Redirects are not followed** by default: a redirect would bypass the allow-list. Opt in with
  `Connection:AllowAutoRedirect: true`. The runtime strips `Authorization` on a redirect but re-sends custom
  headers such as API keys.

## The pipeline

Fixed, outermost first:

```text
Standard (AddHttpResilienceHandler)            Hedged (AddHttpResilienceHedgingHandler)

ConcurrencyLimiter      (optional)             Authority allow-list
  ConcurrencyBackstop   (see below)              ConcurrencyLimiter   (optional)
    Limiter             (always present)           RateLimiter        (optional)
      Total timeout                                  Total timeout
        Retry                                          Hedging
          Circuit breaker                                per authority: backstop limiter
            Attempt timeout                                Circuit breaker
              SocketsHttpHandler                             Attempt timeout
```

- **A concurrency bound always exists.** The platform's limiter slot holds the rate limiter if enabled,
  otherwise `ConcurrencyLimiter:Backstop` (1,000, no queue). On a standard client a rate limiter displaces the
  backstop into its own handler, unless the client's own `ConcurrencyLimiter` is enabled (validated at or below
  the backstop, so it is the bound). Above the bound, requests fail with `RateLimiterRejectedException`.
- **One limiter slot or permit covers a whole logical request**, including its retries, so queue wait is
  **outside** `Timeout:Total`.

To add a strategy the schema does not cover, use the platform's `AddResilienceHandler`. **Not**
`AddStandardResilienceHandler`, which nests a second pipeline:

```csharp
// Composes: 3 origin calls for a failing GET.
.AddHttpResilienceHandler().AddResilienceHandler("extra", p => p.AddTimeout(TimeSpan.FromSeconds(9)));

// Nests: 9 origin calls, total timeout applied twice. Nothing throws.
.AddHttpResilienceHandler().AddStandardResilienceHandler();
```

The two cannot be told apart through public API, so the second is only reported: an Information log (event
12) on any client with more resilience handlers than this package added.

## Timeouts

```text
CancellationToken (caller)     always wins; never a circuit-breaker failure
  Timeout:Client               HttpClient.Timeout: queue wait + attempts + response BODY
    Timeout:Total              all attempts and backoff, up to response headers
      Timeout:Attempt          one attempt, up to response headers
        Connection:ConnectTimeout   TCP + TLS (only with Connection:Enabled)
```

- **`Total` and `Attempt` stop at response headers.** Only `Timeout:Client` bounds the body; it defaults to
  `Timeout:Total` + 30 s. For streaming, use `HttpCompletionOption.ResponseHeadersRead` and your own read
  deadline.
- **Set `HttpClient.Timeout` with `Timeout:Client`, not in code.** A different value set via
  `ConfigureHttpClient` fails client creation. **A typed client that sets `client.Timeout` in its constructor
  cannot be detected** and silently truncates the pipeline: search your constructors.
- A retry schedule longer than `Timeout:Total` is allowed (a cap is valid) and logs a startup Warning (event 14).

## Handling failures

`HttpResilienceFailure.Classify(exception)` names why a request failed (`Timeout`, `CircuitOpen`,
`LimiterRejected`, `RequestFailed`, `Canceled` or `None`), reading the whole inner-exception chain, so callers
map failures to their own responses without referencing Polly. `HttpResilienceFailure.TryGetRetryAfter` gives a
rejecting rate limiter's wait for a `Retry-After` header. See
[Handling failures](https://github.com/baps-apps/http-resilience-net/blob/main/docs/RECIPES.md#handling-failures).

## Connection settings

Off by default. With `Connection:Enabled: true`, the client's primary `SocketsHttpHandler` gets the pool
settings and the factory's handler rotation is turned off (`PooledConnectionLifetime` bounds connection age and
DNS staleness instead; a finite `SetHandlerLifetime` on the client fails client creation). Settings are applied
after every other registration, so order does not matter:

- A `SocketsHttpHandler` you supply is **kept** (certificates, proxy, callbacks survive). If it sets a
  connection setting the schema also sets (connect and pool timeouts, `EnableMultipleHttp2Connections`, a stated
  `MaxConnectionsPerServer`) to a different value, client creation **fails**, naming the key. A value left at the
  runtime default is configured.
- An `HttpClientHandler` with nothing set is replaced with a new `SocketsHttpHandler`. One carrying any setting
  (client certificate, certificate callback, proxy, credentials, cookies) **fails client creation**, naming the
  settings: move them to a `SocketsHttpHandler`.
- Any other primary handler type fails client creation.

## Everything is per process

- **The rate limiter is process-local and cannot enforce a cluster-wide quota.** Each replica and each named
  client has its own budget: the fleet rate is `replicas × clients × PermitLimit`. Divide the quota, or
  enforce it at a gateway.
- **Retries multiply outbound load in every replica independently.** At the default `MaxRetries: 2`, a failing
  dependency receives up to 3x your request rate from every pod. Read
  [OPERATIONS.md](https://github.com/baps-apps/http-resilience-net/blob/main/docs/OPERATIONS.md#retry-amplification) before raising it.
- **The circuit breaker is per replica and counts attempts, not requests.** Each replica must see
  `MinimumThroughput` (20) attempts within `SamplingDuration` (30 s) before it can open, and opens when half of
  them failed; a quieter client needs a lower value. Startup event 11 states the rate each client needs.

## Telemetry

The package depends on no OpenTelemetry package: it publishes on a `Meter` and `ILogger` and creates no spans
(`System.Net.Http` already emits one span per attempt). Wire the SDK in your service:

```csharp
builder.Services.AddHttpResilienceTelemetry();   // adds error.type for exceptions; registers no meter

builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics
        .AddMeter(HttpResilienceTelemetryExtensions.PollyMeterName)  // "Polly": retries, breaker events
        .AddMeter(HttpResilienceTelemetryExtensions.MeterName)       // "HttpResilience.NET": breaker state, limiters
        .AddHttpClientInstrumentation()
        .AddOtlpExporter())
    .WithTracing(tracing => tracing.AddHttpClientInstrumentation().AddOtlpExporter());
```

- **A missing `AddMeter` fails silently** (an empty dashboard). `AddHttpResilienceTelemetry()` adds a tag, not
  a meter.
- With the `OpenTelemetry.NET` package 2.7.0 or later, `AddObservability(...)` registers both meters; still
  call `AddHttpResilienceTelemetry()`.
- Logs use `HttpResilience.NET.*` categories. Startup notices (a disabled client, unsafe methods repeatable, a
  per-client settings summary) need a generic host.

Instruments, tags, log events and alerts: **[docs/OPERATIONS.md](https://github.com/baps-apps/http-resilience-net/blob/main/docs/OPERATIONS.md)**.

## Health checks

The package registers none. **Never gate a liveness or readiness probe on circuit breaker state**: an open
circuit means a dependency is unhealthy, not this process, and restarting pods cuts capacity during the
outage. Alert on the `http_resilience.circuit_breaker.state` gauge instead.

## Requirements

- `net10.0`. Trimming and Native AOT are supported (source-generated configuration binding).
- A generic host. Startup validation, the startup client check and the startup notices run when the host
  starts; a bare `ServiceProvider` gets none of them (validation then happens at first client creation).

## Distribution and license

Proprietary and internal: Copyright (c) BAPS, all rights reserved; no open-source license. Published to
GitHub Packages (`https://nuget.pkg.github.com/baps-apps/index.json`), never to nuget.org. Consumers need that
source and a GitHub PAT with `read:packages` ([scripts/README.md](https://github.com/baps-apps/http-resilience-net/blob/main/scripts/README.md#restoring-from-baps-apps-packages)).
Map `HttpResilience.NET` to that source with `packageSourceMapping` so a public package cannot shadow it.

## Documentation

| Doc | For |
| --- | --- |
| [CONFIGURATION.md](https://github.com/baps-apps/http-resilience-net/blob/main/docs/CONFIGURATION.md) | Every key, default and validation rule |
| [RECIPES.md](https://github.com/baps-apps/http-resilience-net/blob/main/docs/RECIPES.md) | Common setups |
| [TROUBLESHOOTING.md](https://github.com/baps-apps/http-resilience-net/blob/main/docs/TROUBLESHOOTING.md) | Errors and surprises, by symptom |
| [OPERATIONS.md](https://github.com/baps-apps/http-resilience-net/blob/main/docs/OPERATIONS.md) | Load arithmetic, metrics, logs, alerts |
| [RUNBOOK.md](https://github.com/baps-apps/http-resilience-net/blob/main/docs/RUNBOOK.md) | What to do when an alert fires |
| [PRODUCTION-CHECKLIST.md](https://github.com/baps-apps/http-resilience-net/blob/main/docs/PRODUCTION-CHECKLIST.md) | Before go-live |
| [ARCHITECTURE.md](https://github.com/baps-apps/http-resilience-net/blob/main/docs/ARCHITECTURE.md) | How the package composes the platform |
| [SECURITY-GOVERNANCE.md](https://github.com/baps-apps/http-resilience-net/blob/main/docs/SECURITY-GOVERNANCE.md) | Security posture and change control |
| [VERSIONING.md](https://github.com/baps-apps/http-resilience-net/blob/main/docs/VERSIONING.md) | What counts as breaking |
| [benchmarks/](https://github.com/baps-apps/http-resilience-net/blob/main/docs/benchmarks/README.md) | Measured overhead |
| [CONTRIBUTING.md](https://github.com/baps-apps/http-resilience-net/blob/main/CONTRIBUTING.md) | Building, testing, releasing |
