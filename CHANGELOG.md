# Changelog

All notable changes to **HttpResilience.NET** are documented in this file.

This project follows [Semantic Versioning](https://semver.org/). See [docs/VERSIONING.md](docs/VERSIONING.md)
for what counts as MAJOR, MINOR and PATCH here.

## [3.0.0] - 2026-11-14

### Breaking

Changes against 2.0.0. The first nine change behavior **without a compile error** and need configuration,
dashboard, query or `catch` updates; code changes below them fail to compile with an obvious replacement.
2.x gets security fixes until 2027-05-14, so services can migrate on their own schedule
([SECURITY.md](SECURITY.md#supported-versions)).

- **`Enabled` defaults to `true`.** Calling `AddHttpResilienceHandler` is the opt-in; a client with no
  `Enabled` key now gets the pipeline. `HttpResilience:Clients:{name}:Enabled: false` turns one client off.
  The root `HttpResilience:Enabled: false` is a kill switch that overrides every client section and
  `configure` delegate. Event 6 names the key that disabled the client.
- **Telemetry names moved out of OpenTelemetry's reserved `http.*` namespace:** instruments are
  `http_resilience.circuit_breaker.state`, `http_resilience.limiter.available_permits` and
  `http_resilience.limiter.queued_requests`; tags are `http_resilience.client.name`,
  `http_resilience.authority` and `http_resilience.limiter.kind`. Update dashboards and alerts.
  **The breaker gauge changed shape too:** instead of one series valued 0/1/2, each breaker has three series
  tagged `http_resilience.circuit_breaker.state` = `closed`, `open` or `half_open`, valued 1 for its current
  state (OpenTelemetry's state-metric convention), and every breaker is reported, as closed, from the first
  collection rather than after its first transition. An alert on `state > 0` becomes
  `state{http_resilience.circuit_breaker.state="open"} == 1`.
- **Log categories are `HttpResilience.NET.Configuration`, `HttpResilience.NET.Pipeline` and
  `HttpResilience.NET.Limiter`** instead of `HttpResilience`. Update `Logging:LogLevel` entries and log
  queries.
- **A hedged client refuses an unlisted authority with `InvalidOperationException`**, not
  `HttpRequestException`. Retry predicates, the platform's default included, treat every
  `HttpRequestException` as transient, so a consumer's own retry around the client repeated a refusal that no
  retry can fix (three times under a default `HttpRetryStrategyOptions`). A `catch (HttpRequestException)`
  that relied on catching it must catch `InvalidOperationException` too. `HttpClient` throws the same type
  for a request it cannot send.
- **Retry waits are capped by the new `Retry:MaxDelay`, default 5 seconds.** A computed backoff is cut to it,
  and a `Retry-After` longer than it now **ends the retries**: the origin's 429 or 503 is returned to the caller
  at once. In 2.0.0 any `Retry-After` was waited for, uncapped, holding the caller and its limiter slot until
  `Timeout:Total`. Raise `Retry:MaxDelay` (and `Timeout:Total`) for a dependency that asks for longer waits.
- **Resilience handlers registered on a client before this package's are replaced**, so .NET Aspire
  ServiceDefaults work unchanged: the standard handler `ConfigureHttpClientDefaults` adds no longer nests outside
  this package's (twelve origin calls for one failing GET in 2.0.0), with no `RemoveAllResilienceHandlers()`
  (experimental, `EXTEXP0001`) needed. This package's pipeline takes the outermost replaced handler's place, so
  the service discovery ServiceDefaults add inside it stays inside: each retry and hedged attempt is resolved
  again, and the authority allow-list sees the logical name (appended after the defaults instead, retries went
  to one resolved endpoint and a hedged client refused the resolved address). New event 17 (Information)
  reports it once per client. Only defaults are replaced: a resilience handler registered **on the client itself**
  before `AddHttpResilienceHandler` (`AddResilienceHandler` or `AddStandardResilienceHandler`) now **fails client
  creation**, naming the fix (move `AddResilienceHandler` after it, where the documentation has always put it),
  instead of being deleted. A disabled client (its own `Enabled: false` or the root kill switch) keeps the
  handlers registered on it in code but loses the defaults' resilience handlers, reported once by new event 19
  (Information): kept, Aspire's standard handler, which retries every method, delivered a failing POST four
  times as soon as the kill switch was turned on. Event 12 now covers only handlers registered after this
  package's.
- **The circuit breaker defaults changed: `CircuitBreaker:MinimumThroughput` 100 → 20 and `FailureRatio`
  0.1 → 0.5** (window 30 s and break 5 s unchanged). The old values, the platform's, need 3.3 attempts per
  second in one replica before a breaker can open, so most internal clients never had a working breaker. Now
  the 20th attempt in 30 s opens it when half of them failed: about seven fully failing requests with the
  default retries, while a steady low error rate that retries absorb leaves it closed. A breaker can now open
  where it never did: check alerts on `http_resilience.circuit_breaker.state` and callers' handling of
  `BrokenCircuitException`. To keep the old behavior, set `CircuitBreaker:FailureRatio: 0.1` and
  `MinimumThroughput: 100` at the root.
- **Code that changes a client's platform resilience options after this package wrote them fails client
  creation** (and so host start), naming each changed property. The platform shares
  `HttpStandardResilienceOptions` `{client}-standard` (hedging: `HttpStandardHedgingResilienceOptions` `{client}`)
  between every registration on the client, so `AddStandardResilienceHandler(configure)`, a `Configure`,
  `PostConfigure` or `ConfigureAll` of those options, or a second `AddStandardHedgingHandler` silently replaced
  the validated pipeline, including the safe-method guard: one POST reached the origin up to seven times, and a
  hedged POST on a slow origin delivered three bodies. Move such values to the client's section or
  `AddHttpResilienceHandler(configure)`; add strategies with `AddResilienceHandler`.
- **With `Connection:Enabled`, a finite `SetHandlerLifetime` on the client fails client creation** instead of
  being silently overwritten: the package turns handler rotation off and `Connection:PooledConnectionLifetime`
  bounds connection age. Remove the call, or set `Connection:Enabled: false` to keep rotation. An infinite
  lifetime is accepted. Conflicts with code written for a client now all fail the same way, at host start through
  the startup client check: `HttpClient.Timeout`, the handler lifetime and an earlier resilience handler.
- **Per-client methods renamed** to say what they add, matching the platform's `Add…Handler` naming:
  `AddHttpResilience()` on `IHttpClientBuilder` is now `AddHttpResilienceHandler()`, and
  `AddHedgedHttpResilience()` is now `AddHttpResilienceHedgingHandler()`. `services.AddHttpResilience(configuration)`
  is unchanged.
- **The health check is removed:** `services.AddHttpResilienceHealthChecks()`,
  `AddHealthChecks().AddHttpResilience()` and `HttpResilienceHealthCheckExtensions.DependencyTag`. Service
  probes belong to HealthChecks.NET, and breaker state is the `http_resilience.circuit_breaker.state` gauge.
  The package no longer depends on `Microsoft.Extensions.Diagnostics.HealthChecks`.
- **Optional parameters replaced by overloads**, so adding a parameter later does not break compiled
  callers. The `clientName` parameter is now `sectionName`, which is what it always selected. Every argument
  is required where it appears: the default is the overload without it, so `null` is refused (a literal
  `AddHttpResilienceHandler(null)` does not compile; call `AddHttpResilienceHandler()`).
- **An empty or blank `sectionName` is refused** with `ArgumentException` at registration. `string.Empty`
  meant "root values only", which a client without a `Clients:{name}` section of its own already reads; drop
  the argument.
- **`RetryOptions.RetryableMethods` and `PipelineSelectionOptions.Authorities` are get-only `IList<string>`
  properties, never null** (empty by default). In code, use `Add` / `Clear` instead of assigning a list. Configuration
  is unchanged, including a client stating an empty list to drop an inherited one.
- **Every public type starts with `HttpResilience` and lives in `HttpResilience.NET`.** The
  `HttpResilience.NET.Options` and `HttpResilience.NET.Configuration` namespaces are gone: replace
  `using HttpResilience.NET.Options;` and `using HttpResilience.NET.Configuration;` with `using HttpResilience.NET;`.
  Unprefixed names collided with types of the same name in other libraries (`Azure.Core.RetryOptions`,
  `System.Management.ConnectionOptions`, `Microsoft.AspNetCore.RateLimiting.RateLimiterOptions`,
  `System.Threading.RateLimiting.ConcurrencyLimiterOptions`), so a file importing both failed with `CS0104`; and a
  namespace named `Options` hid `Microsoft.Extensions.Options.Options`. Renamed:

  | 2.0.0 | 3.0.0 |
  | --- | --- |
  | `RetryOptions` | `HttpResilienceRetryOptions` |
  | `TimeoutOptions` | `HttpResilienceTimeoutOptions` |
  | `CircuitBreakerOptions` | `HttpResilienceCircuitBreakerOptions` |
  | `ConnectionOptions` | `HttpResilienceConnectionOptions` |
  | `HedgingOptions` | `HttpResilienceHedgingOptions` |
  | `RateLimiterOptions` | `HttpResilienceRateLimiterOptions` |
  | `ConcurrencyLimiterOptions` | `HttpResilienceConcurrencyLimiterOptions` |
  | `PipelineSelectionOptions` | `HttpResiliencePipelineSelectionOptions` |
  | `PipelineSelectionMode` | `HttpResiliencePipelineSelectionMode` |
  | `RetryBackoffType` | `HttpResilienceRetryBackoffType` |
  | `RateLimitAlgorithm` | `HttpResilienceRateLimitAlgorithm` |

  Only code that names the types changes. The `HttpResilienceOptions` properties (`Retry`, `Timeout`, ...), enum
  member names, every configuration key, the package ID, the meter name and the log categories (including
  `HttpResilience.NET.Configuration`, which was never the namespace's) are unchanged.
- **`RetryOptions.MaxAttempts` is removed.** `Retry:MaxAttempts` in configuration still fails startup with a
  message naming `Retry:MaxRetries`; it is now detected in the raw section.
- **`ValidateHttpResilienceClientsOnStart()` is removed.** Clients are already created at host start by
  default; `HttpResilience:ValidateClientsOnStart: false` remains the only switch.
- **A configuration key the schema does not have fails startup**, at the root or in a client section, naming
  the key and the nearest real one. A typo such as `Retry:Enable: false` used to bind to nothing and leave
  retries on. For a file shared by services on different versions of this package, state the version it was
  written for as `HttpResilience:SchemaVersion` (`"3.1"`): on a service with an older 3.x the keys it does not
  know are a startup Warning (new event 18) listing them, and a typo in a file written for its own version still
  fails. A `SchemaVersion` naming a newer major, or that is not a version, fails startup.
  `HttpResilience:AllowUnknownKeys: true` turns the check off entirely. A startup switch or `SchemaVersion` stated
  in a client section is reported this way too.
- **`ConcurrencyLimiter:Backstop` above 10,000 fails startup** (it accepted any value from 1). The backstop is
  the concurrency bound that always exists; a value such as `int.MaxValue` left none. Because
  `ConcurrencyLimiter:Limit` may not exceed the backstop, it is capped at 10,000 too. Lower the value, or split a
  client that needs more concurrency per authority (`PipelineSelection:Mode: ByAuthority`).
- **With `Connection:Enabled`, a configured `HttpClientHandler` fails client creation instead of being
  replaced.** It used to be swapped for a new `SocketsHttpHandler`, silently dropping a client certificate
  (mTLS), a certificate-validation callback (pinning), a proxy, credentials, cookies and every other setting.
  The message names each setting found; move them to a `SocketsHttpHandler` (TROUBLESHOOTING.md). An
  `HttpClientHandler` with nothing set is still replaced, and is now disposed.
- **With `Connection:Enabled`, a supplied `SocketsHttpHandler` that sets `ConnectTimeout`,
  `PooledConnectionIdleTimeout`, `PooledConnectionLifetime`, `EnableMultipleHttp2Connections` or (when the key is
  stated) `MaxConnectionsPerServer` to a value other than the resolved one fails client creation** instead of
  being silently overwritten. The message names each key. Move the value to the client's `Connection` section,
  or set `Connection:Enabled: false`. A value left at the runtime default is still configured.

### Changed

- **Polly is capped below 9.0.0** (`Polly.Core`, `Polly.Extensions` and `Polly.RateLimiting` `[8.4.2, 9.0.0)`,
  previously `>= 8.4.2`, with `Polly.Core` only transitive), because Polly 9 is commercially licensed. Another
  package that requires Polly 9 now fails restore (NU1107) instead of upgrading silently. A Polly 9 reference in
  the application itself still wins with a warning (NU1608); set `WarningsAsErrors` for NU1608 to block it.
- **New `HttpResilienceStartupOptions`** holds the startup switches `ValidateClientsOnStart`,
  `AllowUnusedClientSections` and the new `AllowUnknownKeys`. They are still read from the same root keys, and can
  now be set in code with `services.Configure<HttpResilienceStartupOptions>(...)`, which wins over configuration in
  either registration order (configuration is each instance's starting value, read in an `OptionsFactory`; the
  package does not reorder the service collection). A malformed `ValidateClientsOnStart` now fails startup validation
  (`OptionsValidationException`) instead of throwing from the client probe.

- **New startup rules mirror the platform's own limits**, so a value the platform would reject fails at startup
  naming the key rather than when the client is first built: `Timeout:Attempt` and `Timeout:Total` between
  10 ms and 1 day, `CircuitBreaker:SamplingDuration` and `BreakDuration` at most 1 day, `Timeout:Client` and
  `Connection:ConnectTimeout` at most `int.MaxValue` milliseconds. Configuration outside them never worked, but it
  used to fail later.
- **Validation messages name configuration keys with `:`** (`HttpResilience:Clients:Orders -- Timeout:Attempt:`),
  matching the key an operator edits; they used `Timeout.Attempt`. Update any alert or log query that matches
  on the old text. A root `DisableForUnsafeHttpMethods: false` or unsafe root `RetryableMethods` entry is now
  reported once, not twice.
- **Startup notices are logged once, as the host starts, from each client's final options.** Events 6, 10 and
  11, and the new 14 and 15, run after options validation and after every consumer `Configure`/`PostConfigure`, so a
  `PostConfigure` that allows POST is reported and one that re-enables a client is not reported as disabled.
  Invalid configuration fails startup with no notice logged first, and building options per scope logs
  nothing. A bare `ServiceProvider` without a host logs no startup notices.
- **New event 15 (Information):** one line per enabled client with its effective settings.
- **New event 16 (Warning):** a registration made after `AddHttpResilienceHandler` removed resilience handlers this
  package added (typically `RemoveAllResilienceHandlers()` called after it instead of before), leaving the client
  without its pipeline. Logged once per client at the first chain build. Followed by handler identity, so a
  consumer's own handler in their place does not hide it.
- **The `HttpResilience.NET` meter carries the package version**, so exporters can split series by version.
- **A retry schedule longer than `Timeout:Total` no longer fails startup.** It logs one Warning per client
  (event 14) with the worst-case duration. A total timeout used as a hard cap is valid; the platform's own
  defaults are configured that way. The rule's 1.5x jitter allowance is gone: the estimate is the nominal
  schedule.
- **Limiter rejection logs (events 7, 8, 9) are throttled** to one entry per limiter every 10 seconds, with a
  `SuppressedCount` of the rejections since the previous entry. Messages for events 6–13 are shorter; their
  event IDs, levels and placeholders other than the new `SuppressedCount` are unchanged.
- **Dependencies are declared at their minimum versions** (`Microsoft.Extensions.*` 10.0.0,
  `Microsoft.Extensions.Http.Resilience` 10.0.0, Polly 8.4.2) instead of the latest patch, so adopting the
  package does not force upgrades.
- XML documentation rewritten to be brief and consumer-focused.
- **Registration no longer reads configuration, so call order no longer matters.** A client may be
  registered before `services.AddHttpResilience(configuration)`, and configuration sources added after
  registration are honoured. Every handler is registered and kept or removed from live options when the
  client's handler chain is built, so a late `Configure`/`PostConfigure` of `Enabled`, `RateLimiter:Enabled`,
  `ConcurrencyLimiter:Enabled`, `PipelineSelection:Mode` or `Connection:Enabled` now takes effect instead of
  failing startup. Invalid client configuration fails at host start (or first client creation without a
  host) rather than in the registration call; a missing `services.AddHttpResilience` call is reported the same
  way.

### Added

- **New startup Warning event 21: a hedging delay below 10 ms.** A hedged client whose `Hedging:Delay` is
  shorter than any network round trip sends nearly every request `MaxHedgedAttempts` + 1 times at once, even when
  the dependency is healthy (up to 11 with `MaxHedgedAttempts: 10`). The message names the key. Not a failure: a
  zero delay is valid for a dependency built to absorb the fan-out.
- **New Warning event 20: a Polly v7 handler on a configured client.** `AddPolicyHandler` and
  `AddTransientHttpErrorPolicy` (`Microsoft.Extensions.Http.Polly`), on the client or from
  `ConfigureHttpClientDefaults`, nest with this package's pipeline: a v7 retry of three made one failing GET
  reach the origin twelve times, unreported, because only the platform's `ResilienceHandler` was counted. The
  handler is reported by type name (no dependency on that package) and not removed, since a v7 timeout-only
  policy cannot be told from a retry. [RECIPES.md](docs/RECIPES.md#migrating-from-microsoftextensionshttppolly-polly-v7)
  maps each v7 policy to its key.
- **A side-by-side table of defaults against `AddStandardResilienceHandler` and `AddStandardHedgingHandler`**
  ([CONFIGURATION.md](docs/CONFIGURATION.md#differences-from-addstandardresiliencehandler)), for teams moving
  from the platform's handlers: 20 s total instead of 30 s, 5 s per attempt instead of 10 s, 2 retries instead
  of 3, a 500 ms base delay instead of 2 s, a 5 s cap on retry waits where the platform has none, a longer
  `Retry-After` ending the retries, safe methods only, and a finite `HttpClient.Timeout`. It includes the root
  values that keep the platform's timing. `PlatformDefaultsComparisonTests` pins both columns, and the weekly
  platform-drift run fails if Microsoft changes a default.
- **A platform self-check at host start.** Before creating any client, the startup probe builds two canary
  clients in a private container against the `Microsoft.Extensions.Http` and
  `Microsoft.Extensions.Http.Resilience` versions the application resolved, and fails host start, naming them,
  if their handler chains differ from what this package's mechanism produces. The handler groups rely on the
  factory running actions in registration order, which it does without documenting it; the weekly platform-drift
  run tests new releases, and this covers a consumer who resolves one first. Once per process, about 70 ms cold;
  `ValidateClientsOnStart: false` skips it with the probe.
- **The package has an icon.**
- **Root values in code:** `services.AddHttpResilience(Action<HttpResilienceOptions>)`,
  `AddHttpResilience(IConfiguration, Action<HttpResilienceOptions>)` and
  `AddHttpResilience(IConfigurationSection, Action<HttpResilienceOptions>)`, following Microsoft's options guidance for
  library authors. The delegate applies after the root section and before each client's section, combines with
  a configuration call in either order, is validated like configuration, and is held to the root section's rules:
  it cannot switch off a safe-method guard or add an unsafe method for every client.
- **Every client's concurrency backstop is on the limiter gauges.** `http_resilience.limiter.*{kind="backstop"}`
  was published only when a rate limiter had moved the backstop into its own handler. It is now published for
  every client: one series per client, or one per authority for a hedged client and under `ByAuthority` (a
  backstop moved into its own handler by a rate limiter stays one series per client). The
  bound itself is unchanged and stays per authority where it was.
- **New gauge tag `http_resilience.authority`** on a per-authority backstop series. Its values come from
  `PipelineSelection:Authorities` (plus `shared` for unlisted authorities under `ByAuthority`), never from
  request data. A new dimension is MINOR under [VERSIONING.md](docs/VERSIONING.md); it ships in this major.

### Fixed

- **A repeated `AddHttpResilience` with the same section path from a different configuration object is no longer
  ignored when its values differ.** Only the path was compared, so the first call's values won silently; now the
  client fails when its options are built, naming a key that differs. The same values are still accepted.
- **A resilience handler added to a client before `AddHttpResilienceHandler` through a factory registration**
  (`AddOptions<HttpClientFactoryOptions>(name).Configure<TDep>`, or a registered `IConfigureOptions` type) was
  taken for a `ConfigureHttpClientDefaults` handler and deleted. It now fails client creation like any other
  earlier handler on the client.
- **Per-authority limiter series carry `server.address` and `server.port`**, as the breaker gauge's do, so the
  two join on the destination.
- **A root value is validated only by the clients that read it.** Root `Hedging` values failed every standard
  client, which never hedges, and root `Retry` values every hedged client; now each is judged by the clients whose
  pipeline uses it, and is inert in a service with no such client. A disabled retry's `MaxRetries`, `BaseDelay`,
  `MaxDelay` and `BackoffType` are no longer validated, and no longer written onto the platform's options, where
  Polly would have rejected an out-of-range value anyway. `Retry:RetryableMethods` rules still apply while retry
  is off.
- **`global.json` requires SDK 10.0.404**, the current security release. 10.0.402 and older bundle a Native AOT
  compiler flagged by GHSA-p3jp-9v83-76w9, which fails the AOT smoke publish (NU1903 is an error here). The AOT
  smoke project's override to the patched compiler now applies only on such an SDK, so it never replaces a newer
  SDK's own compiler with an older one.
- **A hedged client's unsafe-method guard is fixed when its pipeline is built.** The guard on the
  timer-started hedge read `Hedging:DisableForUnsafeHttpMethods` from the cached options instance on every
  hedge, so code writing to that instance at run time could start hedging POST, PUT, PATCH and DELETE without
  validation, against the restart-only rule every other option follows. It is now read once, like the rest of
  the pipeline. A consumer's `Configure` or `PostConfigure` of the flag still applies. Affects 2.0.0.
- **Validation no longer crashes on an absurdly long duration.** A `Timeout:Attempt` near `TimeSpan.MaxValue`, or a
  hedged client's `Hedging:Delay` that large, threw `OverflowException` out of startup validation instead of
  reporting the key: the sampling-window rule doubled the attempt timeout, and the hedging budget rule
  multiplied the delay, before any range rule had rejected them. Both are now reported as validation failures
  naming the key. Affects 2.0.0.
- **A consumer that clears `HttpClientFactoryOptions.HttpClientActions` no longer breaks every `CreateClient`**
  with `HttpResilience client action group markers are missing.` Clearing the list removes the package's actions
  with it, so there is nothing left to remove; the pipeline and `Timeout:Client` stay in force. Removing or
  reordering individual entries still fails, now with a message naming the cause. The same applies to
  `HttpMessageHandlerBuilderActions`, which failed with `HttpResilience handler group marker is missing.`

- **A hedged client's authority allow-list did not apply to synchronous `HttpClient.Send`.** The handler
  checked only `SendAsync`, so a request to an unlisted authority sent with `Send` reached the network. Both
  paths are now checked. Affects 2.0.0.
- **Root `Connection` settings no longer reach a client that never opted in.** The connection filter runs for
  every client in the container, and the unnamed default client (`IHttpClientFactory.CreateClient()`, an
  injected `HttpClient`) shares its name with the root options, so it read them: a root `Connection:Enabled: true`
  re-tuned its `SocketsHttpHandler`, or failed its creation when its `HttpClientHandler` carried settings, and a
  root `Connection:AllowAutoRedirect: false` turned off its redirects. The filter now acts only on clients
  registered with `AddHttpResilienceHandler` or `AddHttpResilienceHedgingHandler`. Affects 2.0.0.
- **A hedged client's concurrency backstop logged nothing when it rejected a request.** Its per-authority
  endpoint limiter is sized from `ConcurrencyLimiter:Backstop`; it now logs event 7, like a standard client's.
- **HTTP methods are matched case-sensitively, as RFC 9110 requires.** A request sent as `get` was treated as
  the safe `GET` and retried or hedged; it is now an unknown method and is not repeated. A request sent as
  `post` no longer matches a `RetryableMethods` entry of `POST`. Configured entries stay case-insensitive and
  are upper-cased.
- `AddHttpResilienceHandler()` or `AddHttpResilienceHedgingHandler()` inside `ConfigureHttpClientDefaults` threw a bare
  `ArgumentNullException (Parameter 'key')`. It now throws `InvalidOperationException` explaining that
  resilience is registered per client, and that a .NET Aspire ServiceDefaults handler is replaced per client.
- The library no longer fails to build on newer .NET 10 SDKs: `AnalysisLevel` is pinned to `10.0-recommended`,
  `global.json` pins the 10.0.4xx band, and the AOT smoke test uses the patched ILCompiler 10.0.14.

## [2.0.0] - 2026-08-29

A rewrite. The package now **configures** `Microsoft.Extensions.Http.Resilience` and owns no strategy of its
own. Every public API and every configuration key changed; nothing from 1.0.0 binds or compiles unchanged.

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

Initial release. Superseded by 2.0.0.
