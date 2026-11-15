# Production checklist

## Idempotency — first

- [ ] Every startup **event 10** line is expected: each is a client that can repeat a mutating request.
- [ ] An unsafe method is in `Retry:RetryableMethods` only where the endpoint deduplicates on an idempotency key
      (non-standard verbs such as `PURGE` count).
- [ ] Clients that retry a mutating method send replayable content (`LoadIntoBufferAsync()` on a `StreamContent`).
- [ ] No `DisableForUnsafeHttpMethods: false` without a named owner and reason.
- [ ] Endpoints behind a hedged client tolerate *simultaneous* duplicates of what is hedged.

## Load

- [ ] For each retrying client, `replicas × RPS × (1 + MaxRetries)` is load the dependency can take while
      failing. For hedged clients use `1 + MaxHedgedAttempts`. Sum clients that reach the same host.
- [ ] `Retry:UseJitter` is on.
- [ ] `Retry:BaseDelay` suits the distance: the 500 ms default suits same-cluster calls; use 1 s+ across
      regions (and check `Timeout:Total` still fits).
- [ ] `RateLimiter:PermitLimit` = downstream quota ÷ (replicas × clients reaching it), revisited when
      autoscaling limits change. It cannot enforce a cluster-wide quota.
- [ ] `CircuitBreaker:MinimumThroughput` is reachable by **one replica's** traffic: compare startup **event
      11** with each client's real per-replica rate. Quiet clients need a lower value.
- [ ] `ConcurrencyLimiter:Backstop` (1,000) is above each client's peak concurrency per pipeline.

## Destinations

- [ ] Shared configuration sets `HttpResilience:Connection:AllowAutoRedirect: false` at the root: a standard
      client otherwise follows redirects to any host and re-sends custom headers (API keys) there.
- [ ] Every client section stating `Connection:AllowAutoRedirect: true` sends no credential in a custom header,
      or its redirect targets are trusted.

## Timeouts

- [ ] `Timeout:Attempt` from the dependency's p99; `Timeout:Total` fits the retry schedule (event 14 warns).
- [ ] `QueueLimit` small where an SLO is written against `Timeout:Total` (queue wait is outside it).
- [ ] Large or streamed responses use `HttpCompletionOption.ResponseHeadersRead` with their own deadline.
- [ ] No `ConfigureHttpClient` sets `HttpClient.Timeout` (it fails); use `Timeout:Client`.
- [ ] **No typed-client constructor sets `HttpClient.Timeout`.** Undetectable; truncates the pipeline. Search.

## Registration

- [ ] The app runs on a **generic host** (startup validation, client check and notices need it).
- [ ] `HttpResilience:ValidateClientsOnStart` is not `false` without a reason.
- [ ] No client adds `AddStandardResilienceHandler()` on top of this package (9 origin calls instead of 3).
      Only reported, so **search the code**; every **event 12** line is a known `AddResilienceHandler`. (One with
      a configure, and `AddStandardHedgingHandler`, fail startup.)
- [ ] No client keeps a Polly v7 handler (`AddPolicyHandler`, `AddTransientHttpErrorPolicy`): no **event 20**.
- [ ] No startup **event 6** (client disabled) is unexpected; the root `HttpResilience:Enabled` is not `false`.
- [ ] Every hedged client lists its authorities; the list is reviewed when a host is added.
- [ ] With `Connection:Enabled`, the primary handler is a `SocketsHttpHandler` or the factory default (a
      supplied `HttpClientHandler` carrying certificates, a proxy or any other setting fails client creation).
- [ ] Every **event 13** line (redirect bound not applied) is understood.

## Observability

- [ ] An OpenTelemetry SDK is wired, both meters (`"Polly"`, `"HttpResilience.NET"`) registered,
      `AddHttpResilienceTelemetry()` called, `AddHttpClientInstrumentation()` on metrics **and** tracing.
- [ ] Queries for `resilience.polly.strategy.events`, `http_resilience.circuit_breaker.state` and
      `http.client.request.duration` were **run** and returned data.
- [ ] `HttpResilience.NET.Configuration` logs (events 6, 10–21) reach the backend and are retained.
- [ ] Alerts exist for breaker open, breaker thrashing, retry rate and limiter rejections
      ([OPERATIONS.md](OPERATIONS.md#alerts-worth-having)).
- [ ] `queued_requests` is graphed for clients with `QueueLimit` > 0.
- [ ] No custom enrichment tags URLs, tenants, users or correlation IDs.

## Health and operations

- [ ] **No liveness or readiness probe depends on circuit breaker state.**
- [ ] The team knows configuration changes need a restart, and that a rolling restart resets every breaker.
