# Architecture

## Layering

```text
Consumer application
   │  services.AddHttpResilience(configuration);
   │  services.AddHttpClient<IOrdersApi, OrdersApi>().AddHttpResilience("Orders");
   ▼
HttpResilience.NET
   ├── organizational option schema + startup validation
   ├── safe defaults (only RFC 9110 safe methods are ever repeated)
   ├── SocketsHttpHandler standardization + infinite factory handler lifetime
   ├── a finite HttpClient.Timeout, which the platform's handlers remove
   ├── bounded telemetry conventions
   └── circuit breaker state for a dependency health check
   ▼
Microsoft.Extensions.Http.Resilience     owns pipeline shape and strategy ordering
   ▼
Polly v8                                 owns strategy implementations
   ▼
IHttpClientFactory                       owns handler lifetime and composition
   ▼
SocketsHttpHandler                       owns connection pooling, HTTP/2, HTTP/3
   ▼
.NET 10 runtime
```

## The rules that keep it thin

1. **Never own ordering.** `AddStandardResilienceHandler` and `AddStandardHedgingHandler` define a correct, tested order. This package supplies values and predicates; it does not compose a pipeline.
2. **Never copy a platform option type.** The schema maps onto Microsoft's options and stops. Every property mirrored into a parallel type is a property that would not benefit when the platform improves it.
3. **Every escape hatch is the platform's.** A consumer needing something unusual calls `AddResilienceHandler` directly — the package brings it in transitively. A custom escape hatch would only add a path that bypasses validation.
4. **Unsafe by request, never by default.** Only the four methods RFC 9110 defines as safe are ever repeated. This is an allow-list, not a deny-list of the five familiar mutating verbs: a deny-list repeats whatever it has not been told about, so a WebDAV `MOVE` or a cache `PURGE` would be retried and hedged by default. Repeating anything else requires an explicit, per-client, documented opt-in — and *per client* is enforced, not merely stated: the root section refuses either `DisableForUnsafeHttpMethods` flag being switched off, because a rule about one endpoint's idempotency handling cannot be expressed once for a fleet. Every client that can repeat a mutating request logs a Warning at host start naming the methods and the key, on both mechanisms, so the set is an inventory rather than a property nobody can enumerate.
5. **Every metric dimension is bounded at registration time.** No tag value originates from request data.
6. **A safety guarantee is proven against the mechanism, not the option.** Configuring a platform option whose name sounds like the guarantee is not the guarantee. Every safety claim here has a test that drives the mechanism down the path where it would fail: a *slow* origin for hedging, genuine saturation for a limiter, a competing registration for the primary handler. The hedging guard read correctly and did nothing, because `ShouldHandle` never sees the timer that starts a supplementary attempt.

## What runs when

| Code | Frequency |
| --- | --- |
| Connection settings applied to the primary handler | once per handler creation, after every `ConfigurePrimaryHttpMessageHandler` registration |
| Configuration binding, validation, pipeline-key selector construction | once per client at startup |
| Authority allow-list check (hedged clients) | per request, one frozen-dictionary lookup, no allocation |
| `SocketsHttpHandler` creation, pipeline construction, limiter creation | once per client (handler rotation is disabled when `Connection:Enabled`). Every limiter this package owns -- the rate limiter, the concurrency cap, the displaced backstop -- is a keyed singleton built from **live** options inside this step, so a consumer's `PostConfigure` reaches it. The rate limiter was the one exception until it was fixed: built from a registration-time snapshot, it enforced a budget `IOptionsMonitor` did not report. |
| Pipeline lookup by authority | per request, only under `ByAuthority`, no allocation |
| Metering enrichment | per Polly telemetry event |
| Retry and circuit breaker callbacks | per retry, per state change |

Benchmarks covering each of these live in `benchmarks/`. The number that matters is the gap between the Microsoft standard handler and this package's standard pipeline: that gap is this package's overhead.

```
dotnet run --project benchmarks/HttpResilience.NET.Benchmarks -c Release -- --filter "*" --job medium --memory
```

Apple M4, .NET 10, `MediumRun`, in-memory origin so the transport is not the variable. **The numbers live in
one place — [docs/benchmarks/](benchmarks/README.md) — beside the raw reports they come from.** They used to
be duplicated here as well, and the two copies drifted from the reports until a script started comparing
them; a second hand-maintained copy of a measured number is a second thing to go stale.

