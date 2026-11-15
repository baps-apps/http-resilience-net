# Architecture

## Layering

```text
Your application        services.AddHttpResilience(configuration)
                        services.AddHttpClient("Orders").AddHttpResilienceHandler()
HttpResilience.NET      schema + startup validation, safe-method guards, connection settings,
                        a finite HttpClient.Timeout, bounded gauges and startup notices
Microsoft.Extensions.Http.Resilience    pipeline shape and strategy order
Polly v8                                strategy implementations
IHttpClientFactory                      handler composition and lifetime
SocketsHttpHandler                      connection pooling, HTTP/2, redirects
```

## Design rules

1. **Never own ordering.** `AddStandardResilienceHandler` / `AddStandardHedgingHandler` define the order; this
   package supplies values and predicates.
2. **Never copy a platform option type.** Map onto Microsoft's options, so platform improvements apply.
3. **Every escape hatch is the platform's** (`AddResilienceHandler`). No custom hooks: they would bypass
   validation.
4. **Unsafe by request, never by default.** Only RFC 9110 safe methods are repeated (an allow-list, unlike the
   platform's deny-list, which retries `MOVE` or `PURGE`). The root cannot widen it for everyone, and every
   client that can repeat a mutating request logs startup Warning event 10.
5. **Every metric dimension is bounded at registration.** No tag value comes from request data.
6. **Safety guarantees are tested against the mechanism**, not the option: hedging safety with a slow origin
   (the timer path), limiters with real saturation, the primary handler with a competing registration.

## Pipeline shape

Standard client, outermost first:

```text
ConcurrencyLimiter   (optional)   one slot per logical request
  ConcurrencyBackstop             only with a rate limiter and no ConcurrencyLimiter
    Limiter                       always present: the rate limiter if enabled, else the backstop
      Total timeout
        Retry
          Circuit breaker
            Attempt timeout
              SocketsHttpHandler
```

Hedged client:

```text
Authority allow-list → [ConcurrencyLimiter] → [RateLimiter] → Total timeout → Hedging
  → per authority: backstop limiter → Circuit breaker → Attempt timeout → primary handler
```

`Timeout:Client` (`HttpClient.Timeout`) wraps all of it, including limiter queues and the response body.

- **A concurrency bound is always present.** The platform's limiter slot is never empty: the rate limiter, or
  the backstop (`ConcurrencyLimiter:Backstop`, 1,000, no queue), one instance per pipeline. On a standard
  client a rate limiter displaces the backstop into its own handler (one per client), unless
  `ConcurrencyLimiter` is enabled: its `Limit` is validated at or below the backstop, so it is the bound. A
  hedged client's backstop is the per-authority endpoint limiter and never moves.
- **Limiters sit outside retry and hedging**, so one slot or permit covers a logical request, and a hedged
  client's supplementary attempt cannot be rejected in place of the real outcome. Handlers are registered
  outermost first, because `IHttpClientFactory` makes the first additional handler the outermost.
- **The hedging handler keeps one inner pipeline per authority for the life of the process**, so a hedged
  client requires `PipelineSelection:Authorities` and rejects other authorities in its outermost handler.

## Strategy semantics

| Strategy | Charged per | Retries / hedges | Relation to `Timeout:Total` | Caller cancellation |
| --- | --- | --- | --- | --- |
| Client timeout (`Timeout:Client`) | logical request | spans all | outside it; the only bound on the body | token wins |
| Concurrency limiter | logical request | one slot for all | queue wait is **outside** it | cancels the wait |
| Concurrency backstop | logical request | one slot for all | outside it; no queue | — |
| Rate limiter | logical request | one permit for all | queue wait is **outside** it | cancels the wait |
| Total timeout | logical request | spans all | — | token wins |
| Retry | logical request | owns them | inside | stops immediately |
| Circuit breaker | attempt | each attempt counts | inside; an attempt timeout counts as a failure | not a failure |
| Attempt timeout | attempt | — | inside | token wins |
| Hedging | logical request | owns them | inside | cancels losing attempts |
| Endpoint backstop, circuit breaker (hedged) | attempt, per authority | — | inside | not a failure |

`Timeout:Total` and `Timeout:Attempt` stop at response headers; `HttpClient` reads the body afterwards. The
platform sets `HttpClient.Timeout` to infinite; this package applies a finite `Timeout:Client` after it.

## Configuration model

- Per-client sections live under `Clients`, so a client named `Retry` cannot collide with the schema.
- The binder assigns only keys that are present, so a client section layers on the root with no merge logic.
  Lists are the exception: the binder appends, so a list a client states is cleared first and replaces the
  inherited one (an empty list clears it).
- **Registration reads no configuration.** Every value is read from
  `IOptionsMonitor<HttpResilienceOptions>.Get(name)` when the client is first built, after every `Configure`
  and `PostConfigure`. Every handler group is registered unconditionally and removed when the chain is built
  if live options switch it off. So registration order does not matter, configuration added later is honoured,
  and the pipeline runs on the same object a consumer reads back.
- No reload: options are registered with `Configure`, not `Bind`, so `IOptionsMonitor` never reports a value
  that is not in effect.

## Validation

Hand-written validators, no data annotations (they cannot express `TimeSpan` ranges or cross-key rules, and
skip named options). Runs:

- **In `AddHttpResilience(configuration)`**: the root unsafe-method rules only, as an early failure.
- **When a client's options are first built** (at host start via `ValidateOnStart`, otherwise at first client
  creation): every value and cross-key rule for that client's pipeline, inert keys in its own section, the
  root rules again (configuration sources may have been added since), root delegates, renamed and unknown keys.
