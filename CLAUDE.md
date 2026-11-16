# CLAUDE.md

Guidance for Claude Code (claude.ai/code) in this repository. Rules and the reason for each, in a line or two.
History, incident narratives and review findings belong in CHANGELOG.md, not here or in code comments.

## Prerequisites

- .NET 10 SDK 10.0.404 or a later 10.0.4xx patch (`global.json`, `rollForward: latestPatch`): the floor is the
  current security release; raise it with each one. Solution: `HttpResilience.NET.slnx`.
- `nuget.config` clears defaults, adds nuget.org and `baps-apps-packages` (GitHub Packages), and maps
  `CodeStyle.NET` and `HttpResilience.NET` (the package validation baseline) to the latter; restoring or packing
  from a fresh clone needs a GitHub PAT for that source (`scripts/README.md`).
- `scripts/*.ps1` need PowerShell 7 (`pwsh`); they declare `#Requires -Version 7.0`.

## Commands

```bash
dotnet build -c Release
dotnet test -c Release            # the suite of record: Debug skips every [ReleaseOnlyFact] (allocation ceilings)
dotnet test -c Release -p:TestMinimumDependencies=true   # same suite on the declared minimum dependency versions
dotnet test --filter "FullyQualifiedName~UnsafeHttpMethodTests"
dotnet test -c Release --collect:"XPlat Code Coverage"   # Cobertura under TestResults/

dotnet run --project samples/HttpResilience.NET.Sample/
dotnet run --project benchmarks/HttpResilience.NET.Benchmarks -c Release -- --filter "*" --job medium --memory

dotnet pack src/HttpResilience.NET/
pwsh scripts/publish-package.ps1   # requires GITHUB_PAT and a clean tree (untracked files included); publishes
                                   # the committed <Version>, never edits it; tags the commit it built; runs
                                   # every release gate below (not a fresh benchmark run) and Release tests
                                   # first; signs with NUGET_SIGN_CERT_*, refuses to start without one
                                   # unless -AllowUnsigned;
                                   # overwrites an existing version and re-tags it (intended)

# Release gates, run locally (no CI for changes; the one workflow is the weekly platform-drift run).
dotnet format whitespace --verify-no-changes   # whitespace only: full `dotnet format` compiles without the binding
                                               # source generator and reports IL2026/IL3050 the real build never emits
dotnet list package --vulnerable --include-transitive
dotnet list src/HttpResilience.NET package --deprecated --include-transitive  # shipped project only: the tests
                                               # need the deprecated Polly.Extensions.Http (event 20's v7 stack)
python3 scripts/check-benchmark-docs.py        # docs/benchmarks figures vs the raw reports
python3 scripts/check-benchmark-docs.py --against <dir>/results  # vs a fresh benchmark run (--artifacts <dir>)
python3 scripts/check-public-api-shipped.py    # release builds: Unshipped must be empty
python3 scripts/check-readme-links.py          # README is the package page: absolute links only
dotnet pack src/HttpResilience.NET -c Release  # package validation against the last published version (needs the
                                               # PAT); intended breaks live in CompatibilitySuppressions.xml

# Proves IsTrimmable/IsAotCompatible at run time: a trimmed reflective binder does not throw, it leaves defaults.
# Publishes and runs the AotSmoke binary for this machine's RID; `dotnet test` runs the same SmokeScenarios
# under JIT every time (TheSmokeScenarios_PassUnderJit), so a broken check shows up before AOT is involved.
# Homebrew .NET SDK on macOS: the test appends Homebrew's lib directory to LIBRARY_PATH for the link (libssl).
HTTPRESILIENCE_AOT_SMOKE=1 dotnet test tests/HttpResilience.NET.IntegrationTests -c Release --filter "FullyQualifiedName~NativeAotSmokeTests"
```

Build each test project explicitly (`dotnet build tests/<project> -c Release`) before trusting a `--no-build` run:
a root `dotnet build` can leave a test assembly stale.

## Code conventions

- `TreatWarningsAsErrors`, `GenerateDocumentationFile`, nullable reference types, `net10.0`. `EnforceCodeStyleInBuild`
  in the library. `AnalysisLevel` pinned to `10.0-recommended` so a newer SDK cannot break an unchanged tree.
