# Security and governance

## Posture

- Configures **outbound** HTTP resilience only. No inbound behavior.
- Never touches authentication headers, tokens or cookies.
- Trimming and Native AOT are supported and proven by a Native AOT smoke test in CI, so a consumer cannot silently lose configuration values to a trimmed publish.
- Never records request or response bodies, headers, query strings or URLs.
- Never modifies TLS behavior. There is no configuration path to weaken certificate validation, and the package does not set `ServerCertificateCustomValidationCallback` or any `SslOptions`.
- Never weakens a redirect bound you set yourself. `Connection:Enabled` configures a primary `SocketsHttpHandler` you supplied rather than replacing it, and `AllowAutoRedirect` is written onto it only when the schema states the value or resolves it to `false`. It used to be written unconditionally, so a handler hardened against redirects had them re-enabled by a connection-pool switch — with the runtime re-sending `X-Api-Key` and every other custom credential header across the hop, this was a credential-disclosure path opened by the resilience package rather than by the application. When the bound cannot be applied at all, because the primary handler has no `AllowAutoRedirect`, that is logged at Warning (event 13) rather than passed over.
- `Retry-After` parsing is delegated to `Microsoft.Extensions.Http.Resilience`, not hand-rolled.

## Telemetry

One tag is added, and only where the platform leaves it missing: `error.type` for exception outcomes, carrying the exception type name. The status-code half of that dimension comes from `Microsoft.Extensions.Http.Resilience` itself. Both possible values are bounded by construction.

Nothing derived from a request URI becomes a metric tag. The **health check** is the one place internal topology is exposed: its payload is keyed `client -> authority`, so map `/healthz/deps` behind cluster-internal networking or authentication. `/healthz/live` and `/healthz/ready` carry no such detail. This is a security property as much as an operational one: a dimension whose cardinality is the number of hosts a process happens to call is a resource-exhaustion path into the metrics backend, reachable from ordinary application input.

If you add your own enrichment, apply the same rule. Never tag on full URLs, tenant identifiers, user identifiers, correlation IDs or exception messages.

## Amplification

This package does not choose destinations, so it cannot introduce SSRF. It does **multiply** whatever destination it is given:

- retry, on a client registered with `AddHttpResilience`: up to `1 + Retry:MaxRetries` requests
- hedging, on a client registered with `AddHedgedHttpResilience`: up to `1 + Hedging:MaxHedgedAttempts` requests

The two are alternatives on any one client, not factors: a client gets one registration or the other, and the
hedging pipeline has no retry strategy. Across two clients calling the same host they do add up.

Where an application has an SSRF weakness, a resilience pipeline makes each occurrence several times louder. Validate destinations before the request reaches the client, and keep `MaxRetries` at the smallest value that meets the reliability goal.

## Resource exhaustion

| Path | Control |
| --- | --- |
| Unbounded pipelines per authority | `PipelineSelection:Mode = ByAuthority` requires an explicit `Authorities` allow-list. Unlisted hosts share one pipeline, so pipeline count is fixed at deploy time. |
| Unbounded pipelines per authority, hedged clients | The hedging handler keeps a circuit breaker, a concurrency limiter and a metric series per authority whatever the selection mode, cached for the life of the process. `AddHedgedHttpResilience` therefore requires `Authorities` and rejects an unlisted authority in an outermost handler, before anything is allocated for it. |
| Duplicate registration | A second `AddHttpResilience` on one client would nest two pipelines and multiply retries — three configured attempts becoming nine origin calls, silently. It fails at startup instead, and the root `AddHttpResilience(configuration)` is idempotent so that calling it again cannot disarm that guard. **The guard covers this package's API only.** A consumer calling the platform's `AddStandardResilienceHandler` on the same client nests a pipeline identically and cannot be refused, because the excess is indistinguishable from the `AddResilienceHandler` this package recommends without reflection that trim and AOT support rule out. It is reported at Information (event 12); treat "no client calls the platform's standard handler as well" as a review item, not a guarantee. |
| Stalled response bodies | `Timeout:Total` stops applying at response headers, and the platform's handlers set `HttpClient.Timeout` to infinite, so an origin that trickles a body indefinitely would hold a connection, a buffer and an inbound request for as long as it liked. `Timeout:Client` puts a finite bound back. |
| Unbounded metric series | No tag is derived from request data. |
| Queue memory | `RateLimiter:QueueLimit` and `ConcurrencyLimiter:QueueLimit` default to 0. Each queued request holds its `HttpRequestMessage` and content buffer while it waits, so a deep queue of large uploads is a memory risk as well as a latency one. |
| Traffic amplification | `Retry:MaxRetries` and `Hedging:MaxHedgedAttempts` are capped at 10 and validated against the total timeout budget. |
| Queue memory, upper bound | Both `QueueLimit` values are capped at 1,000. Past that depth a queue stops being backpressure and becomes a memory sink outside any timeout budget. |
| Unbounded concurrency | A concurrency bound is never absent. With no rate limiter it is `ConcurrencyLimiter:Backstop`, in the platform's own limiter slot; with a rate limiter it moves to its own handler outside it; and when the client sets its own `ConcurrencyLimiter:Limit`, validation holds that at or below the backstop, so it is the tighter bound and the backstop handler is skipped. There is no configuration in which nothing caps in-flight requests. Pinned by `ConcurrencyBackstopTests`, including the both-limiters case that the earlier absolute wording of this row hid. |
| Inert configuration | A section under `HttpResilience:Clients` that no registered client reads fails startup. A misspelled or renamed client section otherwise leaves that client on root defaults with nothing to say so -- the same class of silent state as a forgotten `Enabled` key. `AllowUnusedClientSections` opts out, root-only, defaulting to failing. |

