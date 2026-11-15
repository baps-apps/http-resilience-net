# Security and governance

## Posture

- **Outbound only.** No inbound behavior.
- **No credentials.** Never touches authentication headers, tokens or cookies; the schema holds no secrets.
- **No request data recorded.** Never logs or tags bodies, headers, paths or query strings. The only URI part
  it reports is the authority (scheme, host, port): in the exception a hedged client throws for an unlisted
  host, and as an allow-list entry or `shared` in breaker logs and gauges.
- **No TLS weakening.** No setting for it; the package sets no `ServerCertificateCustomValidationCallback` or
  `SslOptions`. With `Connection:Enabled`, an `HttpClientHandler` primary handler is replaced by a new
  `SocketsHttpHandler` only when nothing is set on it; one carrying a client certificate, a certificate
  callback, a proxy, credentials or any other setting fails client creation, so mTLS or pinning is never
  dropped silently. A supplied `SocketsHttpHandler` is configured, not replaced.
- **Redirects.** Standard clients follow them (runtime default); hedged clients do not, unless
  `Connection:AllowAutoRedirect: true`. The runtime strips `Authorization` across a redirect but **re-sends
  custom headers such as API keys** to the new host. A redirect bound you set on your own `SocketsHttpHandler`
  is kept unless the schema states one or the client is hedged. If the bound cannot be applied (a primary
  handler without `AllowAutoRedirect`), Warning event 13 is logged. **In shared configuration, set
  `HttpResilience:Connection:AllowAutoRedirect: false` at the root**; a client that needs redirects (a CDN,
  pre-signed URLs) states `true` in its own `Clients:{name}:Connection` section, where review sees it.
- `Retry-After` parsing is the platform's. A value longer than `Retry:MaxDelay` (default 5 s) ends the retries
  instead of being waited for, so an origin cannot hold a caller and its limiter slot for as long as it likes.
- Report a vulnerability privately: [SECURITY.md](../SECURITY.md).

## Idempotency

Only RFC 9110 safe methods (GET, HEAD, OPTIONS, TRACE) are retried or hedged by default: an allow-list, so
PUT, DELETE, POST and unrecognised methods are excluded. Request methods match exactly (`get` is unknown).
Hedging's guard covers both triggers: a failed attempt and the delay timer on a slow request.

Opting in is per client, explicit and logged:

- `Retry:RetryableMethods` naming the method,
- `Retry:DisableForUnsafeHttpMethods: false`,
- `Hedging:DisableForUnsafeHttpMethods: false`.

None of these can widen the root for every client (in the section or in `AddHttpResilience(options => ...)`).
Every opted-in client logs **Warning event 10** at startup naming the methods and key, so "which services can
duplicate a mutation?" is a log query (needs a generic host).

Review these changes like a database migration. The endpoint must deduplicate on an idempotency key — for
hedging, under simultaneous arrival. Retried content must be replayable.

## Amplification

The package chooses no destinations, so it cannot introduce SSRF, but it **multiplies** requests to whatever
destination it is given: up to `1 + Retry:MaxRetries` (or `1 + Hedging:MaxHedgedAttempts`) per request, both
capped at 10 extra, in every replica independently. If the application has an SSRF weakness, retries make it
louder: validate destinations before the client.

## Resource exhaustion

| Risk | Control |
| --- | --- |
| Pipelines per authority | `ByAuthority` requires an explicit list; unlisted hosts share one pipeline. Hedged clients require a list and reject unlisted hosts before anything is allocated (on `SendAsync` and `Send`). |
| Unbounded metric series | No tag value comes from request data. |
| Unbounded concurrency | A concurrency bound always exists: the backstop (1,000, no queue) or the client's own `Limit`. |
| Queue memory | `QueueLimit` defaults to 0, capped at 1,000. Each queued request holds its content. |
| Stalled response bodies | `Timeout:Client` bounds the body; `Timeout:Total` does not. |
| Nested pipelines | A second registration through this package throws. A platform `AddStandardResilienceHandler` on the same client cannot be refused; it is reported (event 12). Review it. |
| Inert configuration | Unread `Clients` sections and keys a pipeline never reads fail startup. |

## Configuration as a control plane

The schema governs how much load your services generate:

- Store it centrally; restrict write access.
- Review changes to `MaxRetries`, `MaxHedgedAttempts`, `RetryableMethods` and both `DisableForUnsafeHttpMethods`
  flags as production changes.
- Try them in a lower environment with telemetry first.

## Supply chain

- Depends only on first-party Microsoft packages and Polly, referenced at their **minimum** versions: your
  application's restore picks the version that ships, so your own vulnerability scan is what counts.
- Polly is limited to 8.x (`Polly.Core`, `Polly.Extensions` and `Polly.RateLimiting` `[8.4.2, 9.0.0)`): Polly 9
  is commercially licensed. Another package that requires Polly 9 fails restore with a version conflict (NU1107).
  A Polly 9 reference in the application itself wins, and restore only warns (NU1608): treat NU1608 as an error
  (`WarningsAsErrors`) to block it.
- `nuget.config` uses `packageSourceMapping`: only `CodeStyle.NET` (a build-time analyzer) and
  `HttpResilience.NET` (the previous release, the package validation baseline) come from the internal feed;
  everything else from nuget.org.
- Deterministic builds, embedded PDBs and SourceLink. The package is signed: the publish script refuses to start
  without a code-signing certificate unless `-AllowUnsigned` is passed, and verifies the signature before
  pushing ([scripts/README.md](../scripts/README.md#package-signing)).
- The public API is recorded in `PublicAPI.*.txt` and gated by an analyzer, so every public change shows in
  review.
- There is no CI for changes; a weekly workflow runs the tests against the newest platform releases
  ([CONTRIBUTING.md](../CONTRIBUTING.md#platform-drift)). The release gates, `dotnet list package --vulnerable
  --include-transitive` among them, run locally ([CONTRIBUTING.md](../CONTRIBUTING.md#release-gates)); the
  publish script runs them all, except a fresh benchmark run, before packing.