- Central package management (`Directory.Packages.props`). The library references dependencies at **minimum**
  versions (`ExtensionsMinimumVersion`, `HttpResilienceMinimumVersion`, `PollyMinimumVersion`, via `VersionOverride`)
  and references directly every package whose types it uses, never relying on a transitive one. Tests, samples and
  benchmarks run on the latest versions, and with `-p:TestMinimumDependencies=true` on the minimums: both runs are
  release gates, because the handler-group mechanism relies on undocumented factory behavior that could differ
  between them. Raise a minimum only when an API requires it, or when the minimum run fails and cannot be fixed.
- **Polly stays on 8.x: Polly 9 is commercially licensed.** The package declares `PollyVersionRange`
  (`[8.4.2, 9.0.0)`) on `Polly.Core`, `Polly.Extensions` and `Polly.RateLimiting` (`Polly.Extensions` 8.x alone
  accepts `Polly.Core` 9), tests pin the latest 8.x (`PollyTestVersion`, `Polly.Core` listed for transitive pinning),
  and `resolve-platform-versions.py` caps Polly at major 8 (`MAX_MAJOR`). Never lift the cap without a license
  decision.
- **Every public member is in `PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt`**; an unrecorded change is a build
  error (RS0016 quotes the line). Add to Unshipped in the same commit; moving it to Shipped is a release step.
- **Every public type starts with `HttpResilience` and lives in `HttpResilience.NET`** (extension classes in
  `Microsoft.Extensions.DependencyInjection`), so generic names cannot collide in consumer code and no
  sub-namespace hides `Microsoft.Extensions.Options.Options` (`PublicApiNamingTests`). Folders do not set
  namespaces: `Options/` and `Configuration/` hold root-namespace types.
- Breaking changes follow `docs/VERSIONING.md`: a new validation rule, a default change or a renamed key is MAJOR.
- **Public option types carry the schema and nothing else.** Facts recorded while composing an instance (a
  value stated before it was resolved, root-delegate failures, malformed startup switches) go in
  `OptionsComposition`, a weak table keyed by the instance, never in internal members of a public type. Values
  derived from options (`EffectiveClientTimeout`, `PermitKey`, `FollowsRedirects`) are extension methods in
  `OptionDerivations`. `PublicOptionTypes_CarryOnlyTheSchema` fails on any non-public member.
- **Every comment, line or XML doc, is brief, to the point and self-explanatory to a consumer, with no historical
  reference or note** ("used to", "now", "previously", "again", "was replaced", version or review history).
  State what holds and why; history goes in CHANGELOG.md.

## Architecture

The package **configures** `Microsoft.Extensions.Http.Resilience`. It does not implement retry, timeouts, circuit
breaking, rate limiting or connection pooling, and must not start to. Push back on any change that breaks a rule:

1. **Never own pipeline ordering.** `AddStandardResilienceHandler` / `AddStandardHedgingHandler` define it; this
   package supplies values and predicates only.
2. **Never copy a platform option type.** Map the schema onto Microsoft's options and stop.
3. **Every escape hatch is the platform's** (`AddResilienceHandler`). No custom hook: they bypass validation.
4. **Unsafe by request, never by default; per client is enforced.**
   - Only RFC 9110 safe methods (GET, HEAD, OPTIONS, TRACE) are repeated: an **allow-list**, unlike the platform's
     `DisableForUnsafeHttpMethods`, a deny-list that retries `MOVE`/`PURGE`/`PROPPATCH`. Request methods match
     **exactly** (methods are case-sensitive, so `get` is unknown); configured `RetryableMethods` are upper-cased.
   - The root may not switch it off for everyone: neither `DisableForUnsafeHttpMethods` may be `false` at the root,
     and a root `Retry:RetryableMethods` may list only safe methods (narrowing is allowed). Checked against the raw
     section (`RootSectionRules`), eagerly in `AddHttpResilience` and again per client, because root options are
     only validated where a host runs `IStartupValidator`. Root delegates from `AddHttpResilience(configure)` are
     held to the same rules (`CollectRootDelegateFailures`).
   - A client may not state `Retry:DisableForUnsafeHttpMethods` beside a `RetryableMethods` list in force, in either
     direction: the list replaces the flag. An empty list restores the safe-method guard under an inherited list.
   - `UnsafeMethodNotice` logs Warning event 10 at startup for every client that can repeat a mutating request.
5. **Every metric dimension is bounded at registration.** No tag value comes from request data, including what the
   platform emits for us: the hedging handler keys a pipeline per request authority, hence the required
   `Authorities` allow-list. The allow-list handler covers `Send` as well as `SendAsync`
   (`EveryHandlerThatOverridesSendAsync_AlsoOverridesSend`).