Every strategy this package configures costs nothing over the platform handler. The one fixed cost it adds is
**one `CancellationTokenSource` per request** — allocated by `HttpClient` when `HttpClient.Timeout` is finite.
`AddStandardResilienceHandler` sets that timeout to infinite; this package sets it back, because
`Timeout:Total` stops applying at response headers and nothing else bounds the response *body*. That is the
price of an origin not being able to hold a connection and a buffer open by trickling bytes, and
`PipelineAllocationTests` pins it so the number cannot drift unnoticed — as it did: earlier revisions claimed
parity and were not re-run when the timeout fix broke it.

Per-authority selection allocates nothing on top of that, and authority matching itself is single-digit
nanoseconds with a unit test that fails on one byte.

The other cost is the rate limiter, which displaces the standard handler's own limiter and so causes the
concurrency backstop to be re-applied as a handler of its own rather than disappearing. That is a deliberate
trade -- a safety control that does not vanish when an operator enables an unrelated one -- paid only by
clients that enable rate limiting, and skipped when the client already has a concurrency cap of its own,
because validation holds that cap at or below the backstop.

Measured under contention as well as single-threaded: limiter overhead is a flat ~1.33x from 1 to 64
concurrent requests, so there is no lock cliff to plan around. Every benchmark here was single-threaded until
that was measured, which is the one shape in which a shared lock is free.

## Pipeline shape

Fixed, outermost to innermost:

```text
[ AuthorityAllowList ]                 hedged clients only, outermost of everything
  └─ ConcurrencyLimiter   (optional)   one slot per logical request
       └─ ConcurrencyBackstop          only when a rate limiter has taken the slot below
            └─ Limiter                 ALWAYS PRESENT — rate limiter if configured, else the backstop
                 └─ Total timeout
                      └─ Retry
                           └─ Circuit breaker
                                └─ Attempt timeout
                                     └─ SocketsHttpHandler
```

`Timeout:Client` (`HttpClient.Timeout`) sits outside all of this, including both limiter queues and the
response-body transfer the pipeline cannot reach.

The standard handler's limiter slot is never empty: not configured it holds a concurrency limiter of 1,000 with no queue. That is a real control and it was invisible, so the schema owns the number (`ConcurrencyLimiter:Backstop`). Assigning a rate limiter *replaces* what is in that slot, which would mean enabling rate limiting silently removed a concurrency cap — so when a rate limiter is configured the backstop is re-added as its own handler outside it.

**The invariant is that a concurrency bound is never absent — not that the backstop is always the thing providing it.** In one configuration it is not: with both a rate limiter and the client's own `ConcurrencyLimiter:Limit` enabled, the backstop handler is skipped, because validation holds `Limit` at or below `Backstop` and it therefore bounds concurrency more tightly. The combination is pinned by `ConcurrencyBackstopTests.ConcurrencyBound_StillHolds_WhenBothLimitersAreEnabled`, which is what makes the weaker claim checkable rather than merely argued.

Ordering is not configurable. `IHttpClientFactory` composes the first registered additional handler as the **outermost** one, so handlers are registered outermost-first. Placing the concurrency limiter and rate limiter outside the retry loop is what makes a slot and a permit cover a logical request rather than an attempt — a retrying request can never be rejected by its own budget.

On the hedging pipeline the rate limiter is added as its own handler outside the hedging handler, rather than using the per-endpoint limiter, so a permit means the same thing on both pipelines. The per-endpoint limiter would charge a permit per hedged attempt, and a rejected *supplementary* attempt would surface a `RateLimiterRejectedException` to the caller in place of the real outcome.

The hedging handler keeps its own inner pipeline per authority — endpoint concurrency limiter, circuit breaker, attempt timeout — cached for the life of the process. That is sound for the fixed set of endpoints it is designed around and a resource-exhaustion path for a client whose destination can come from request data, so `AddHedgedHttpResilience` requires `PipelineSelection:Authorities` and rejects anything else in an outermost handler, before a pipeline can be minted for it. The same bounded key labels circuit breaker state, so every live breaker has its own entry in the health check rather than the last transition overwriting the rest.

## Strategy semantics