## Idempotency

Only the four methods RFC 9110 defines as safe — GET, HEAD, OPTIONS, TRACE — are ever repeated. Everything else is disabled by default, including methods this package does not recognize, and for hedging on **both** paths by which a hedged attempt can start -- an attempt that completed and failed, and the hedging delay elapsing while every attempt is still running. Only the first is an outcome, so only the first is reachable from a predicate; the second is closed by suppressing the attempt itself. A guard covering only the first duplicates exactly the requests hedging is for: the slow ones.

Enabling them is a deliberate, reviewable act, and one the running process announces:

- `Retry:RetryableMethods` naming the method, or
- `Retry:DisableForUnsafeHttpMethods: false`, **per client only** — the root section refuses it, or
- `Hedging:DisableForUnsafeHttpMethods: false`, **per client only** — the root section refuses it

None of the three can be stated fleet-wide. The two flags cannot be `false` at the root because one key there decides that every client in the process, including clients registered later that state nothing, may deliver a mutating request to its origin more than once. That is not a property a fleet can have; it is a property of one endpoint's idempotency handling. `RetryableMethods` remains inheritable in the **narrowing** direction only — a root list of `["GET"]` restricts every client and is strictly safer than the default — while an *unsafe* entry at the root is refused, because it reaches every standard client by exactly the route the flags are refused for and only the blast radius over unrecognized methods differs.

A client stating `DisableForUnsafeHttpMethods` beside a list in force is refused in **both** directions, and the direction that reads as harmless is the one that matters: `true` beside a list is the protective statement being silently discarded, written by whoever is closest to the endpoint, in the section they own, while the list may sit in a root section a platform team owns. To narrow a client back to safe methods under an inherited list, give it an empty `RetryableMethods`.

All three also emit **event ID 10 at Warning** at host start, naming the client, the methods and the key — the key the list is *actually* stated under, which for an inherited list is the root section or, for one set through the `configure` delegate, neither. (It named this client's section unconditionally, so an operator following "remove that key" searched a section that did not contain it.) That turns "which of our services can duplicate a mutation?" from a configuration grep into a log query, and makes the set something a platform team can inventory per deploy.

Treat any of these three appearing in a diff as requiring the same scrutiny as a database migration. The endpoint must deduplicate on an idempotency key, and for hedging it must do so under *simultaneous* arrival — hedged attempts give the origin no serialization to rely on.

A retried request must also carry **replayable** content. Measured against a real endpoint, a `StreamContent` over a non-seekable stream retried three times delivers the body once and then an empty body twice, with no exception thrown — so an endpoint that tolerates a missing body will act on it. Buffer the content before sending, or build fresh content per attempt.

## Configuration as a control plane

The schema contains no credential fields and no secrets are expected in it. Even so, it governs how much load your services can generate:

- Store it centrally and restrict write access to the platform or SRE team.
- Review changes to `Retry:MaxRetries`, `Hedging:MaxHedgedAttempts`, `RetryableMethods` and the two `DisableForUnsafeHttpMethods` flags as production changes.
- Test in a lower environment with observability before rolling out.

## Supply chain

- `nuget.config` clears default sources and uses `packageSourceMapping` to pin private packages to the internal feed, so a public package cannot shadow an internal name.
- Deterministic builds, embedded PDBs and SourceLink are enabled for every project.
- The public surface is gated by `Microsoft.CodeAnalysis.PublicApiAnalyzers`: every public type and member is listed in `src/HttpResilience.NET/PublicAPI.*.txt`, an addition or signature change fails the build, and the change shows up as a diff in review. `EnablePackageValidation` is on as well, but it compares against a baseline only once one exists within the current major -- see [VERSIONING.md](VERSIONING.md).
- Every out-of-band dependency is pinned in `Directory.Packages.props`, including `System.Threading.RateLimiting`: left to its transitive reference it resolved two majors behind everything else and outside central package management's reach for patching.
- CI builds, tests and packs on every pull request, and fails on any vulnerable dependency. The audit reads `dotnet list package --format json` rather than grepping the table, so a change in column padding cannot silently stop it failing.