6. **One registration per client; an idempotent root registration.** A second `AddHttpResilienceHandler` or
   `AddHttpResilienceHedgingHandler` on a client throws (nested pipelines multiply retries). The ledger lives on the registered
   `HttpResilienceRegistration` instance, which `AddHttpResilience` must never replace. A second
   `AddHttpResilience` with a *different* section throws; the same path from another configuration object fails
   when options are built if its values differ (`RequireSection`); delegates from `AddHttpResilience(configure)`
   accumulate.
   - **Defaults' resilience handlers are replaced; the client's own earlier ones fail client creation**
     (`ReplaceEarlierResilienceHandlers`, event 17, Information), so .NET Aspire's `ConfigureHttpClientDefaults`
     standard handler needs no consumer call and no experimental API, and no code written for the client is
     deleted. Defaults actions run before a named client's whatever the registration order; a marker
     (`MarkWhereTheClientsOwnActionsBegin`) separates the two: inserted before the client's first earlier
     `ConfigureNamedOptions` naming it, or before the first factory/type registration after the platform's unnamed
     one (whose client cannot be read; too early fails loud, too late deletes unseen). Never before the platform's:
     defaults are applied there, so an earlier one would make the defaults' handler look like the client's. Handlers registered after this package's are kept, because that is
     where `AddResilienceHandler` adds a strategy the schema lacks. A disabled client keeps its own handlers but
     loses the defaults' resilience handlers (event 19): the platform's standard handler retries every method, so
     keeping it would make the kill switch repeat POSTs.
   - **One policy for conflicts with code written for a client: fail client creation** (the startup probe makes
     that host start): `HttpClient.Timeout`, a finite `SetHandlerLifetime` under `Connection:Enabled`, a
     connection setting on a supplied `SocketsHttpHandler` that differs from the resolved one (`ConflictingSettings`;
     the runtime default counts as unset), an earlier resilience handler, a later change to the platform options
     (`PlatformOptionsGuard`). Only what nobody wrote
     for the client (defaults) is replaced quietly.
   - **The pipeline takes the outermost replaced handler's slot** (`TakeTheReplacedHandlersPlace`), so handlers
     it wrapped stay wrapped: Aspire's service discovery resolves per attempt and the allow-list sees logical
     names (`AspireServiceDefaults_KeepServiceDiscoveryInsideThePipeline`). With nothing replaced, the resolver
     stays where it was registered (pinned by `ServiceDiscoveryWithoutADefaultsHandler_…`).
   - A consumer's bare `AddStandardResilienceHandler()` registered *after* this package's still nests (nine origin
     calls) and cannot be told apart from the supported `AddResilienceHandler` through public API without
     reflection that trim/AOT rule out. So `ResilienceHandlerCountFilter` reports at **Information** (event 12);
     never raise it to Warning without a sound discriminator. Its handler check is `is ResilienceHandler`.
