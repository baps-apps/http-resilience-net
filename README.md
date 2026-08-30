# HttpResilience.NET

Standardized outbound HTTP resilience for .NET services: one validated configuration schema, safe defaults, and a fixed pipeline shape, over [`Microsoft.Extensions.Http.Resilience`](https://learn.microsoft.com/dotnet/core/resilience/http-resilience) and [Polly](https://www.pollydocs.org/).

The package **configures** the platform's resilience handlers. It does not implement retry, timeouts, circuit breaking, rate limiting or connection pooling itself, and it does not stand between you and a future improvement to any of them.

## What it gives you

- **A validated schema.** Every rule is checked at startup, for every client, with messages that name the property, the value, the expected range and the reason.
- **Safe defaults, on by default.** Only GET, HEAD, OPTIONS and TRACE are ever repeated. Every other method — POST, PUT, PATCH, DELETE, and anything non-standard — is retried or hedged only if you say so, per client, in writing.
- **A fixed pipeline shape.** Ordering is not configurable, because ordering is where resilience pipelines go wrong.
- **Connection standardization.** `SocketsHttpHandler` settings applied consistently, and applied after every other registration so nothing can take them away.
- **Bounded telemetry.** Nothing derived from a request URI ever becomes a metric tag, and a hedged client's destinations are allow-listed so the platform's own per-authority series stay bounded too.
- **A dependency health check.** Circuit breaker state, reported as **Degraded and never Unhealthy** — which is what stops a dependency outage restarting a healthy pod.
- **Guards at the consumer boundary.** A second registration on the same client fails; a conflicting `HttpClient.Timeout` fails; a client carrying a handler this package did not add is reported. What each guard cannot see is stated where it is documented rather than implied to be airtight.

## Quick start

```json
{
  "HttpResilience": {
    "Enabled": true,
    "Timeout": { "Total": "00:00:20", "Attempt": "00:00:05" },
    "Retry": { "MaxRetries": 2, "BaseDelay": "00:00:00.500" }
  }
}
```

```csharp
using Microsoft.Extensions.DependencyInjection;

builder.Services.AddHttpResilience(builder.Configuration);

builder.Services.AddHttpClient<IOrdersApi, OrdersApi>()
    .AddHttpResilience("Orders");
```

That is the whole minimum. Every value except `Enabled` shown above is already the default, so a service that states nothing else gets the standard pipeline.

**`Enabled` is opt-in and defaults to `false`,** so adding this package never changes how an existing client behaves until someone says so. Because a forgotten key produces exactly the same state as a deliberate opt-out, a client registered with resilience switched off logs a **Warning** naming the key, at startup, before the service accepts traffic.

`AddHttpResilience(configuration)` is idempotent, so a shared platform extension and the application using it may both call it. Calling it with a *different* section fails at startup, because clients registered on either side of the second call would read different configuration.

Every key, default and validation rule: **[docs/CONFIGURATION.md](docs/CONFIGURATION.md)**.

## Per-client configuration

Clients name a section under `HttpResilience:Clients`. Values not stated are inherited from the root, so a client states only what it changes.

```json
{
  "HttpResilience": {
    "Enabled": true,
    "Timeout": { "Total": "00:00:20", "Attempt": "00:00:05" },

    "Clients": {
      "Orders":  { "Timeout": { "Total": "00:00:10", "Attempt": "00:00:03" } },
      "Reports": { "Timeout": { "Total": "00:02:00", "Attempt": "00:00:30" } }
    }
  }
}
```

```csharp
builder.Services.AddHttpClient("Orders").AddHttpResilience();
builder.Services.AddHttpClient("Reports").AddHttpResilience();
```

A client reads the section named after it, so the name is written once. Pass a different name to read someone else's section, or `string.Empty` to use only the root values.

**The name is `IHttpClientBuilder.Name`, which for a typed client comes from `IHttpClientFactory` and not from this schema.** For the two-generic overload that is `TClient` — the *interface*:

| Registration | Section it reads |
| --- | --- |
| `AddHttpClient("Orders")` | `Clients:Orders` |
| `AddHttpClient<OrdersApi>()` | `Clients:OrdersApi` |
| `AddHttpClient<IOrdersApi, OrdersApi>()` | `Clients:`**`IOrdersApi`** |

A leading `I` in a configuration file is nobody's first guess, so name the section explicitly when it matters:

```csharp
builder.Services.AddHttpClient<IOrdersApi, OrdersApi>().AddHttpResilience("Orders");
```

**A section under `Clients` that no client reads fails startup.** Inert configuration reads exactly like configuration that is in force — the client runs on root defaults and nothing says so. The message names the section, lists the sections that *are* read, and mentions the typed-client rule above, because that is the usual cause. If one configuration file is deliberately shared by services registering different subsets of clients, set `HttpResilience:AllowUnusedClientSections` to `true`.

**A client section replaces a list it states, rather than adding to the root's.** Both of the schema's lists — `Retry:RetryableMethods` and `PipelineSelection:Authorities` — are allow-lists, so widening is the unsafe direction, and the binder's default behavior of adding to a collection would let a client widen an inherited list but never narrow one. A client that states no list of its own still inherits the root's, so a fleet-wide allow-list is still expressible in one place.

Client names live under their own `Clients` child, so a client may be called `Retry` or `Timeout` without colliding with the schema.

Resilience can only be added once per client. A second `AddHttpResilience` on the same name fails at startup rather than nesting two pipelines.

## Retrying a non-idempotent request

Only the methods RFC 9110 defines as safe — GET, HEAD, OPTIONS, TRACE — are retried. Everything else is left alone, including methods this package has never heard of: a WebDAV `MOVE` or `PROPPATCH`, a cache `PURGE`, any `new HttpMethod("...")`. This is an **allow-list**, not a deny-list of the five familiar mutating verbs — a deny-list retries whatever it has not been told about.

If an endpoint deduplicates on an idempotency key, opt in explicitly:

```json
{
  "HttpResilience": {
    "Clients": {
      "Payments": {
        "Retry": { "RetryableMethods": [ "GET", "POST" ] }
      }
    }
  }
}
```

The allow-list replaces the default guard entirely: only the methods you name are retried. It is also the only way to retry a non-standard method.

Four rules make "per client" real rather than advisory:

- **Every client that can repeat a mutating request logs one Warning at startup** (event 10), naming the client, the methods and the key that allowed it — by either mechanism, the allow-list included. "Which of our clients can duplicate a mutation?" is an incident question that should be answerable from logs rather than by grepping configuration across repositories.
- **Neither `DisableForUnsafeHttpMethods` flag may be `false` at the root.** One key there decides that every standard client in the process — including clients registered later that state nothing — may deliver a mutating request to its origin more than once. Whether that is safe is a property of one endpoint's idempotency handling, and there is no fleet-wide answer to it. Startup fails and names the client section to move it to.
- **`Retry:RetryableMethods` may narrow at the root, but not widen.** A root list of `["GET"]` restricts every client and is safer than the default. Naming an *unsafe* method there reaches every standard client by exactly the route the flag is refused for, so it is refused too.
- **Stating the flag beside a list in force is refused, in either direction.** An allow-list wins outright in the pipeline, so a `DisableForUnsafeHttpMethods` beside one is bound and never read. The direction that reads as harmless is the one that matters: a client writing `true` beside a list is writing the *protective* statement and having it discarded. Measured before this was refused — a clean startup and three POST bodies at the origin.

**To narrow a client back to safe methods under an inherited list, give it an empty list.** `"RetryableMethods": []` means *no allow-list*, so the client returns to the default guard. It does not disable retries; `Retry:Enabled` is the off switch.

**A retried request must carry re-playable content.** A retry re-sends the same `HttpRequestMessage`, so `StringContent`, `ByteArrayContent` and `JsonContent` replay correctly and a single-pass stream does not. Measured: a `StreamContent` over a non-seekable stream retried three times delivers the body once and then **an empty body twice**, with no exception thrown. Buffer it first with `await content.LoadIntoBufferAsync()`, or build fresh content per attempt.

## Hedging

Hedging races concurrent copies of a request and takes the first success. It multiplies outbound traffic, so it is selected in code, never by a configuration value:

```csharp
builder.Services.AddHttpClient("Search").AddHedgedHttpResilience();
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

Mutating methods are excluded by default, on the same safe-method allow-list as retries. Hedged attempts are *simultaneous*, so unlike retries they give an origin's idempotency key no serialization to rely on.

The exclusion covers **both** ways a hedged attempt starts. Polly begins one either because an attempt completed and failed, or because the hedging delay elapsed while every attempt was still running — and only the first consults an outcome predicate. The second is the case hedging exists for, a slow primary, so a guard written only as `ShouldHandle` would let a slow POST reach the origin `1 + MaxHedgedAttempts` times with its body while every test using a fast origin passed. The timer path is closed separately, by suppressing the attempt itself.

**A hedged client must list the authorities it may call.** The hedging handler keeps a circuit breaker, a concurrency limiter and a metric series **per authority** for the life of the process, so an unbounded set of destinations is a memory-exhaustion path. Requests to an unlisted authority are rejected before they reach the pipeline.

The hedging pipeline has no retry strategy, so a hedged client that states `Retry:*` keys of its own fails at startup rather than binding configuration nothing reads. Root-level retry configuration is still inherited by every standard client and is unaffected.

```text
AuthorityAllowList                  rejects an unlisted destination before anything is allocated
  └─ ConcurrencyLimiter (optional)  one slot per logical request
       └─ RateLimiter   (optional)  one permit per logical request
            └─ Total timeout
                 └─ Hedging
                      └─ Endpoint concurrency limiter   1,000 concurrent, platform default
                           └─ Endpoint circuit breaker  per authority
                                └─ Attempt timeout
                                     └─ SocketsHttpHandler
```

## The pipeline

Fixed, outermost to innermost:

```text
ConcurrencyLimiter   (optional)  one slot per logical request
  └─ Limiter         ALWAYS PRESENT — the standard handler has one limiter slot and it is never empty
       │             RateLimiter:Enabled = false → concurrency backstop, 1,000 permits, no queue
       │             RateLimiter:Enabled = true  → your rate limiter, and the backstop moves outside it
       └─ Total timeout
            └─ Retry
                 └─ Circuit breaker
                      └─ Attempt timeout
                           └─ SocketsHttpHandler
```

A concurrency slot and a rate-limit permit each cover a whole logical request, including its retries — a retrying request can never be rejected by its own budget. The cost is that queue wait is **outside** `Timeout:Total`.

**The limiter slot is never empty.** Microsoft's standard handler always carries one, and its default is a concurrency limiter of 1,000 with no queue. Left implicit that is a scaling cliff nobody can see: above 1,000 concurrent requests the client throws `RateLimiterRejectedException`, naming a rate limiter you never enabled. It is surfaced as `ConcurrencyLimiter:Backstop` so the number can be read, alerted on and changed.

### Adding something outside this shape

You already have the platform API for it — but the two calls that look interchangeable are not:

```csharp
// Composes. Origin sees 3 calls.
builder.Services.AddHttpClient("Legacy").AddHttpResilience()
    .AddResilienceHandler("legacy-quirk", p => p.AddTimeout(TimeSpan.FromSeconds(9)));

// Nests. Origin sees 9 calls. Nothing throws.
builder.Services.AddHttpClient("Legacy").AddHttpResilience()
    .AddStandardResilienceHandler();
```

`AddResilienceHandler` adds one more strategy outside this pipeline and the origin call count does not change. `AddStandardResilienceHandler` on a client that already has `AddHttpResilience` **nests a second pipeline**, and nested pipelines multiply rather than add: one GET makes **nine** origin calls — three configured attempts, each retried three times by the outer pipeline — and the total timeout is applied twice.

A second `AddHttpResilience` fails at startup. **This does not**, and the honest reason is that the package cannot tell the two apart: both add a `ResilienceHandler`, and the only difference is a pipeline name on an internal field that reading would need the reflection this package's trim and Native AOT support rules out. Every other observable difference was measured and there is none.

So instead the package **counts**. A client carrying more resilience handlers than this package added logs one **Information** line (event 12) at construction, naming both possibilities. Information rather than Warning because the state is frequently correct — it is what the composing pattern above produces — and a line that cries wolf on recommended code is a line operators filter out.

## Timeouts

```text
CancellationToken (caller / request abort)   always wins, never a breaker failure
  └─ Timeout:Client     HttpClient.Timeout: queue wait + attempts + RESPONSE BODY transfer
       └─ Timeout:Total      all attempts plus backoff, from admission onwards
            └─ Timeout:Attempt      one HTTP attempt, up to response HEADERS
                 └─ Connection:ConnectTimeout   TCP + TLS only, strictly less than Attempt
```

**`Timeout:Total` stops applying when response headers arrive.** Every strategy lives in the handler chain, and the chain returns as soon as `SocketsHttpHandler` has the headers. Under the default `HttpCompletionOption.ResponseContentRead` the body is then buffered by `HttpClient` itself, after the chain, where no resilience strategy can see it.

`Microsoft.Extensions.Http.Resilience` sets `HttpClient.Timeout` to infinite for exactly this reason in reverse — so that its total request timeout, rather than the 100-second default, bounds the attempts. That leaves a trickled response body bounded by nothing at all: an origin that answers headers promptly and then stalls holds a connection, a buffer and an inbound request for as long as it likes, while the pipeline's telemetry reports a fast successful attempt.

So this package puts a finite bound back. `Timeout:Client` defaults to `Timeout:Total` plus 30 seconds, is validated to be strictly greater than `Timeout:Total`, and wins over every other assignment. The allowance covers limiter queue wait and the response body only, since `Timeout:Total` already covers every attempt up to headers; it was a minute, which was three times the whole default attempt budget for body bytes alone. It is a backstop, not an SLO. For streaming, request `HttpCompletionOption.ResponseHeadersRead` and impose your own deadline on reading the stream.

**`Timeout:Client` is the only place the schema can validate that bound.** `ConfigureHttpClient` actions run in registration order and last wins, so a plain `ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(2))` truncates a 30-second pipeline with a bare `TaskCanceledException` — the exact condition validation refuses when the same value is written as `Timeout:Client`. It is therefore applied from a post-configure on `HttpClientFactoryOptions`, which runs after every `ConfigureHttpClient` registration rather than racing it. A conflicting assignment in code fails at client creation and names the key to use instead. Every other use of `ConfigureHttpClient` — a default request header, a base address — is untouched.

**One shape gets past that guard, and it is not preventable.** A **typed client's constructor** runs after `IHttpClientFactory` has finished building the client, so no options phase can observe it — `public OrdersApi(HttpClient client) { client.Timeout = TimeSpan.FromSeconds(1); }` truncates a 30-second pipeline to one second with the same bare `TaskCanceledException`, and nothing warns. Grep typed-client constructors for `Timeout`; the log will not tell you. Explained in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

Startup validation enforces that the retry schedule fits the total budget, with 1.5x headroom for jitter. **That headroom is a factor, not a bound**: Polly's jittered delay is drawn from a distribution, so a schedule that passes can still occasionally have its last retry truncated. What is rejected is a schedule that could not fit even with half again the nominal backoff. The caveat applies more sharply to `Retry-After`, which replaces the computed delay entirely — an origin naming a large value can spend the whole budget on one wait. Both are bounded by `Timeout:Total`, never unbounded.

## Connection settings are owned, not merely requested

`Connection:Enabled` configures the primary `SocketsHttpHandler` and sets the `IHttpClientFactory` handler lifetime to infinite, because `PooledConnectionLifetime` is what bounds connection age instead. Those two are only safe together.

`ConfigurePrimaryHttpMessageHandler` is last-wins across registrations; `SetHandlerLifetime` is not. A package that replaced the handler *at registration time* would leave a trap: any later registration — a client certificate, a proxy, a test stub — takes the handler away and leaves rotation disabled around a pool nothing gave a lifetime to. A handler a consumer constructed directly carries the runtime default of **infinite**, so nothing then recycles connections or re-resolves DNS for the life of the process, which behind a moving service IP is an outage no probe reports.

So the settings are applied from an `IHttpMessageHandlerBuilderFilter`, which runs after every `ConfigurePrimaryHttpMessageHandler` registration rather than racing it:

- a `SocketsHttpHandler` you supplied is **kept and configured**, so a client certificate, proxy or SSL callback survives. Configured means: `ConnectTimeout`, `PooledConnectionIdleTimeout`, `PooledConnectionLifetime` and `EnableMultipleHttp2Connections` are overwritten, because the schema always states them; `MaxConnectionsPerServer` only when it is set; and `AllowAutoRedirect` only when it is stated or resolves to `false`. **A redirect bound you set on your own handler is not reversed by switching this on** — it used to be, silently, which for a control the runtime re-sends custom credential headers across is a credential-disclosure path and not a preference. Tune any of the first four yourself and you should leave `Connection:Enabled` false instead;
- the factory's default `HttpClientHandler` is replaced;
- anything else fails at client creation with a message saying why, because there is nothing for `PooledConnectionLifetime` to bind to.

Registration order does not matter for `ConfigurePrimaryHttpMessageHandler`, which is what a consumer normally uses. `IHttpClientFactory` composes handler-builder filters in reverse, so another `IHttpMessageHandlerBuilderFilter` registered **before** this one still gets the last word. If you have one of those, or you want to own the pool yourself, set `Connection:Enabled` to `false` and set `PooledConnectionLifetime` on your own handler.

**Whether a client *can* be created is not an options value, so startup validation cannot see it.** The handler chain is built by `IHttpClientFactory` on the first `CreateClient`, which for a client on a rare code path is hours after the deploy. `AddHttpResilience` therefore registers an `IHostedService` that creates every client this package configured, once, while the host is starting. Only this package's clients, and nothing else in the container.

It is on by default. Turn it off with configuration rather than code, so it is reachable during an incident without a redeploy:

```jsonc
{ "HttpResilience": { "ValidateClientsOnStart": false } }
```

`ValidateHttpResilienceClientsOnStart()` is the equivalent call in code and is idempotent. Setting the key to `false` **while** calling that method fails at startup naming both: the call would win, and a key an operator reached for that silently does nothing is worse than no key.

This is an `IHostedService`, so it runs under a generic host and not under a bare `ServiceCollection` plus `BuildServiceProvider` — the same limitation `ValidateOnStart` has.

## Rate limiting is process-local

**The rate limiter cannot enforce a cluster-wide quota.** Each replica gets its own limiter, so the fleet-wide rate is `replicas × PermitLimit` per window. Ten pods configured for 100 requests per second permit 1,000 requests per second in aggregate.

**And there is a second multiplier.** The budget belongs to a *named client*, not to a downstream, so two clients calling the same host hold two independent budgets. That shape is ordinary — a typed client split by concern, or a shared library registering its own client against a host the application also calls — so the real fleet-wide rate is `replicas × clients × PermitLimit`. Count the clients that reach the host, not the hosts.

Size `PermitLimit` as `downstream quota ÷ (replicas × clients)`, or enforce the real quota at a gateway. The same applies to the circuit breaker: `MinimumThroughput` is observed per replica **and counted in attempts, not caller requests**, so a 20-replica deployment sends at least `20 × MinimumThroughput` failing attempts before the fleet stops.

See [docs/OPERATIONS.md](docs/OPERATIONS.md#retry-amplification) for the amplification arithmetic before raising `Retry:MaxRetries`.

## Per-authority pipelines

One client calling several hosts can isolate their **circuit breakers**. Rate and concurrency limits stay per client and shared across authorities: a limiter budget is a statement about this process's capacity, not about one host's health. The authorities must be listed, so the number of pipelines is fixed at deploy time:

```json
{
  "HttpResilience": {
    "Clients": {
      "Partner": {
        "PipelineSelection": {
          "Mode": "ByAuthority",
          "Authorities": [ "https://a.partner.example", "https://b.partner.example" ]
        }
      }
    }
  }
}
```

Anything not listed shares a single pipeline. Without the allow-list, a target host derived from request data — a tenant-configured webhook, a stored callback URL — would let each distinct authority permanently allocate a pipeline, a circuit breaker and a metric series, with nothing to evict them.

**A root-level `Authorities` list is inherited by every client, and that is deliberate.** It is how a fleet states one destination allow-list for its hedged clients, which require one. A standard client inheriting it under the default `Mode: None` is unaffected — the list is inert for it and no failure is raised. What *is* refused is a client that **states** a list of its own while its mode is `None`: a written statement nothing reads.

**Per-authority selection instantiates the whole pipeline per key, and the concurrency backstop with it — while the client has no rate limiter.** The rate limiter does not multiply: it is one instance per client, shared by every authority's pipeline. `ConcurrencyLimiter:Backstop` normally does, so a client with N listed authorities is bounded at `(N + 1) × Backstop` concurrent requests, counting the shared pipeline.

Enabling a rate limiter changes that number. The rate limiter takes the standard handler's limiter slot, so the backstop moves out into a handler of its own — one handler, outside the per-authority pipelines, and therefore **one limiter per client rather than one per authority**. The bound in that configuration is `1 × Backstop`. Size `Backstop` per authority when the client has no rate limiter and per client when it does. All three cases are pinned by `ConcurrencyBackstopTests`.

Hosts are matched on `Uri.IdnHost` with any trailing root label removed, so an internationalized authority matches whether the request spells it in Unicode or punycode, and `orders.internal` matches `orders.internal.`. Scheme and port must match exactly.

### A hedged client does not follow redirects

A 3xx is resolved inside `SocketsHttpHandler`, below every `DelegatingHandler`, so the allow-list above never sees the second hop — and an allow-list a redirect can step around is not an allow-list. `AddHedgedHttpResilience` therefore resolves `Connection:AllowAutoRedirect` to `false`, and does so even when `Connection:Enabled` is off, because a safety bound an unrelated connection-pool switch can disable is not a bound.

Two measured facts behind that choice:

| | Across a redirect |
| --- | --- |
| `Authorization` header | **stripped by the runtime**, same-origin or cross-origin |
| `X-Api-Key` and every other custom header | **re-sent verbatim**, including cross-origin |

Bearer tokens are safe; the custom header that most internal service-to-service auth actually uses is not. That is why OWASP's SSRF guidance says to disable redirects rather than validate the first URL and trust the rest. Pinned against real sockets by `RedirectTests`.

Opt back in per client when the destination genuinely redirects:

```json
{
  "HttpResilience": {
    "Clients": { "Search": { "Connection": { "AllowAutoRedirect": true } } }
  }
}
```

`AddHttpResilience` — the standard pipeline — keeps the runtime default of `true`, and applies **no destination control at all**. It has declared no closed destination set to leave: under `Mode: None` every request shares one pipeline, and even under `ByAuthority` an unlisted host is explicitly allowed. Turning redirects off there would break every client that talks to a CDN or a pre-signed URL. If a standard client's destination is influenced by request data, the cardinality that grows is `System.Net.Http`'s own `server.address` dimension, which this package neither sets nor can bound.

## Telemetry

This package emits through BCL primitives only and **takes no dependency on OpenTelemetry**: metrics on a `Meter` obtained from `IMeterFactory`, logs on `ILogger` under the category `HttpResilience`, and no `ActivitySource` of its own. Nothing leaves the process until the consuming service wires an SDK, which is why the meter names are exposed as constants rather than a package reference that would choose an exporter for every consumer.

Install the SDK in the **consuming service**: `OpenTelemetry.Extensions.Hosting` for `AddOpenTelemetry()` and the provider lifetime, `OpenTelemetry.Exporter.OpenTelemetryProtocol` for `AddOtlpExporter()`, and `OpenTelemetry.Instrumentation.Http` for `System.Net.Http`'s own metrics and per-attempt spans.

```csharp
using Microsoft.Extensions.DependencyInjection;

