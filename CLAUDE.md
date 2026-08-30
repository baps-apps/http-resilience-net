# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Prerequisites

- .NET 10 SDK (pinned in `global.json` with `rollForward: latestFeature`)
- `nuget.config` clears defaults and adds `baps-apps-packages` (GitHub Packages) for `CodeStyle.NET`. Restore from a fresh clone needs a GitHub PAT configured for that source — see `scripts/README.md`.

## Build & Test Commands

```bash
dotnet build
dotnet test

dotnet test tests/HttpResilience.NET.Tests/
dotnet test tests/HttpResilience.NET.IntegrationTests/
dotnet test --filter "FullyQualifiedName~UnsafeHttpMethodTests"

# Bare `dotnet test` builds Debug, where every [ReleaseOnlyFact] — the allocation ceilings — reports as
# skipped. CI runs both legs. Run this before calling a suite green.
dotnet test -c Release

dotnet run --project samples/HttpResilience.NET.Sample/
dotnet run --project benchmarks/HttpResilience.NET.Benchmarks -c Release -- --filter "*"

dotnet pack src/HttpResilience.NET/
pwsh scripts/publish-package.ps1   # requires GITHUB_PAT

# The rest of the CI gates, runnable locally. Formatting is whitespace-only on purpose: full `dotnet format`
# compiles without the configuration binding source generator, so every intercepted Bind call reports IL2026
# and IL3050 that the real build never emits.
dotnet format whitespace --verify-no-changes
dotnet list package --vulnerable --include-transitive   # CI fails the build on any hit
python3 scripts/check-benchmark-docs.py                 # docs/benchmarks numbers vs the raw reports
python3 scripts/check-public-api-shipped.py             # tag builds only

# Proves the IsTrimmable / IsAotCompatible claim. Analyzers are necessary but not sufficient: a trimmed
# reflective binder does not throw, it leaves a client on defaults it never configured.
dotnet publish tests/HttpResilience.NET.AotSmoke -c Release -r osx-arm64
./tests/HttpResilience.NET.AotSmoke/bin/Release/net10.0/osx-arm64/publish/HttpResilience.NET.AotSmoke
```

Solution file is `HttpResilience.NET.slnx` (XML solution format). `dotnet` resolves it automatically from the repo root.

## Code Conventions

- `TreatWarningsAsErrors` is enabled globally — all warnings are build errors.
- `GenerateDocumentationFile` is enabled — public APIs require XML doc comments.
- Nullable reference types throughout; target framework is `net10.0`.
- Central package management via `Directory.Packages.props`: add the version there, reference without a version in the `.csproj`.
- `Directory.Build.props` applies `Deterministic`, embedded PDBs and SourceLink to every project.
- **Every public member is recorded in `src/HttpResilience.NET/PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt`.** `Microsoft.CodeAnalysis.PublicApiAnalyzers` plus `TreatWarningsAsErrors` make an unrecorded addition, removal or signature change a build error — add the line to `PublicAPI.Unshipped.txt` in the same commit (RS0016 quotes the exact text). Moving Unshipped into Shipped is a release step, not a per-PR one, and `scripts/check-public-api-shipped.py` gates it on tag builds.
- `EnforceCodeStyleInBuild` is on in the library project, so style rules are build errors there too.

## Architecture

The package **configures** `Microsoft.Extensions.Http.Resilience`. It does not implement retry, timeouts, circuit breaking, rate limiting or connection pooling, and it must not start to.

Twelve rules keep it thin. The first ten are the reason the 2.0 rewrite happened; rules 11 and 12 came from a
later review that found four silent defects at the consumer boundary. Changes that violate any of them should
be pushed back on:

1. **Never own pipeline ordering.** `AddStandardResilienceHandler` and `AddStandardHedgingHandler` define the order. This package supplies values and predicates only. (In 1.0 a configurable `PipelineOrder` was applied in reverse, silently inverting every multi-strategy pipeline.)
2. **Never copy a platform option type.** Map the schema onto Microsoft's options and stop.
3. **Every escape hatch is the platform's.** Consumers needing something unusual call `AddResilienceHandler` directly. Do not add a custom one — the 1.0 versions bypassed validation and silently dropped a parameter.
4. **Unsafe by request, never by default — and *per client* is enforced, not merely stated.** Only the four RFC 9110 safe methods (GET/HEAD/OPTIONS/TRACE) are ever repeated. This is an *allow-list*, not a deny-list of the five familiar mutating verbs — the platform's own `DisableForUnsafeHttpMethods` is a deny-list, which retries whatever it has not heard of (`MOVE`, `PURGE`, `PROPPATCH`). Repeating anything else requires an explicit per-client opt-in.

   Two things make "per client" real rather than advisory. Neither `Retry:DisableForUnsafeHttpMethods` nor `Hedging:DisableForUnsafeHttpMethods` may be `false` in the **root** section: every client inherits the root, so one key there decided it for every client in the process, including clients registered afterwards that state nothing. That is refused in `AddHttpResilience` itself, against the raw section — *not* only in the options validator, which evaluates root options that materialize only when something calls `IStartupValidator`; a generic host does and a bare `ServiceCollection` does not, so the rule was skippable by choice of hosting model and four tests setting the flag at the root went on passing. And `UnsafeMethodNotice` logs a **Warning** (event 10) at startup for every client that can repeat a mutating request, by *either* mechanism, including `Retry:RetryableMethods` — the supported path, which is still worth a line because "which of our clients can duplicate a mutation?" is an incident question that should be answerable from logs. `Retry:RetryableMethods` stays inheritable from the root **in the narrowing direction only**: a root list of `["GET"]` restricts every client and is safer than the default, while an unsafe entry at the root reaches every standard client by exactly the route the flags are refused for, so it is refused too and belongs per client. That confinement is also what makes the two guards detectable against each other -- once unsafe entries can only be in a client section, the list and the flag are necessarily co-located. A client stating the flag beside a list in force fails registration in *both* directions; the `true` direction is the one that matters, because the discarded statement is the protective one, and it was silently accepted while three POST bodies reached the origin. A client narrows back to safe methods under an inherited list by stating an empty `RetryableMethods`, not by stating the flag.
5. **Every metric dimension is bounded at registration.** No tag value may originate from request data — including the dimensions the platform emits on this package's behalf. The hedging handler takes the request authority as `pipeline.instance` and mints a pipeline per authority, which is why `AddHedgedHttpResilience` requires an `Authorities` allow-list and rejects anything else.
6. **One registration per client, and an idempotent root registration.** A second `AddHttpResilience` on the same client nests two pipelines and multiplies retries, so it fails at startup. The guard is a ledger on the registered `HttpResilienceRegistration` instance, so the root `AddHttpResilience(configuration)` must never replace that instance — doing so disarmed the guard and produced nine origin calls for one GET. It is idempotent; a second call with a *different* section throws.

   **The guard covers this package's API and not the platform's, and that limit is documented rather than implied away.** A consumer calling `AddStandardResilienceHandler` on a client that already has a pipeline nests one just as effectively — measured at nine origin calls — and it cannot be refused, because the excess is not attributable through public API: `AddResilienceHandler`, the hatch this package recommends, adds a `ResilienceHandler` to the same chain and is correct. The distinguishing pipeline name is an internal field, the platform's `HttpKey` is internal, and the platform's own options registrations are `TryAdd`-shaped, so no descriptor differs. Reading the field needs reflection this package's trim and AOT declarations rule out. So `ResilienceHandlerCountFilter` reports at **Information** — rule: an ambiguous signal that fires on the documented pattern gets the level event 11 gets, not the level event 10 gets. Never promote it to Warning without first finding a sound discriminator.