- **For the root options at host start**: value rules and unused `Clients` sections. Needs a host that runs
  startup validation.
- **`ClientStartupProbe`** creates every configured client at host start, because whether a handler chain can
  be built is not an options fact. `ValidateClientsOnStart: false` opts out.
- **`PlatformSelfCheck`**, run by the probe first, once per process. The handler groups and the defaults
  replacement rely on `IHttpClientFactory` running its action lists in registration order, which it does but
  does not document. The check builds two canary clients in a private container (one with a concurrency limit
  and a later handler, one disabled, both under a defaults standard handler registered after them) against the
  platform version the application resolved, asserts their handler chains and the finite `HttpClient.Timeout`,
  and fails host start naming the versions if anything differs. About 70 ms on a cold process. It covers a
  consumer who resolves a new platform release before the weekly platform-drift run tests it. Its gauges use a
  renamed meter that no exporter subscribes to.

Handler groups are removed rather than left in place as pass-through handlers: an empty `ResilienceHandler`
adds 72 B and roughly a tenth of a standard pipeline's time per request (an ad-hoc measurement, not one of the
reports in `benchmarks/`), and the platform handler's group (the kill switch, the .NET Aspire replacement) needs
the markers regardless, because the platform's pipeline is reachable only through its own handler.

Startup notices (events 6, 10, 11, 14, 15, 18, 21) are logged by `ClientStartupReport` after validation and
before any hosted service starts, from each client's final options.