| Strategy | Charged per | Sees retries | Inside total timeout | Caller cancellation |
| --- | --- | --- | --- | --- |
| Client timeout (`Timeout:Client`) | logical request | spans all attempts | **outside** it, and the only bound on the response body | token wins |
| Concurrency limiter | logical request | holds one slot across them | **no** — the queue wait is outside it | cancels the queued wait |
| Concurrency backstop | logical request | holds one slot across them | **no** | cancels the wait (there is no queue) |
| Rate limiter | logical request | retries covered by one permit | **no** — the queue wait is outside it | cancels the queued wait |
| Total timeout | logical request | spans all attempts | owns it | token wins |
| Retry | logical request | owns it | catches attempt timeout | stops immediately |
| Circuit breaker | attempt | each attempt counts | attempt timeout counts as failure | not counted as a failure |
| Attempt timeout | attempt | — | owns it | token wins |
| Hedging | logical request | replaces retry | yes | cancels losing attempts |
| Endpoint circuit breaker (hedging) | attempt, **per authority** | n/a | yes | not counted as a failure |
| Endpoint concurrency limiter (hedging) | hedged attempt, per authority | n/a | yes | cancels the wait |

Both limiters sit outside the total timeout because that is the platform's ordering, and it is what makes one permit cover a whole logical request. The consequence is that a queued request can exceed `Timeout:Total`, bounded by `Timeout:Client` and the caller's cancellation token. Keep `QueueLimit` small.

`Timeout:Total` also stops applying at response *headers*: every strategy lives in the handler chain, and the chain returns once `SocketsHttpHandler` has them. The response body is buffered afterwards by `HttpClient` itself, outside the chain. `Microsoft.Extensions.Http.Resilience` sets `HttpClient.Timeout` to infinite so its own total timeout is authoritative for the attempts, which leaves that body transfer bounded by nothing; `Timeout:Client` is applied after the platform handler to put a finite bound back.

## Configuration model

The root section is `HttpResilience`. Per-client overrides live under `HttpResilience:Clients:{name}`, bound on top of the root values — configuration binding only assigns keys that are present, so inheritance falls out of the binder rather than needing merge logic.

Client sections are namespaced under their own `Clients` child so that a client named `Retry` or `Timeout` cannot collide with a schema property.

## Validation

One hand-written validator, no data annotations. Annotations cannot express a `TimeSpan` range or a relationship between two properties, they are scoped to a single options name so they silently skip every per-client registration, and filtering their output requires matching on message text.

Validation runs twice, deliberately:

- **Eagerly at registration**, before any value is used to build a handler, so registration code never reads a value the validator was about to reject.
- **At startup via `ValidateOnStart`**, per client, against the pipeline that client actually uses — retry budget rules for the standard pipeline, hedging budget rules for the hedging one.

Rules that exist because the platform enforces them at runtime, and a first-request failure in production is worse than a startup failure:

- `Timeout:Attempt` strictly less than `Timeout:Total`
- `Timeout:Client` strictly greater than `Timeout:Total` — it is the backstop, not a competing budget
- `Retry:*` keys absent from a client registered with `AddHedgedHttpResilience`, which has no retry strategy to read them
- `CircuitBreaker:SamplingDuration` at least double `Timeout:Attempt`
- `Retry:MaxRetries` at least 1 — `Retry:Enabled` is the off switch
- `PipelineSelection:Authorities` non-empty for any client registered with `AddHedgedHttpResilience`
- `ConcurrencyLimiter:Limit` at most `ConcurrencyLimiter:Backstop` — a higher cap is never reached, because the excess is rejected by the platform's inner limiter rather than queued by the outer one
- `Connection:ConnectTimeout` strictly less than `Timeout:Attempt`
- `PipelineSelection:Authorities` populated with `Mode: None` on a standard client, where the list has no effect
- `Retry:DisableForUnsafeHttpMethods` **stated at all** beside a `Retry:RetryableMethods` allow-list in force, which replaces it — two written statements about duplicating mutating requests, one of which is not in force. Both directions fail: `false` in the options validator, `true` at registration. The `true` direction is the worse one, because the statement being discarded is the protective one and the list may have been inherited from a section a different team owns
- `Hedging:*` keys on a client registered with `AddHttpResilience`, the mirror of the `Retry:*` rule above — the standard pipeline has no hedging strategy to read them