7. **One source of truth for options, held by construction rather than policed.** The pipeline configurators read `IOptionsMonitor<HttpResilienceOptions>.Get(name)` *inside* the delegate the platform invokes when it first builds the pipeline — after every `Configure` and `PostConfigure`. So the values the pipeline runs on are the same object a consumer reads back, and a consumer's `PostConfigure` reaches the pipeline exactly as it does for the platform's own options. Never capture the options object at registration and configure the pipeline from that: it was how this worked before, and it cost a hand-maintained mirror of the whole options graph (`CopyTo` + `Describe`) kept honest by a reflection test, while still leaving `PostConfigure<HttpStandardResilienceOptions>` — the type that actually drives the strategies — able to change behavior unguarded.

   This held for every value except one, and the exception cost exactly what the rule predicts: the rate limiter was built from the `RateLimiterOptions` instance captured at registration, so a consumer's `PostConfigure` raising `PermitLimit` was reported by `IOptionsMonitor` and was not in effect — measured at a reported 50 against an enforced 2, with startup validation clean, and invisible to `NamedPipelineOptionsValidator` because the limiter's shape is not a structural decision. Every limiter is now created from live options inside its keyed factory, which the pipeline resolves when it is first built. **A new limiter, or anything else built from options, goes in a factory that reads `IOptionsMonitor` — never in a closure over the registration snapshot.**

   The exception is anything that decides **which handlers exist**, because `IHttpClientFactory` composes a client's chain from registration-time decisions. That set is `StructuralDecisions`: `Enabled`, `RateLimiter:Enabled`, `ConcurrencyLimiter:Enabled`, `PipelineSelection:Mode`, `Connection:Enabled` and the resolved `Connection:AllowAutoRedirect`. `NamedPipelineOptionsValidator` fails startup when one of those changed after registration. A new option is a value option by default, which is the safe default; adding one to `StructuralDecisions` should be a deliberate act.
8. **A safety guarantee is proven against the mechanism, not the option.** Setting a platform option whose name sounds like the guarantee is not the guarantee. `Hedging.ShouldHandle` returning false read like "never hedge a POST" and did nothing about the timer that starts a supplementary attempt when the primary is slow — which is the only case hedging exists for. Every safety claim needs a test that drives the mechanism down the path where it would fail: a *slow* origin for hedging, genuine saturation for a limiter, a competing registration for the primary handler.
9. **A control is only as strong as the layer it sits in.** `SocketsHttpHandler` resolves redirects below
   every `DelegatingHandler`, so the authority allow-list cannot see the second hop: it bounds *pipeline
   cardinality*, and `Connection:AllowAutoRedirect` bounds *destinations*. The two are one control, which is
   why the pipeline that enforces a list (hedging) resolves the redirect flag to `false` and applies it even
   with `Connection:Enabled` off. A standard client keeps the runtime default -- it has declared no closed set.
   `RedirectTests` pins this against real sockets; `TestServer`'s handler follows no redirects and could not
   fail. The runtime strips `Authorization` across a redirect but re-sends custom headers, so the exposure is
   API-key auth, not bearer tokens.

   **Because it is a security control rather than a performance one, it is also the one connection property
   this package never writes onto somebody else's handler unasked, and the one whose absence is never
   silent.** Both halves were wrong. `Apply` assigned `AllowAutoRedirect` from the *resolved* value, and the
   resolved value for a standard client that stated nothing is `true` -- so a consumer who hardened their own
   `SocketsHttpHandler` with `AllowAutoRedirect = false` had it reversed by nothing more than
   `Connection:Enabled`, while TROUBLESHOOTING.md told them their handler's settings were preserved.
   `BindEffective` destroys the difference between "unstated" and "stated true" by resolving, so
   `ConnectionOptions.AllowAutoRedirectStated` is recorded before the resolution and is read in exactly one
   place: `ApplyRedirectBound` writes only when the value was stated or resolves to `false`. `false` is always
   written -- that is the hedged bound, and a bound a consumer's handler defeats by construction is not one.
   Separately, `DisableAutoRedirect` returned in silence when the primary handler had no `AllowAutoRedirect`
   at all; it is now `TryDisableAutoRedirect` and the caller logs **Warning event 13**. It reports rather than
   throws -- unlike the `Connection:Enabled` path -- because the handler that reaches that branch in practice
   is a test stub that resolves no redirects, and the way out of a throw would have been to state
   `AllowAutoRedirect: true`, i.e. to teach people to switch a security bound off to make tests compile.
   Both directions are in `ConsumerBoundaryTests`, which is where they should have been from the start: rule
   11 names this exact boundary, and neither defect was reachable by any test without a consumer in it.