builder.Services.AddHttpResilience(builder.Configuration);
builder.Services.AddHttpClient("Orders").AddHttpResilience();

builder.Services.AddHttpResilienceTelemetry();   // adds the error.type tag. Registers no meter.

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("orders-api"))
    .WithMetrics(metrics => metrics
        .AddMeter(HttpResilienceTelemetryExtensions.PollyMeterName)  // "Polly" — retries, breaker events
        .AddMeter(HttpResilienceTelemetryExtensions.MeterName)       // "HttpResilience.NET" — breaker state, limiters
        .AddHttpClientInstrumentation()                              // request duration, connection pool
        .AddOtlpExporter())
    .WithTracing(tracing => tracing
        .AddHttpClientInstrumentation()                              // one span per attempt
        .AddOtlpExporter());

builder.Logging.AddOpenTelemetry(logging =>
{
    logging.IncludeFormattedMessage = true;
    logging.IncludeScopes = true;
    logging.AddOtlpExporter();
});
```

**`AddHttpResilienceTelemetry()` and `AddMeter(...)` do different jobs and neither substitutes for the other.** The first adds one tag and registers no instrument; the second registers instruments and adds no tag. Confusing them fails **silently**: the SDK drops every measurement from a meter name it was not given, so a missing `AddMeter` is an empty dashboard with nothing logged and no exception thrown.

Both meter names are constants on `HttpResilienceTelemetryExtensions` rather than strings you maintain. `"Polly"` covers **every** Polly pipeline in the container, not only this package's.

The one gap `AddHttpResilienceTelemetry` fills: `Microsoft.Extensions.Http.Resilience` already tags `error.type` with the status code when a **response** is the failure, and nothing tags it when an **exception** is — Polly emits `exception.type` there instead. A dashboard filtering on `error.type` would otherwise silently miss every connection failure, DNS failure and timeout.

Debug-level events (retry attempts, hedging attempts) are below the default log level:

```json
{ "Logging": { "LogLevel": { "HttpResilience": "Debug" } } }
```

**This package creates no spans, on purpose.** `System.Net.Http` emits one `Activity` per **attempt**, so a retried or hedged call already appears as several sibling HTTP spans under the caller's span — counting them is how you answer "was it retried, and how many times".

Every instrument, dimension, cardinality bound and log event ID: **[docs/OPERATIONS.md](docs/OPERATIONS.md#metrics)**.

### If your service uses `OpenTelemetry.NET` package

**On 2.7.0 or later the meters need no configuration.** `AddObservability(configuration)` registers HTTP client instrumentation and both meter names unconditionally — they are in `OpenTelemetryConstants.DefaultMeterNames`. Nothing in the block above is needed.

On **2.6.x or earlier**, add both explicitly, since the SDK drops measurements from a meter name it was not given:

```json
{ "OpenTelemetryOptions": { "Meters": [ "Polly", "HttpResilience.NET" ] } }
```

`AddHttpResilienceTelemetry()` is a separate call in `Program.cs` at **every** version. Nothing in the observability package can make it for you, and without it `error.type` is missing on every exception outcome.

## Health checks

```csharp
builder.Services.AddHttpResilienceHealthChecks();
// or, in the shape the rest of the health-check ecosystem uses:
builder.Services.AddHealthChecks().AddHttpResilience();