One rule is about *where* a value is stated rather than what it is. Neither `DisableForUnsafeHttpMethods` flag may be `false` at the root section: every client inherits the root, so one key there decides that every client in the process — including clients registered later that state nothing — may deliver a mutating request to its origin more than once. Whether that is safe is a property of one endpoint's idempotency handling, so the decision belongs in a `Clients:{name}` section. `Retry:RetryableMethods` is still inheritable, but only in the narrowing direction: a root list of `["GET"]` restricts every client, which is what one shared statement should be able to say, while an unsafe entry at the root is refused because it reaches every standard client in the process by exactly the route the flag is refused for. A client returns to the default guard under an inherited list by stating an empty list of its own.

The root section is validated for range, not for budget: whether a retry schedule has to fit the total timeout depends on which pipeline the client registering it uses, and only the per-client validators know that.

Two rules are about the configuration *file* rather than about a value, and run once against the root options at startup — they cannot run eagerly at registration, because a section unread when the third client registers may be read by the fourth:

- **`Clients:{name}` sections that no registered client reads.** Inert configuration is indistinguishable from configuration in force, and a typed client's section name is `TClient` — `AddHttpClient<IOrdersApi, OrdersApi>()` reads `Clients:IOrdersApi`. The message lists the sections that *are* read. `AllowUnusedClientSections` opts out, root-only, defaulting to failing.
- **Keys renamed rather than aliased.** `Retry:MaxAttempts` is still bound, to a tombstone property, so a file carrying it fails with a message naming `Retry:MaxRetries` instead of binding to nothing.

One thing validation cannot reach at all: whether a client can be *created*. The primary handler is a DI fact and the chain is not built until `CreateClient`, so `Connection:Enabled` on a client whose handler is not a `SocketsHttpHandler` throws on first use. `ClientStartupProbe` moves it to host start — an `IHostedService`, registered by `AddHttpResilience` itself, that creates every client this package configured, once.

It is on by default: the alternative leaves the difference between "the deployment fails" and "a rare code path returns 500s hours later" resting on every adopting service separately remembering a checklist line. Opting out is `HttpResilience:ValidateClientsOnStart`, read from the raw section like `AllowUnusedClientSections` — a configuration key rather than a code change, so it is reachable during an incident. `ValidateHttpResilienceClientsOnStart()` remains, idempotent; the key set to `false` beside that call fails startup naming both, because the call would win and a key that silently does nothing is worse than no key.

## Trimming and Native AOT

Supported. Option binding goes through the configuration binding source generator, so the entry points carry no `RequiresUnreferencedCode` or `RequiresDynamicCode` and there is no reflection for the trimmer to remove. `IsTrimmable`, `EnableTrimAnalyzer` and `IsAotCompatible` are set, and with `TreatWarningsAsErrors` a reflective binding call reintroduced in this assembly fails the build.

The analyzers are necessary but not sufficient: trimming a reflective binder does not throw, it leaves a client silently running on defaults it never configured. `tests/HttpResilience.NET.AotSmoke` therefore publishes a Native AOT binary in CI and asserts bound values -- `TimeSpan`, enums by name, a per-client override, a string list -- plus an origin call count through the real pipeline.

## Configuration reload

Not supported; a restart is required. Options are registered with `Configure` rather than `Bind`, so no reload token is created and a configuration reload cannot leave `IOptionsMonitor` reporting a value that is not in effect. Within the process the reported values *are* the ones the pipeline was built from — it reads the same options instance — except for the handful in `StructuralDecisions`, where a late change fails startup rather than being reported. Rebuilding handler pipelines under load would mean disposing live strategies with requests in flight, which is not worth the failure modes it introduces.

## Key files