10. **Never own the primary handler by racing for it.** `ConfigurePrimaryHttpMessageHandler` is last-wins and `SetHandlerLifetime` is not, so a package that replaces the handler at registration time and disables rotation alongside it has built a trap. Connection settings are applied from an `IHttpMessageHandlerBuilderFilter`, which runs after every registration.

    The same applies to `HttpClient.Timeout`. `ConfigureHttpClient` is last-wins too, and two ordinary consumer
    actions beat it: a second platform handler put the timeout back to infinite (removing the response-body
    bound `Timeout:Client` exists for), and a plain `ConfigureHttpClient(c => c.Timeout = 2s)` truncated a
    30-second pipeline with a bare `TaskCanceledException` — the exact condition `ValidateTimeouts` refuses
    when the same value is written as `Timeout:Client`. It is applied from an
    `IPostConfigureOptions<HttpClientFactoryOptions>` instead, because every `IConfigureOptions` runs before
    every `IPostConfigureOptions`. A conflicting assignment in code now fails at client creation.

11. **Guards are written against this package's API, so the consumer boundary is where they leak.** Four defects
    in a row were found there, each silent and each passing a 319-test suite that had no consumer in it: a
    consumer's platform handler nesting a pipeline, a consumer's `ConfigureHttpClient` truncating the pipeline,
    a consumer's `PostConfigure` reaching a captured value, and two of this package's own features that could
    not be used together. `ConsumerBoundaryTests` is the standing axis for this — the same move
    `HedgingSafetyTests` made for "the origin is slow". When adding a guard, write the case where a consumer
    also does something to the same client, and assert the outcome at the origin or on the constructed client.

12. **A rule about inert configuration must judge the client's own section, not the bound value.** The bound
    value includes everything inherited from the root, and the root is shared. Judging it made a root
    `PipelineSelection:Authorities` — the mechanism a fleet uses to state one destination allow-list for its
    hedged clients — fail the registration of every standard client in the process, with a message naming the
    standard client's own section rather than the root the list was in. Two documented, individually tested
    features that could not be used together, because no test registered both kinds of client.
    `CollectInertConfiguration` reads the raw section for exactly this reason and now holds all four statedness
    rules; `ValidatePipelineSelection` holds none.

### Registration

```csharp
services.AddHttpResilience(configuration);              // once, first
// AddHttpResilience already creates every client at host start; ValidateClientsOnStart: false opts out
services.AddHttpClient("Orders").AddHttpResilience();  // reads HttpResilience:Clients:Orders
services.AddHttpClient("Search").AddHedgedHttpResilience();  // requires PipelineSelection:Authorities
```

A **typed** client is named by `IHttpClientFactory`, not by this schema: `AddHttpClient<IOrdersApi, OrdersApi>()` reads `Clients:IOrdersApi`, after `TClient`. Pass the section name explicitly rather than guessing it.

`AddHttpResilience(services, ...)` stores the root `IConfigurationSection` as a registered instance so the builder extension can read configuration while the service collection is still being built — handlers must be added at registration time, not resolve time.

### Pipeline shape (fixed)

```text
ConcurrencyLimiter (optional) → [ConcurrencyBackstop, only when displaced] → Limiter (ALWAYS PRESENT)
  → Total timeout → Retry → Circuit breaker → Attempt timeout → SocketsHttpHandler
```

`Timeout:Client` (`HttpClient.Timeout`) wraps all of it, including both limiter queues and the response-body
transfer. **`Timeout:Total` stops applying at response headers** — the handler chain returns there, and
`HttpClient` buffers the body afterwards. `AddStandardResilienceHandler` sets `HttpClient.Timeout` to infinite
itself (measured: a client with only that handler reports `-00:00:00.001`), so the finite value has to be
applied *after* the platform handler or it is dead code, which is what it was.