7. **Options have one source of truth, held by construction.** Everything reads
   `IOptionsMonitor<HttpResilienceOptions>.Get(name)` when the platform builds the pipeline, after every
   `Configure`/`PostConfigure`, so the pipeline runs on the object a consumer reads back.
   - **The platform options are held to what the package wrote.** They are shared by every registration on the
     client (`{client}-standard`; `{client}` for hedging), so any later write would replace validated values,
     guard included. `PlatformOptionsGuard` records the written values per instance (last statement of each
     configurator; the hedging action generator after `SuppressUnsafeHedgedAttempts`) and validates both option
     types after every post-configure. Anything a configurator writes goes in its `Read` list.
   - **Nothing reads options per request.** A delegate the pipeline runs captures values when the pipeline is
     built, never the options instance: it is cached and mutable, so a later write would bypass validation
     (`MutatingTheOptionsAfterThePipelineIsBuilt_DoesNotTurnOffTheGuard`).
   - **Registration reads no configuration.** Anything built from options (every limiter, `BackstopLimiters`) goes in
     a factory that reads live options, never a closure over a registration-time snapshot.
   - **Which handlers exist is decided from live options too.** Every handler group is registered unconditionally by
     `AddHandlerGroup` between marker handlers; when the chain is built, an inactive group is removed, costing
     nothing per request. The platform builds a handler's pipeline at construction, so each group's pipeline
     delegate must add nothing when its predicate is false. A group's own `HttpClient` actions are bracketed by
     marker delegates and an inactive group's are removed from `HttpClientFactoryOptions` by a post-configure.
     Both markers missing means a consumer cleared the list: a no-op, not an error (`ConsumerBoundaryTests`).
     Not pass-through handlers instead: an empty `ResilienceHandler` adds 72 B and roughly a tenth of a standard
     pipeline's time per request (ad-hoc run, not in `docs/benchmarks`), and the platform handler's own group (kill
     switch, Aspire replacement) needs the markers anyway: its pipeline is reachable only through its handler.
   - **Accepted risk:** the groups rely on `HttpMessageHandlerBuilderActions` and `HttpClientActions` running in
     list order, which `IHttpClientFactory` does but does not document. The price of order-free registration and
     honoured consumer `Configure`/`PostConfigure`, not of reload (options are restart-only). A consumer removing
     or reordering entries fails client creation naming the list; removing the handlers logs event 16. If the
     platform ever changes the order, `ConsumerBoundaryTests` and `EveryPipelineShape_TalliesItsOwnHandlers` fail,
     and `.github/workflows/platform-drift.yml` runs them weekly on the newest stable release of the .NET major,
     the newest stable and the newest preview platform releases (`scripts/resolve-platform-versions.py`). A consumer can resolve a release before that run, so
     `PlatformSelfCheck` runs the mechanism (two canary clients under a defaults handler, in a private container)
     once per process from `ClientStartupProbe`, before any client, and fails host start naming the platform
     versions. It goes with the probe's switch (`ValidateClientsOnStart`); its gauges use a renamed meter.
     `AspNetCoreTestVersion` stays on the `global.json` .NET major: ASP.NET Core packages target only their own.
   - **`CreateClient` does no per-call options lookup or bookkeeping** (a typed client is created per request):
     handler lifetime and `Timeout:Client` are resolved once, when the factory options are built.
     `CreateClient_AllocatesNothingOverTheBareFactory` pins it.
   - Result: no call order between `AddHttpResilience` and client registration, and configuration sources added
     later are honoured. Never reintroduce a configuration read at registration.
8. **Prove a safety guarantee against the mechanism, not the option.** An option named like the guarantee is not
   the guarantee (`Hedging.ShouldHandle` does not stop the timer-started hedge). Drive the path where it would fail:
   a slow origin for hedging, real saturation for a limiter, a competing registration for the primary handler.
9. **A control is only as strong as its layer.** Redirects resolve inside `SocketsHttpHandler`, below every
   `DelegatingHandler`, so the allow-list bounds pipeline cardinality and `Connection:AllowAutoRedirect` bounds
   destinations. A hedged client resolves the flag to `false` and applies it even with `Connection:Enabled` off; a
   standard client keeps the runtime default. The runtime strips `Authorization` across redirects but re-sends custom
   headers (API keys).
   - It is never written onto a consumer's `SocketsHttpHandler` unasked: whether it was stated is recorded
     (`OptionsComposition`) before `BindEffective` resolves the value, and `ApplyRedirectBound` writes only a stated value or `false`.
     With `Connection:Enabled`, a consumer's `HttpClientHandler` is replaced by a new `SocketsHttpHandler` only
     when it differs from a new one in nothing (`CustomizedSettings`); otherwise, and for any other primary
     handler type, client creation fails. Its settings are never copied: the certificate callback signatures differ.
   - With `Connection:Enabled` off, when the primary handler has no `AllowAutoRedirect`, `TryDisableAutoRedirect`
     fails and Warning event 13 is logged. It reports rather than throws, so nobody learns to switch the bound off to make a test stub work.
   - `RedirectTests` uses real sockets (`TestServer` follows no redirects); both directions are in
     `ConsumerBoundaryTests`.
10. **Never own the primary handler by racing for it.** `ConfigurePrimaryHttpMessageHandler` is last-wins;
    connection settings are applied from an `IHttpMessageHandlerBuilderFilter`, after every registration.
    `HttpClient.Timeout` likewise is applied from an `IPostConfigureOptions<HttpClientFactoryOptions>` (every
    configure runs before every post-configure), and a conflicting assignment in code fails at client creation.
    The same post-configure sets an infinite handler lifetime under `Connection:Enabled`; a finite
    `SetHandlerLifetime` (anything but the factory default or infinite) fails client creation.