| File | Purpose |
| --- | --- |
| `Extensions/HttpResilienceServiceCollectionExtensions.cs` | `AddHttpResilience` — schema registration and root-section handoff |
| `Extensions/HttpResilienceHttpClientBuilderExtensions.cs` | `AddHttpResilience` / `AddHedgedHttpResilience` per client |
| `Options/HttpResilienceOptions.cs` | Root configuration model |
| `Internal/HttpResilienceOptionsValidator.cs` | All validation rules and message formatting |
| `Internal/NamedPipelineOptionsValidator.cs` | Per-client startup validation, aware of the pipeline kind |
| `Internal/StandardPipelineConfigurator.cs` | Maps options onto `HttpStandardResilienceOptions` |
| `Internal/HedgingPipelineConfigurator.cs` | Maps options onto `HttpStandardHedgingResilienceOptions` |
| `Internal/CircuitBreakerCallbacks.cs` | Breaker thresholds and per-pipeline state reporting |
| `Internal/PipelineKeySelector.cs` | Bounded authority keys for pipelines and state tracking |
| `Internal/SocketsHttpHandlerFactory.cs` | Applies `ConnectionOptions` to whatever primary handler the client ends up with |
| `Internal/ConnectionHandlerFilter.cs` | Runs that after every `ConfigurePrimaryHttpMessageHandler`, so registration order cannot defeat it |
| `Internal/AuthorityIndex.cs` | Allocation-free authority matching for pipeline keys and the hedged allow-list |
| `Internal/RateLimiterFactory.cs` | BCL rate limiter from configuration |
| `Internal/HttpResilienceMeteringEnricher.cs` | The single `error.type` tag |
| `Internal/HttpResilienceHealthCheck.cs` | Dependency health from breaker state |
| `Internal/HttpResilienceRegistration.cs` | Root section handoff, and the guard against a second registration nesting two pipelines |
| `Internal/AuthorityAllowListHandler.cs` | Bounds the authorities a hedged client may reach |
| `Internal/StructuralDecisions.cs` | The options that decide which handlers exist, and so cannot honour a late change |
| `Internal/DisabledClientNotice.cs` | One log line when a client is registered with resilience switched off |
| `Internal/CircuitBreakerReachNotice.cs` | The startup line stating the traffic a client's breaker needs before it can open |
| `Internal/UnsafeMethodNotice.cs` | One log line when a client can repeat a mutating request, by either mechanism |
| `Internal/RateLimiterKey.cs` | The DI key for a client's limiter, owned by this package so a consumer cannot collide with it |
| `Internal/UnusedClientSectionValidator.cs` | Fails startup on a `Clients:{name}` section no client reads |
| `Internal/ClientStartupProbe.cs` | The default `IHostedService` that creates every configured client at host start |
| `Internal/ResilienceHandlerCountFilter.cs` | Reports a client carrying a resilience handler this package did not add, and documents why it cannot fail instead |
| `Internal/HttpResilienceMetrics.cs` | The three gauges Polly and `System.Net.Http` do not publish, and `LimiterKind` |

## The consumer boundary

Every guard in this package was written against its own API, and that is where its blind spot was. A review
found four defects in a row at the boundary where a *consumer* also touches a client this package configured —
each one silent, and each one passing a 319-test suite that had no consumer in it. What follows is what each
guard covers and, more usefully, what it does not.

| A consumer also… | Outcome | Mechanism |
| --- | --- | --- |
| calls `AddHttpResilience` twice on one client | **fails at registration** | A ledger of client names on `HttpResilienceRegistration` |
| calls the platform's `AddStandardResilienceHandler` on the same client | **reported at Information** (event 12); still nests | `ResilienceHandlerCountFilter` counts the composed chain |
| calls `AddResilienceHandler` — the documented hatch | works, composes, also reported | The same filter; it cannot tell the two apart |
| sets `HttpClient.Timeout` through `ConfigureHttpClient` | **fails at client creation** | `Timeout:Client` applied from a post-configure that runs after every `ConfigureHttpClient` |
| sets `HttpClient.Timeout` to exactly 100 seconds through `ConfigureHttpClient` | fails at client creation | Refused like any other conflicting value; see below |
| sets `HttpClient.Timeout` in a **typed client's constructor** | **not seen at all** — truncates the pipeline | Nothing runs after typed-client activation; see below |
| replaces the primary handler after registration | connection settings still applied | `ConnectionHandlerFilter`, an `IHttpMessageHandlerBuilderFilter` |
| changes a handler-composition option after registration | **fails at startup** | `StructuralDecisions` + `NamedPipelineOptionsValidator` |
| changes any other option after registration | reaches the pipeline | Every configurator reads `IOptionsMonitor` inside the platform's build delegate |

### What the client-timeout guard cannot see

Two shapes get past it, and both are stated here rather than left to be discovered.