The standard handler's limiter slot is never empty. Unconfigured it holds a concurrency limiter of 1,000 with no queue, which is why `ConcurrencyLimiter:Backstop` exists: the number was a real control and it was invisible. Assigning a rate limiter *replaces* that slot, so the backstop is re-added as its own handler whenever a rate limiter is configured.

The invariant is that **a concurrency bound is never absent** -- not that the backstop is always the one providing it. When a client enables both a rate limiter and its own `ConcurrencyLimiter:Limit`, the backstop handler is skipped, because validation holds `Limit` at or below `Backstop` and it is therefore the tighter bound. The stronger claim was in three documents and was false in that one combination; `ConcurrencyBackstopTests.ConcurrencyBound_StillHolds_WhenBothLimitersAreEnabled` is what now holds the weaker one to account.

`IHttpClientFactory` composes `AdditionalHandlers[0]` as the **outermost** handler, so handlers are registered outermost-first. Getting this backwards is the 1.0 bug; there is a behavioral test pinning it.

Both limiters sit outside the total timeout — the platform's ordering, and what makes one permit cover a whole logical request. The consequence is that queue wait is not inside `Timeout:Total`; say so rather than repeating the tidier claim.

The hedging pipeline is a different shape: total timeout → hedging → per-endpoint concurrency limiter (platform default, 1,000) → per-endpoint circuit breaker → attempt timeout. Those endpoint pipelines are per authority and never evicted.

### Configuration

Root section `HttpResilience`; per-client overrides at `HttpResilience:Clients:{name}`, bound on top of the root (the binder only assigns keys that are present, so inheritance is free). Client sections are namespaced under `Clients` so a client named `Retry` cannot collide with the schema.

All durations are `TimeSpan`. `Enabled` is **opt-in, default false** — adding the package must not change an existing client's behavior. Because that state is indistinguishable from a forgotten key, `DisabledClientNotice` is an `IPostConfigureOptions<HttpResilienceOptions>` that logs a **Warning** when the host materializes the options under `ValidateOnStart`, i.e. at startup rather than on first client use. Never move it back to a `ConfigureHttpClient` hook. Resilience configuration is **restart-only**: options are registered with `Configure`, not `Bind`, so no reload token exists and `IOptionsMonitor` cannot report values that are not in effect.

### Validation

No data annotations — they cannot express `TimeSpan` ranges or cross-property relationships, they skip every non-default options name, and filtering them requires matching message text. All rules live in `HttpResilienceOptionsValidator`, and every message names the config path, the value, the expectation and the reason.

Runs twice on purpose: eagerly at registration (before any value builds a handler) and at startup via `ValidateOnStart` through `NamedPipelineOptionsValidator`, which knows whether the client uses the standard or hedging pipeline since their budget rules differ.

When adding a rule, prefer catching at startup anything the platform would throw on at the first live request.

Two rules are about the configuration *file* rather than a value, and run once against the root options at startup -- they cannot run eagerly at registration, because a section unread when the third client registers may be read by the fourth: **unused `Clients:{name}` sections** (escape hatch `AllowUnusedClientSections`, root-only, defaults to failing) and **renamed keys** (`Retry:MaxAttempts` is still bound, to a tombstone, so a stale file fails instead of binding to nothing). Whether a client can be *created* is not an options fact at all -- the handler chain is DI -- which is what `ClientStartupProbe` exists for. It is registered by `AddHttpResilience` and on by default; `ValidateClientsOnStart` (root-only, raw section, like `AllowUnusedClientSections`) opts out, and stating it `false` beside a `ValidateHttpResilienceClientsOnStart()` call fails startup rather than letting the call silently win.

### Key files