11. **Guards leak at the consumer boundary.** For every guard, write the case where a consumer also does something
    to the same client, and assert the outcome at the origin or on the constructed client
    (`ConsumerBoundaryTests`).
12. **Inert-configuration rules judge the client's own section, not the bound value**, which includes the shared
    root. `CollectInertConfiguration` reads the raw section and holds all four statedness rules;
    `ValidatePipelineSelection` holds none. Test with both kinds of client registered.

### Registration and composition

```csharp
services.AddHttpResilience(configuration);                          // or (options => ...), or both; order-free
services.AddHttpClient("Orders").AddHttpResilienceHandler();         // reads HttpResilience:Clients:Orders
services.AddHttpClient("Search").AddHttpResilienceHedgingHandler();  // requires PipelineSelection:Authorities
// AddHttpResilience also creates every client at host start; HttpResilience:ValidateClientsOnStart: false opts out
```

- A typed client is named by `IHttpClientFactory`: `AddHttpClient<IOrdersApi, OrdersApi>()` reads `Clients:IOrdersApi`.
  Pass the section name explicitly.
- `HttpResilienceRegistration` (created by whichever call comes first) holds the client ledger, the root section and
  the root delegates; all are read when options are built. Composition: root section → root delegates → client
  section → client `configure` → kill switch. A code-only app reads an empty root section. A client whose app never
  calls `AddHttpResilience` fails when its options are built, naming the call.

### Pipeline shape (fixed)

```text
ConcurrencyLimiter (optional) → [ConcurrencyBackstop, only with a rate limiter and no ConcurrencyLimiter] → Limiter (ALWAYS PRESENT)
  → Total timeout → Retry → Circuit breaker → Attempt timeout → SocketsHttpHandler
```

- `Timeout:Client` (`HttpClient.Timeout`) wraps everything, including limiter queues and the response body.
  `Timeout:Total` stops at response headers. The platform handler sets `HttpClient.Timeout` to infinite, so the
  finite value is applied after it.
- The limiter slot is never empty: the rate limiter if enabled, else the backstop (`ConcurrencyLimiter:Backstop`,
  1,000, no queue), supplied by `BackstopLimiters`, one instance per pipeline. On a standard client a rate limiter displaces the
  backstop into its own handler unless the client's own `ConcurrencyLimiter` (validated at or below the backstop) already
  bounds it. The invariant is that **a concurrency bound is never absent**
  (`ConcurrencyBound_StillHolds_WhenBothLimitersAreEnabled`).
- Both limiters sit outside the total timeout, so one permit covers a whole logical request and queue wait is
  **not** inside `Timeout:Total`.
- `AdditionalHandlers[0]` is the outermost handler, so handlers are registered outermost-first (pinned by a test).
- Hedging: allow-list → [concurrency cap, rate limiter] → total timeout → hedging → per-endpoint concurrency limiter
  (the backstop, per authority, event 7 and gauge) → per-endpoint circuit breaker → attempt timeout. Endpoint
  pipelines are per authority and never evicted.

### Configuration

- Root `HttpResilience`; per-client overrides at `HttpResilience:Clients:{name}`, bound on top of the root (the
  binder assigns only present keys). Namespaced under `Clients` so a client named `Retry` cannot collide with the
  schema. Lists are replaced, not merged (`ResetListsStatedBy`).
- All durations are `TimeSpan`. `Enabled` defaults to `true`: calling `AddHttpResilienceHandler` is the opt-in. A
  client's `Enabled: false` turns it off; the root `Enabled: false` is a configuration-only **kill switch** applied
  last in `Bind`. Warning event 6 names the key.
- **Restart-only:** options are registered with `Configure`, not `Bind`, so no reload token exists.
- **Startup notices (6, 10, 11, 14, 15, 18, 21) are logged only by `ClientStartupReport`** (`IHostedLifecycleService.
  StartingAsync`, after validation, before any `StartAsync`), from final options. Each notice is a static function
  of `(client, options)`, plus the raw root section where it must name the key (6, 10); 18 is once per application
  (`SchemaVersionNotice`); add a new one to the report. Never log a notice from the options pipeline (it runs before
  later post-configures and once per `IOptionsSnapshot` scope). A bare `ServiceProvider` without a host gets none.

### Validation