**A typed client's constructor.** `ApplyClientTimeout` is an `IPostConfigureOptions<HttpClientFactoryOptions>`,
which is the last phase `IHttpClientFactory` runs while building a client — that is what makes it beat
`ConfigureHttpClient`. A typed client's constructor runs *after* the factory has finished, on the instance it
returns, so no options phase, filter or validator exists that could observe the assignment:

```csharp
public OrdersApi(HttpClient client)
{
    client.Timeout = TimeSpan.FromSeconds(1);   // wins, and nothing reports it
}
```

Measured: against `Timeout:Total` of 30 seconds this truncates the pipeline to one second, produces one origin
call, and surfaces a bare `TaskCanceledException` carrying none of the pipeline's context — the exact condition
`ValidateTimeouts` refuses when the same value is written as `Timeout:Client`. It is the same class of defect as
the nesting one below: real, unpreventable through public API, and therefore documented rather than implied
away. `ConsumerBoundaryTests.ATypedClientsConstructorTimeout_TruncatesThePipeline_AndNoGuardCanSeeIt` pins the
damage so the numbers quoted here stay true. **Grep for `Timeout` in typed-client constructors; the log will
not tell you.**

**Exactly 100 seconds is not a hole.** Inferring "nothing assigned one" from the framework's 100-second
default would make a consumer who deliberately wrote 100 seconds indistinguishable from silence, and no
sentinel avoids that, because the unset value of a `TimeSpan` property is a real duration.

That was true and it was answering the wrong question. The ambiguity was never in the *value*; it was in the
*moment of reading*. `ApplyClientTimeout` now registers **two** actions rather than one: the first at index 0
of `HttpClientActions`, which normalises the timeout to `Timeout.InfiniteTimeSpan`, and the second appended at
the end, which reads it. Index 0 is genuinely first at execution time because every `ConfigureHttpClient` and
the platform handler's own assignment are registered through `IConfigureOptions`, and all of those have
already run by the time an `IPostConfigureOptions` sees the list. So "nothing assigned one" is *established*
rather than inferred, any finite value surviving to the last action is a consumer statement by construction,
and 100 seconds is refused exactly like 2 seconds.

Two consequences worth stating rather than discovering. A consumer reading `client.Timeout` inside their own
`ConfigureHttpClient` now sees infinite rather than 100 seconds. And a consumer who registers their own
`IPostConfigureOptions<HttpClientFactoryOptions>` that also inserts at index 0 would run before this one —
unguardable, and already true at the other end of the list.

### Why the nesting guard reports instead of failing

This is the one place a guard is weaker than it looks, so it is stated rather than implied.

`AddStandardResilienceHandler` on a client that already has a pipeline nests a second one: retries multiply
rather than add — measured at **nine** origin calls for one GET at the default retry count — and the total
timeout is applied twice. It is exactly what the duplicate-registration guard exists to prevent, reached
through the API every Microsoft Learn page shows.

It is not prevented because the excess is **not attributable through public API**. Measured, in order:

- `AddResilienceHandler` — the hatch this package recommends — adds a `ResilienceHandler` to the same chain,
  and is correct: origin call count unchanged, no timeout doubled. So a count alone cannot decide.
- The difference between them is the pipeline name on the handler, an internal field. Reading it needs
  reflection, which `IsTrimmable` and `IsAotCompatible` rule out and `EnableTrimAnalyzer` would fail the build
  over.
- The platform's internal `HttpKey` in the service collection would distinguish them, and is also internal.
- Every other observable difference was measured and there is none: the platform's own options registrations
  are `TryAdd`-shaped, so a second `AddStandardResilienceHandler` adds no descriptor a consumer's
  `AddResilienceHandler` does not.

So the state is ambiguous, and this package's rule for an ambiguous state is the one behind log event 11:
**Information, not Warning**, because a signal that is frequently correct and fires on the documented pattern
is a signal operators filter out. What it buys is that "does any client here have two nested pipelines?" is
answerable from logs, which it was not.

One symptom *is* fixed rather than reported. A second platform handler would otherwise reset
`HttpClient.Timeout` to infinite over the finite one this package restores — removing the response-body bound
entirely — because `ConfigureHttpClient` is last-wins. `Timeout:Client` comes from an
`IPostConfigureOptions<HttpClientFactoryOptions>` instead, and every `IConfigureOptions` runs before every
`IPostConfigureOptions`, so it wins regardless of registration order. Same reasoning as the primary handler,
one phase later.