| File | Purpose |
| --- | --- |
| `Extensions/HttpResilienceServiceCollectionExtensions.cs` | `AddHttpResilience` — schema registration |
| `Extensions/HttpResilienceHttpClientBuilderExtensions.cs` | Per-client registration and handler ordering |
| `Internal/HttpResilienceOptionsValidator.cs` | All validation rules |
| `Internal/NamedPipelineOptionsValidator.cs` | Per-client startup validation |
| `Internal/StandardPipelineConfigurator.cs` | Maps options → `HttpStandardResilienceOptions` |
| `Internal/HedgingPipelineConfigurator.cs` | Maps options → `HttpStandardHedgingResilienceOptions` |
| `Internal/CircuitBreakerCallbacks.cs` | Breaker thresholds + state reporting (shared by both pipelines) |
| `Internal/CircuitBreakerStateTracker.cs` | Live state per (client, authority), the only thing the health check reads |
| `Internal/RateLimiterFactory.cs` | Builds each limiter from live options inside its keyed factory — rule 7's exception |
| `Internal/HttpMethodPredicates.cs` | The RFC 9110 safe-method allow-list, shared by retry and hedging |
| `Internal/PipelineKeySelector.cs` | Bounded authority keys for pipelines *and* state tracking |
| `Internal/SocketsHttpHandlerFactory.cs` | Applies `ConnectionOptions` to the client's final primary handler, and is careful about which properties it writes onto a handler it did not create |
| `Internal/ConnectionHandlerFilter.cs` | Runs that after every registration, so ordering cannot defeat it |
| `Internal/AuthorityIndex.cs` | Allocation-free authority matching |
| `Internal/HttpResilienceMeteringEnricher.cs` | The single `error.type` tag |
| `Internal/HttpResilienceRegistration.cs` | Root-section handoff and duplicate-registration guard |
| `Internal/UnsafeMethodNotice.cs` | The startup Warning for a client that can repeat a mutating request |
| `Internal/DisabledClientNotice.cs` | The startup Warning for a client registered with `Enabled: false` |
| `Internal/CircuitBreakerReachNotice.cs` | The startup Information saying how much traffic this breaker needs to open at all |
| `Internal/HttpResilienceLogging.cs` | Every source-generated message and its event ID |
| `Internal/RateLimiterKey.cs` | The limiter's DI key, a type this package owns so a consumer cannot collide with it |
| `Internal/AuthorityAllowListHandler.cs` | Bounds a hedged client's destinations |
| `Internal/StructuralDecisions.cs` | The few options a late change cannot reach, and why |
| `Internal/HttpResilienceMetrics.cs` | The two gauges Polly and `System.Net.Http` do not publish |
| `Internal/UnusedClientSectionValidator.cs` | Fails startup on a `Clients:{name}` section no client reads |
| `Internal/ClientStartupProbe.cs` | The default `IHostedService` that creates every configured client at host start |
| `Internal/ResilienceHandlerCountFilter.cs` | Reports a handler this package did not add, and records why it cannot fail instead |
| `Internal/HttpResilienceHealthCheck.cs` | Aggregate breaker state — `Degraded` at worst, never a probe |
| `Extensions/HttpResilienceHealthCheckExtensions.cs` | Registers that check, tagged as a dependency check |
| `Extensions/HttpResilienceTelemetryExtensions.cs` | The meter names to hand OpenTelemetry, plus the enricher |

### Log events

`HttpResilienceLogging` owns event IDs 1–13. The ones the rules above argue about: **6** disabled client
(Warning), **10** a client that can repeat a mutating request (Warning), **11** circuit breaker reach
(Information), **12** extra resilience handlers (Information), **13** a redirect bound that could not be
applied because the primary handler has no `AllowAutoRedirect` (Warning — see rule 9 for why it reports
rather than throwing). 7–9 are limiter rejections, at Warning because
all three surface as the same `RateLimiterRejectedException` on the same instrument and an operator cannot
otherwise tell the configured control from the invisible backstop. 1 and 5 (retry / hedging attempt) are Debug
because Polly already logs each attempt twice at Warning. Level is an argument in each case — read the comment
above the message before changing one.

## Testing

Tests assert **behavior**, not that registration does not throw. The 1.0 suite had 64 tests and caught none of the four critical defects, because they almost all asserted "did not throw".