- No data annotations: they cannot express `TimeSpan` ranges or cross-property rules and skip named options. Rules
  live in `HttpResilienceOptionsValidator`, `NamedPipelineOptionsValidator` (per client: pipeline rules, inert
  configuration against the raw client section, root rules), `RootSectionRules`, `RenamedKeyRules`,
  `UnknownKeyRules` and `UnusedClientSectionValidator`. Each rule lives in one place.
- **A pipeline's own section is judged only where that pipeline runs**: `Retry` for standard clients, `Hedging`
  for hedged ones, neither for the root alone, so a value no registered client reads is inert. A disabled retry's
  schedule is neither validated nor written onto the platform; its method rules still apply.
- Runs when a client's options are first built: at host start via `ValidateOnStart`, else at first client creation.
- Messages name the configuration key with `:` (`HttpResilience:Clients:Orders -- Timeout:Attempt: value ...`), the
  value, the expectation and the reason. Only root-delegate failures name C# properties (the value was set in code).
- Catch at startup anything the platform would reject later. Platform limits are mirrored as constants in
  `HttpResilienceOptionsValidator`: timeouts 10 ms–1 day, breaker windows ≤ 1 day, `HttpClient.Timeout` /
  `ConnectTimeout` ≤ `int.MaxValue` ms. `ThePlatformsLimits_AreAccepted_AtStartupAndByThePipeline` keeps the mirror
  no stricter.
- An unused `Clients:{name}` section fails once, against the root options (`UnusedClientSectionValidator`;
  `AllowUnusedClientSections` opts out). `Retry:MaxAttempts` in the raw root or the client's own section fails that
  client's validation, naming `Retry:MaxRetries` (`RenamedKeyRules`, called by `NamedPipelineOptionsValidator`).
  A key the schema does not have, in the raw root or the client's own section, fails that client the same way
  (`UnknownKeyRules`, schema read from the option types; `AllowUnknownKeys` opts out). **Except** under a root
  `SchemaVersion` naming a newer minor of this major (`SchemaVersionRules`): a minor only adds optional keys, so a
  file rolled out ahead of a service is Warning event 18, not a failure, while a typo in a file written for this
  version still fails. A newer major or a malformed value fails startup (validated with the startup switches).
  Never make unknown keys a warning by default: the typo case (`Retry:Enable: false` during an incident) is the
  reason the check exists.
  `ClientStartupProbe` creates every client at host start (`ValidateClientsOnStart: false` opts out).
- The three startup switches live on `HttpResilienceStartupOptions`: read from the root by hand
  (`StartupSwitches`, so a malformed value is a validation failure) as each instance's starting value
  (`StartupSwitches.Factory`, an `OptionsFactory.CreateInstance` override), so a consumer's `Configure` wins
  whatever the order without reordering the service collection; rejected in a client section as unknown keys.

### Telemetry and logs

- Meter `HttpResilience.NET` (versioned): three `ObservableGauge`s, `http_resilience.circuit_breaker.state`,
  `http_resilience.limiter.available_permits` and `.queued_requests`, read per collection, never per request. The
  breaker gauge has OpenTelemetry's state-metric shape: three series per breaker, tagged
  `http_resilience.circuit_breaker.state` (`closed`/`open`/`half_open`), 1 for the current state; every breaker
  the registered clients can have (`PipelineKeySelector.BreakerKeys`) is reported closed from the first
  collection. Limiter
  gauges carry `http_resilience.limiter.kind` (`rate`/`concurrency`/`backstop`) and, on per-authority backstops,
  `http_resilience.authority` plus `server.address`/`server.port`, as the breaker gauge does. The gauges are
  published by an explicit `HttpResilienceMetrics.Publish()` from `ResilienceHandlerCountFilter`, before the first
  handler chain is built; never rely on resolving the service for its side effect. Plus one tag on Polly's metrics, `error.type` for exceptions
  (`AddHttpResilienceTelemetry`). Anything else is Polly's or `System.Net.Http`'s; do not restate it. Add a gauge
  only where a counter cannot answer ("is it open", not "did it open"). Names use `http_resilience.*`, never
  OpenTelemetry's reserved `http.*`.
- Only limiters this package holds an instance of can report, so `BackstopLimiters` owns the backstop **per
  pipeline key**, never per client; one shared instance would turn a per-authority bound into a per-client one
  (`Backstop_IsPerAuthority_UnderByAuthoritySelection`, `HedgedBackstop_IsPerAuthority`).