app.MapHealthChecks("/healthz/live",  new() { Predicate = _ => false });
app.MapHealthChecks("/healthz/ready", new() { Predicate = r => !r.Tags.Contains("dependency") });
app.MapHealthChecks("/healthz/deps",  new() { Predicate = r =>  r.Tags.Contains("dependency") });
```

Reports **Degraded** at worst, never Unhealthy, and is tagged `dependency` by default. The Degraded ceiling is the guarantee — it is unconditional, so even an accidental wiring to a liveness probe answers 200 and restarts nothing. The tag is routing: passing your own `tags` **replaces** it rather than adding to it.

**Degraded is HTTP 200.** ASP.NET Core's default `ResultStatusCodes` maps Healthy *and* Degraded to `200`, and only Unhealthy to `503` — so the endpoint's status code alone carries no signal, and an alert wired to it stays green while every circuit in the process is open. Alert on `http.resilience.circuit_breaker.state` instead, or opt in explicitly on the diagnostic endpoint only:

```csharp
app.MapHealthChecks("/healthz/deps", new HealthCheckOptions
{
    Predicate = r => r.Tags.Contains(HttpResilienceHealthCheckExtensions.DependencyTag),
    ResultStatusCodes =
    {
        [HealthStatus.Healthy]   = StatusCodes.Status200OK,
        [HealthStatus.Degraded]  = StatusCodes.Status503ServiceUnavailable,
        [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
    },
});
```

Do that for `/healthz/deps` and nothing else.

**Do not gate a liveness or readiness probe on it.** An open circuit means a downstream is unhealthy, not that this process is. Restarting the pod or pulling it from the load balancer would shed capacity during a dependency outage and amplify it.

**The dependency endpoint names your internal topology.** Its payload is keyed `client -> authority`, so `/healthz/deps` discloses internal client names and hostnames to anyone who can reach it. Keep it behind cluster-internal networking or authentication.

Registration is idempotent under one name, like `AddHttpResilience` and for the same reason. A second registration under a *different* name is a deliberate one and is honoured.

## Requirements

- .NET 10 SDK (pinned in `global.json`)
- **Trimming and Native AOT are supported.** Option binding goes through the configuration binding source generator, so there is no reflection to trim away. `tests/HttpResilience.NET.AotSmoke` publishes a Native AOT binary in CI and asserts the bound values at run time — a trimmed reflective binder does not fail loudly, it leaves a client running on defaults it never configured, so the claim is proven by execution rather than by a clean build.
- `nuget.config` clears defaults and adds `baps-apps-packages` for `CodeStyle.NET`. Restore from a fresh clone needs a GitHub PAT for that source — see [scripts/README.md](scripts/README.md).

## Distribution and license

Proprietary and internal. Copyright (c) BAPS, all rights reserved — this repository carries no open-source
license and grants no rights to use, copy, modify or redistribute it outside the organization.

The package is published to GitHub Packages under `baps-apps` (`https://nuget.pkg.github.com/baps-apps/index.json`)
and never to nuget.org. Consuming it needs that source in your `nuget.config` and a GitHub PAT with
`read:packages` — see [scripts/README.md](scripts/README.md). `packageSourceMapping` should pin
`HttpResilience.NET` to that source, so a public package cannot shadow the internal name.

## Documentation

- [CONFIGURATION.md](docs/CONFIGURATION.md) — every key, default and validation rule
- [ARCHITECTURE.md](docs/ARCHITECTURE.md) — how the package composes the platform, and what it deliberately does not own
- [OPERATIONS.md](docs/OPERATIONS.md) — amplification arithmetic, instruments, dashboards, alerts
- [RUNBOOK.md](docs/RUNBOOK.md) — what to do when resilience alerts fire
- [RECIPES.md](docs/RECIPES.md) — common configurations
- [TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md)
- [SECURITY-GOVERNANCE.md](docs/SECURITY-GOVERNANCE.md)
- [PRODUCTION-CHECKLIST.md](docs/PRODUCTION-CHECKLIST.md)
- [VERSIONING.md](docs/VERSIONING.md)
- [benchmarks/](docs/benchmarks/README.md) — the raw reports behind every performance claim
- [CHANGELOG.md](CHANGELOG.md)
- [V1.md](docs/V1.md) — using the 1.0.0 package, for services that have not moved to 2.0.0

## Building

```bash
dotnet build
dotnet test
dotnet run --project samples/HttpResilience.NET.Sample
dotnet run --project benchmarks/HttpResilience.NET.Benchmarks -c Release -- --filter "*"
```