- Layout: `Behavior/` is the bulk — one file per guarantee, wired through the public API; `Internal/` holds unit tests for the few types with logic of their own; `Options/ValidationTests.cs` holds the validator. `tests/HttpResilience.NET.IntegrationTests` runs against a real Kestrel, and `RedirectTests` / `Http2Tests` against real sockets — the reason CI has an OS matrix at all.
- Allocation ceilings are `[ReleaseOnlyFact]`: Debug codegen adds display classes and unelided state machines, so a ceiling that covered both configurations would be past what it exists to exclude. They skip with a reason rather than vanishing.
- `tests/.../Infrastructure/ResilienceHarness.cs` wires a real client through the public API with a counting primary handler. Origin call count is what distinguishes a correct pipeline from an inverted one.
- Prefer the stub handler over `TestServer` — exact counts, no timing dependency. Use `TestServer` only where real HTTP semantics matter.
- Never assert on sleeps where a deterministic mechanism exists. Polly's own timing is Polly's to test; test the reporting and configuration this package contributes.
- Any new guard against duplicate side effects needs a test that counts delivered bodies, not just status codes.
- **A test that cannot fail proves nothing.** Every hedging test used an origin that answered immediately, so none of them ever reached the timer path that duplicated POSTs. When adding a safety test, name the production change that would make it fail, then make that change and watch it fail.
- Claims about platform behavior get measured, not assumed. `Behavior/RemediationTests.cs` exists because a second review found nine defects that source reading had missed — metric names that do not exist, a pipeline registry that grows per authority, a registration that silently nests. Assert on what a `MeterListener`, an origin call count or the health-state dictionary actually reports.
- **"A consumer also does X to this client" is a standing test dimension**, in `Behavior/ConsumerBoundaryTests.cs`. It exists because a third review found four defects there in a row, all of which passed 319 tests: no test had a consumer in it. It is the same move `HedgingSafetyTests` made for "the origin is slow".
- **A test that pins a defect the package cannot prevent asserts the damage, not just the notice.** `AConsumersOwnStandardHandler_NestsTwoPipelines_AndSaysSo` asserts nine origin calls, because that number is quoted in README.md, ARCHITECTURE.md, TROUBLESHOOTING.md and RUNBOOK.md. If the arithmetic changes, the documents are wrong and this is what says so.
- **Guard arithmetic that mirrors a decision made elsewhere gets a test per combination.** The handler tally duplicates the conditions in three `Add*IfEnabled` methods and assumed the platform's hedging handler contributed one handler where it contributes two, which reported every hedged client in the suite as nested. `EveryPipelineShape_TalliesItsOwnHandlers` covers all eight shapes in both directions so a drift names its own cause.

## Deeper docs

`docs/`: `ARCHITECTURE.md`, `OPERATIONS.md` (amplification arithmetic), `RUNBOOK.md`, `RECIPES.md`, `TROUBLESHOOTING.md`, `SECURITY-GOVERNANCE.md`, `PRODUCTION-CHECKLIST.md`, `VERSIONING.md`.

`docs/benchmarks/` quotes numbers from the raw BenchmarkDotNet reports checked in beside it, so a claim can be
re-derived rather than trusted. They drifted once; `scripts/check-benchmark-docs.py` now compares them in CI.
Allocation only — deterministic, unlike a mean. Editing either the summary or a report without the other fails.

Telemetry the package owns is deliberately three `ObservableGauge`s and one metric tag. Anything else is
Polly's or `System.Net.Http`'s and must not be restated. A gauge is added only where a counter cannot answer
the operator's question ("is it open", not "did it open"); read per collection, never per request.

The two limiter gauges carry `http.resilience.limiter.kind` (`rate` / `concurrency` / `backstop`) rather than
splitting into separate instruments, because the operator's question is "how close is this client to shedding
load" and the answer is whichever limiter is nearest its bound. Only limiters this package holds an instance of
report: the undisplaced backstop lives in the platform's limiter slot where Polly builds it **per pipeline**,
which is what makes it per authority under `ByAuthority`, so supplying an instance to read its statistics would
convert a per-authority bound into a per-client one. That gap is documented in three places and must not be
closed by making the bound weaker.

Three facts that must stay in any documentation rewrite, because their absence in 1.0 was itself a finding: the rate limiter is process-local and cannot enforce a cluster-wide quota; retries multiply outbound load in every replica independently; the circuit breaker health check must never gate a liveness or readiness probe.