- `HttpResilienceLogging` owns events 1–21. Categories: `HttpResilience.NET.Configuration` (6, 10–21),
  `.Pipeline` (1–5), `.Limiter` (7–9 from this package's own limiter handlers). 7 and 8 raised from the platform
  handler's limiter slot (a standard client's rate limiter or backstop, a hedged client's per-endpoint backstop) use
  the configurators' `.Pipeline` logger.
  - 1, 5 (retry / hedging attempt): Debug, because Polly already logs each attempt at Warning.
  - 2–4: breaker opened (Warning), half-open and closed (Information).
  - 6 disabled client, 10 unsafe methods repeatable, 13 redirect bound not applied, 14 retry schedule longer
    than `Timeout:Total` (a Warning, not a failure: a total timeout used as a cap is valid), 21 `Hedging:Delay`
    below 10 ms (`HedgingFanOutNotice`; nearly every request fans out, valid for a dependency sized for it):
    Warning.
  - 7–9 limiter rejections: Warning, because all surface as the same exception; throttled by
    `RejectionLogThrottle` to one per limiter per 10 s with `SuppressedCount`.
  - 11 breaker reach, 12 extra resilience handlers, 15 client summary, 17 earlier resilience handlers replaced,
    19 defaults' resilience handlers removed from a disabled client: Information.
  - 20 a Polly v7 `PolicyHttpMessageHandler` on an enabled client (matched by type name; the package does not
    reference `Microsoft.Extensions.Http.Polly`): Warning, not a failure, because a v7 timeout-only policy
    cannot be told from a retry.
  - 18 keys ignored from a newer-minor `SchemaVersion` file: Warning, once per application.
  - 16 this package's handlers removed by a later registration: Warning. Followed by identity
    (`PackageHandlers`, marked when a group is kept), so a consumer's handler in their place does not hide it.

### Key files

| File | Purpose |
| --- | --- |
| `Extensions/HttpResilienceServiceCollectionExtensions.cs` | `AddHttpResilience`: configuration, code, or both |
| `Extensions/HttpResilienceHttpClientBuilderExtensions.cs` | Per-client public API, `AddStandard` / `AddHedging` |
| `Extensions/HttpResilienceHttpClientBuilderExtensions.HandlerChain.cs` | Handler groups, markers, defaults replacement |
| `Extensions/HttpResilienceHttpClientBuilderExtensions.Registration.cs` / `.Limiters.cs` / `.Binding.cs` | Options and client settings; limiter handlers; root → client composition |
| `Extensions/HttpResilienceTelemetryExtensions.cs` | Meter names for OpenTelemetry, plus the enricher |
| `Internal/HttpResilienceRegistration.cs` | Client ledger, root section, root delegates |
| `Internal/HttpResilienceOptionsValidator.cs` | Value rules, platform limits |
| `Internal/NamedPipelineOptionsValidator.cs` | Per-client validation, inert configuration |
| `Internal/RootSectionRules.cs` | Root unsafe-method rules (raw section and root delegates), kill switch |
| `Internal/RenamedKeyRules.cs` / `UnknownKeyRules.cs` / `UnusedClientSectionValidator.cs` | Renamed and unknown keys (per client); unused client sections (root) |
| `Internal/StartupSwitches.cs` | `HttpResilienceStartupOptions` from the root section; malformed values |
| `Internal/StandardPipelineConfigurator.cs` / `HedgingPipelineConfigurator.cs` | Options → platform options |
| `Internal/PlatformOptionsGuard.cs` | Fails a client whose platform options changed after the package wrote them |
| `Internal/BackstopLimiters.cs` | The backstop in the platform's limiter slot, one owned limiter per pipeline |
| `Internal/RateLimiterFactory.cs` / `RateLimiterKey.cs` | Limiters from live options; a package-owned DI key |
| `Internal/CircuitBreakerCallbacks.cs` / `CircuitBreakerStateTracker.cs` | Breaker thresholds, state per (client, authority) |
| `Internal/HttpMethodPredicates.cs` | The RFC 9110 safe-method allow-list |
| `Internal/PipelineKeySelector.cs` / `AuthorityIndex.cs` | Bounded, allocation-free authority keys |
| `Internal/AuthorityAllowListHandler.cs` | Bounds a hedged client's destinations (`Send` and `SendAsync`) |
| `Internal/SocketsHttpHandlerFactory.cs` / `ConnectionHandlerFilter.cs` | Connection settings on the final primary handler |
| `Internal/ResilienceHandlerCountFilter.cs` / `PackageHandlers.cs` | Events 12 and 16; which handlers are this package's |
| `Internal/HttpResilienceMetrics.cs` / `HttpResilienceMeteringEnricher.cs` | The three gauges; the `error.type` tag |
| `Internal/HttpResilienceLogging.cs` / `RejectionLogThrottle.cs` | Every log event; rejection throttling |
| `Internal/ClientStartupReport.cs` + `*Notice.cs` | Startup notices 6, 10, 11, 14, 15, 21 |
| `Internal/ClientStartupProbe.cs` / `PlatformSelfCheck.cs` | Creates every client at host start, after checking the platform's action order |
| `Failures/HttpResilienceFailure.cs` | Failure kind from an exception chain; a new exception the pipeline can throw needs a kind and a real-pipeline case in `FailureClassificationTests` |

## Testing

Tests assert **behavior** (origin call counts, delivered bodies, gauge readings), never just "did not throw".

- `Behavior/`: one file per guarantee, through the public API. `Internal/`: units with logic of their own.
  `Options/ValidationTests.cs`: the validator. Integration: `EndToEndTests` uses `TestServer`; `RedirectTests` and
  `Http2Tests` use real Kestrel sockets.
- `ResilienceHarness` wires a real client with a counting `RecordingHandler` (which also supports sync `Send`).
  Prefer it to `TestServer`: exact counts, no timing dependency.
- No sleeps where a deterministic mechanism exists: `RecordingHandler.WaitForCountAsync`, and `FakeTimeProvider`
  via `ResilienceHarness.Create(timeProvider:)`. Tests proving something *never* happens (hedging safety) keep a
  real slow origin with a wide margin.
- **Wait for a condition, never for quiet.** "Nothing changed for N ms" flakes on a loaded agent. Wait until
  every request is accounted for (at the origin, completed, or on the `queued_requests` gauge), bounded, so a
  missing control fails the assertion instead of stalling (`ConcurrencyBackstopTests.AllAccountedForAsync`).
- **Every test has `Timeout = TestTimeouts.Default`** (`EveryTest_HasATimeout`), and xUnit1051 is on: pass
  `TestContext.Current.CancellationToken`, so a timeout stops the work as well as failing the test. xUnit1069 is
  off (it demands the token in tests with nothing to cancel). Release anything a test holds in `finally`, or a
  failing assertion turns into a 30 s timeout.
- Allocation ceilings are `[ReleaseOnlyFact]`; they skip in Debug.
- xunit v3 through VSTest (`IsTestingPlatformApplication=false`) so `--filter` works. Polly's own timing is Polly's
  to test; test what this package contributes.
- **A test that cannot fail proves nothing.** For each safety test, make the production change that should break
  it, watch it fail, revert. Guards against duplicate side effects count delivered bodies.
- Measure platform behavior (`MeterListener`, origin counts, the breaker gauge); do not assume it.
- A test pinning damage the package cannot prevent asserts the damage: nine origin calls
  (`AConsumersOwnStandardHandler_NestsTwoPipelines_AndSaysSo`). Docs quote that number.
- Guard arithmetic that mirrors a decision made elsewhere gets a test per combination
  (`EveryPipelineShape_TalliesItsOwnHandlers`: all eight shapes, against the `AddHandlerGroup` predicates).

## Docs

`docs/`: ARCHITECTURE, CONFIGURATION, OPERATIONS (amplification arithmetic, instruments, alerts), RUNBOOK, RECIPES,
TROUBLESHOOTING, SECURITY-GOVERNANCE, PRODUCTION-CHECKLIST, VERSIONING. `docs/benchmarks/` quotes allocation figures
from raw BenchmarkDotNet reports beside it; `check-benchmark-docs.py` fails if either side changes alone, and with
`--against` if a fresh run disagrees (the two hedged rows depend on scheduling: 10% tolerance, `VARIABLE`). Compare
timings only within one run. CONFIGURATION.md's table of differences from `AddStandardResilienceHandler` /
`AddStandardHedgingHandler` is pinned, both columns, by `PlatformDefaultsComparisonTests`: change a default and
the table together, and never quote a platform default without measuring it there.

Three facts must survive any documentation rewrite: the rate limiter is process-local and cannot enforce a
cluster-wide quota; retries multiply outbound load in every replica independently; circuit breaker state must never
gate a liveness or readiness probe.