Platform limits are mirrored so they fail at startup rather than at the first request: timeouts 10 ms–1 day,
breaker windows ≤ 1 day (Polly's ranges), `Timeout:Client` and `ConnectTimeout` ≤ `int.MaxValue` ms.

## Trimming and Native AOT

Supported: configuration binding is source-generated, and `IsTrimmable` / `IsAotCompatible` are set with
warnings as errors. A trimmed reflective binder would fail silently (leaving defaults), so
`tests/HttpResilience.NET.AotSmoke` publishes a Native AOT binary that starts real hosts and asserts, with
values that differ from every default: bound options, retry and hedged origin call counts, connection settings
on the actual primary handler, the startup notices, and host start failing on an invalid configuration or an
uncreatable client. The integration tests run the same scenarios under JIT on every `dotnet test`.

## The consumer boundary

What happens when a consumer also configures a client this package configures:

| The consumer also… | Outcome | Mechanism |
| --- | --- | --- |
| calls `AddHttpResilienceHandler` / `AddHttpResilienceHedgingHandler` twice | **throws at registration** | client ledger on `HttpResilienceRegistration` |
| calls it inside `ConfigureHttpClientDefaults` | **throws at registration** | that builder has no name, so no section |
| adds the platform's `AddStandardResilienceHandler` | still nests (9 origin calls); **reported** at Information (event 12) | `ResilienceHandlerCountFilter` counts handlers |
| adds `AddResilienceHandler` (the supported hatch) | composes; also reported by event 12 | same filter; it cannot tell the two apart |
| sets `HttpClient.Timeout` via `ConfigureHttpClient` to anything but the resolved `Timeout:Client` (100 s included) | **fails at client creation** | post-configure brackets every `ConfigureHttpClient` |
| sets `HttpClient.Timeout` in a typed client's constructor | **not detected**; can truncate the pipeline | the constructor runs after the factory |
| replaces the primary handler, with `Connection:Enabled` | `SocketsHttpHandler` configured, and **fails creation** if it sets a connection key to a different value; an unconfigured `HttpClientHandler` replaced, a configured one (certificates, callback, proxy, credentials, ...) fails creation naming its settings; other types fail creation | `ConnectionHandlerFilter`, an `IHttpMessageHandlerBuilderFilter` |
| calls `SetHandlerLifetime` with a finite value, with `Connection:Enabled` | **fails at client creation**; an infinite one is kept | post-configure after every `SetHandlerLifetime` |
| adds `AddResilienceHandler` / `AddStandardResilienceHandler` to the client **before** this package's call | **fails at client creation** | a marker before the client's first earlier `Configure`; defaults run before it |
| adds a resilience handler through `ConfigureHttpClientDefaults` (.NET Aspire) | replaced; this package's pipeline takes its place (event 17, Information) | `ReplaceEarlierResilienceHandlers` |
| changes any option after registration | reaches the client | live options, read when the chain is built |
| registers the client before `AddHttpResilience` | works | registration reads no configuration |

### Client timeout guard

One post-configure adds two `HttpClientActions` to an enabled client: one inserted **first** that resets the
timeout to infinite, and one appended **last** that checks it and applies `Timeout:Client`. Every
`ConfigureHttpClient` runs between them, so a finite value at the end was set by the consumer. Side effects:

- Reading `client.Timeout` inside your own `ConfigureHttpClient` shows infinite.
- Your own `PostConfigure<HttpClientFactoryOptions>` registered after this package's is not checked.

A typed client's constructor runs after the factory finishes, so this cannot be caught:

```csharp
public OrdersApi(HttpClient client)
{
    client.Timeout = TimeSpan.FromSeconds(1);   // wins, silently
}
```

Against a 30-second `Timeout:Total`, every request is cut at one second with a bare `TaskCanceledException`
(`ConsumerBoundaryTests.ATypedClientsConstructorTimeout_TruncatesThePipeline_AndNoGuardCanSeeIt`). **Search
typed-client constructors for `Timeout`.**

### Why a nested pipeline is reported, not refused

A platform `AddStandardResilienceHandler` on a client that already has this pipeline nests a second one: nine
origin calls for a failing GET, and the total timeout applies twice. Through public API it is
indistinguishable from the supported `AddResilienceHandler`: both add a `ResilienceHandler`, the difference is
an internal pipeline name (readable only by reflection, which trimming and AOT rule out), and the platform's
key type is internal. So event 12 is Information, not Warning.

What *can* be told apart is a change to the options both pipelines read. The platform shares one options
instance per client (`{client}-standard`, or `{client}` for hedging) between every registration, so
`AddStandardResilienceHandler(configure)`, or a consumer's `Configure`/`PostConfigure`/`ConfigureAll` of those
options, would rewrite this package's validated pipeline, safe-method guard included. A second
`AddStandardHedgingHandler` needs no configure: its post-configure replaces the action generator that stops a
delay-started hedge. `PlatformOptionsGuard` records what the configurators wrote, per instance, and an
`IValidateOptions` for both option types, which runs after every configure and post-configure, fails the client
when anything differs (`PlatformOptionsOwnershipTests`). Only a bare `AddStandardResilienceHandler()` changes
nothing and is left to event 12.
