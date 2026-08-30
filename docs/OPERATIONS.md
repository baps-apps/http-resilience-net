# Operations

## Resilience is process-local

Every control in this package acts inside one process. Nothing here coordinates across replicas.

| Control | Scope | Fleet-wide effect |
| --- | --- | --- |
| Retry | per process | outbound traffic multiplied in every replica independently |
| Circuit breaker | per process, per pipeline | each replica must independently observe `MinimumThroughput` **attempts** before it can open |
| Rate limiter | per process, per client | actual rate is `replicas × clients × PermitLimit` per window |
| Concurrency limiter | per process, per client | actual concurrency is `replicas × clients × Limit` |
| Concurrency backstop | per process, per pipeline — one per client, or one **per authority** when the client is hedged or uses `PipelineSelection:Mode = ByAuthority`. Enabling a rate limiter displaces it into a handler of its own, which is **per client again** even under `ByAuthority` | always in force; actual concurrency is `replicas × pipelines × Backstop` |
| Hedging | per process | fan-out multiplied in every replica |

Size every one of these by dividing the real budget by the expected replica count, or enforce the real budget at a gateway or service mesh where it can actually be global.

Note the second multiplier on both limiters: the budget belongs to a *named client*, not to a downstream. Two clients calling the same host hold two independent budgets — an ordinary shape when a typed client is split by concern, or when a shared library registers its own client against a host the application also calls. Count the clients that reach the host, not the hosts.

## The circuit breaker is per replica, and needs traffic to work

`MinimumThroughput` (default 100) must be observed **in one replica** within `SamplingDuration` (default 30 s) before `FailureRatio` is even evaluated.

**It counts attempts, not caller requests.** The breaker sits inside the retry loop — total timeout, retry, circuit breaker, attempt timeout — so each retry is its own observation. At the default `Retry:MaxRetries` of 2 a fully-failing caller request contributes three. That makes the breaker more sensitive than the number reads, not less, but the arithmetic below is what capacity planning uses. Pinned by `BreakerThroughputScopeTests`.

Three consequences:

```text
low-traffic client:  100 attempts / 30 s = 3.3 attempts per second per replica
                     ~1.1 failing caller requests per second at MaxRetries=2;
                     below that the breaker can never open — the client
                     effectively has no circuit breaker at all

20 replicas:         20 x 100 = >=2,000 failing ATTEMPTS fleet-wide
                     (~670 caller requests at MaxRetries=2)
                     before the fleet stops calling a dead dependency
```

Size `MinimumThroughput` against **per-replica attempt** traffic, not fleet request traffic. A client that gets a request a minute needs a much lower value, or it needs to accept that timeouts are its only protection.

## Retry amplification

The arithmetic to do before raising `Retry:MaxRetries`:

**The two multipliers do not compound on one client.** A client is registered with `AddHttpResilience` *or*
`AddHedgedHttpResilience`, never both: the hedging pipeline has no retry strategy at all, and a hedged client
that states `Retry:*` keys of its own fails at registration. So there are two independent ceilings, not one
combined one. An earlier revision of this page multiplied them together and quoted a twelve-times worst case
that this package cannot produce.

```text
20 pods × 100 RPS = 2,000 RPS steady state
Dependency degrades to 100% 5xx.

A retrying client (AddHttpResilience):

  MaxRetries = 1   outbound = 2,000 × 2  =  4,000 RPS   2.0×
  MaxRetries = 2   outbound = 2,000 × 3  =  6,000 RPS   3.0×   (default)
  MaxRetries = 3   outbound = 2,000 × 4  =  8,000 RPS   4.0×
  MaxRetries = 10  outbound = 2,000 × 11 = 22,000 RPS  11.0×   (the cap)

A hedged client (AddHedgedHttpResilience), for the requests slow enough
to start a supplementary attempt:

  MaxHedgedAttempts = 1   ×2    (default)
  MaxHedgedAttempts = 2   ×3
  MaxHedgedAttempts = 10  ×11   (the cap)
```

Where they *do* compound is across clients, not within one: a service whose retrying client and whose hedged
client both call the same dependency puts both multipliers on that dependency at once. Count the clients that
reach the host.

Circuit breakers damp this, but not before the surge:

```text
MinimumThroughput = 100 attempts, per pod, per pipeline.
20 pods × 100 = 2,000 failed requests minimum before the fleet stops.
Under ByAuthority, that threshold applies per authority as well as per pod.
```

Keep `UseJitter` on. Without it every replica retries on the same schedule and the retries arrive as a wave rather than a spread.

## Metrics

None of this leaves the process until the consuming service wires an exporter. The package references no
OpenTelemetry package and registers no meter with an SDK — it publishes on a `Meter` and stops there. Register
both meter names, plus HTTP client instrumentation:

```csharp
builder.Services.AddHttpResilienceTelemetry();   // the error.type tag. Registers no meter.

builder.Services.AddOpenTelemetry().WithMetrics(metrics => metrics
    .AddMeter(HttpResilienceTelemetryExtensions.PollyMeterName)  // "Polly"
    .AddMeter(HttpResilienceTelemetryExtensions.MeterName)       // "HttpResilience.NET"
    .AddHttpClientInstrumentation()
    .AddOtlpExporter());
```

Under `OpenTelemetry.NET` package **2.7.0 or later**, both meters are registered by
`AddObservability(...)` already and nothing needs configuring. On **2.6.x or earlier**, put the two names in
`OpenTelemetryOptions:Meters`. `AddHttpResilienceTelemetry()` is called in `Program.cs` either way. Full wiring,
including traces and logs, is in [README.md](../README.md#telemetry).

**The failure mode is silence.** The SDK drops every measurement from a meter name it was not given: a missing
`AddMeter` produces an empty dashboard, no log line and no exception. Confirm every instrument below returns
a series before you write an alert on it.

| Metric | Instrument | Source | Useful dimensions |
| --- | --- | --- | --- |
| `resilience.polly.strategy.events` | counter | Polly | `pipeline.name`, `pipeline.instance`, `strategy.name`, `event.name`, `event.severity`, `error.type` |
| `resilience.polly.strategy.attempt.duration` | histogram | Polly | `pipeline.name`, `strategy.name`, `attempt.number`, `attempt.handled`, `error.type` |
| `resilience.polly.pipeline.duration` | histogram | Polly | `pipeline.name`, `pipeline.instance`, `error.type` |
| `http.client.request.duration` | histogram | `System.Net.Http` | `server.address`, `http.request.method`, `http.response.status_code` |

Retries are the `resilience.polly.strategy.events` counter filtered to `event.name = OnRetry`; breaker transitions are the same counter filtered to the `OnCircuit*` event names. There is no separate attempts counter, and `error.type` appears on all three Polly instruments.

`pipeline.instance` is empty for a standard client and carries the request authority for a hedged one, which is why a hedged client has to declare the authorities it may call.

`error.type` carries the status code (`"503"`) when a response is the failure and the exception type name
(`"System.Net.Http.HttpRequestException"`) when an exception is. The status half comes from
`Microsoft.Extensions.Http.Resilience` itself; `AddHttpResilienceTelemetry()` supplies the exception half,
which nothing else does. Without it, `sum by (error.type)` silently omits every connection failure and
timeout. Every dimension above is bounded by construction.

Do not add dimensions derived from request URIs, tenant identifiers, correlation IDs or exception messages. A metric dimension whose cardinality is the number of hosts a process happens to call will eventually evict everything else in the backend.

### The package's own instruments

Three `ObservableGauge`s, read once per collection and never on the request path. They exist because Polly
counts breaker transition *events* — which answers "did it open" but not "is it open" once a scrape is
missed — and because limiter statistics exist on `RateLimiter` and are otherwise never read. The meter comes
from `IMeterFactory`, so it is scoped to the container and disposed with it.

| Instrument | Unit | Dimensions | Cardinality |
| --- | --- | --- | --- |
| `http.resilience.circuit_breaker.state` | `{state}` — 0 closed, 1 open, 2 half-open | `http.client.name`, `http.resilience.authority`, `server.address`, `server.port` | LOW — clients x allow-listed authorities, both fixed at deploy time |
| `http.resilience.limiter.available_permits` | `{permit}` | `http.client.name`, `http.resilience.limiter.kind` | LOW — at most two series per client |
| `http.resilience.limiter.queued_requests` | `{request}` | `http.client.name`, `http.resilience.limiter.kind` | LOW — same |

The breaker gauge carries `server.address` and `server.port` as well as the authority string, because those
are the semantic-convention pair `System.Net.Http` tags *its* series with — so breaker state joins to request
duration without splitting `scheme://host:port` in the query. They add no series: both are functionally
determined by the authority already present, and a key that is not an authority (the shared pipeline) carries
neither rather than a placeholder. `http.client.name` has no semantic-convention equivalent, so it stays this
package's own dimension.

Both limiter gauges carry `http.resilience.limiter.kind` rather than splitting into separate instruments,
because the operator's question is "how close is this client to shedding load" and the answer is whichever
limiter is nearest its bound. One query covers all three:

| `kind` | Present when | What it measures |
| --- | --- | --- |
| `rate` | `RateLimiter:Enabled` | The configured permit budget, and the queue waiting for one. |
| `concurrency` | `ConcurrencyLimiter:Enabled` | The client's own concurrency cap. **`queued_requests` here is the number worth watching** — that wait is outside `Timeout:Total`. |
| `backstop` | `RateLimiter:Enabled` **and** `ConcurrencyLimiter:Enabled` false | The concurrency backstop, in the handler it gets when a rate limiter takes the platform's slot. With both limiters enabled that handler is skipped — the client's own `Limit` is the tighter bound — so this series is absent and `kind=concurrency` is the one to watch. `queued_requests` is always 0; it has no queue. |

One gap to know about: the `backstop` series exists only when a rate limiter has displaced the backstop into a
handler of its own. In the ordinary case it lives inside the platform's limiter slot, where Polly builds it
per pipeline — which is what makes it per authority under `ByAuthority`. Reading its statistics would mean
supplying a single instance and turning that per-authority bound into a per-client one, which is the worse
trade. If you need a concurrency number you can watch on a client with no rate limiter, set
`ConcurrencyLimiter:Limit`.

## Alerts worth having

| Alert | Condition | Why |
| --- | --- | --- |
| Retry rate elevated | retry attempts / total requests > 10% for 5 min | A dependency is degrading, and you are amplifying load on it. |
| Circuit breaker open | `http.resilience.circuit_breaker.state` > 0 for > 2 min, or the dependency health check Degraded | A dependency is failing fast. The gauge is tagged `http.client.name`, `http.resilience.authority`, `server.address` and `server.port`, so it names which one without a health endpoint and joins to `http.client.request.duration` without string-splitting. |
| Breaker thrashing | more than 3 open transitions in 10 min | `MinimumThroughput` or `BreakDuration` is mistuned for this traffic level. |
| Rate limiter rejections | event ID 8, or `http.resilience.limiter.available_permits{kind="rate"}` at 0 | Either the downstream contract changed, or `PermitLimit` was not divided by replica count. The log line names the key to raise. |
| Concurrency limiter rejections | event ID 9 | This client is already waiting on the dependency as much as it is allowed to. Usually the dependency slowed down. Raising the queue instead of the limit only moves the latency outside `Timeout:Total`, where nothing in this package can bound it. |
| Limiter approaching saturation | `http.resilience.limiter.queued_requests` > 0 sustained, any `kind` | Requests are waiting for a permit or a slot. That wait is **outside** `Timeout:Total` — only `Timeout:Client` and caller cancellation bound it. Alert on the instrument without filtering `kind`: the concurrency limiter's queue is the one that can be 1,000 deep. |
| Concurrency limiter approaching saturation | `http.resilience.limiter.available_permits{kind="concurrency"}` at 0 | Every slot is in use, so the next request queues. Precedes event ID 9 rather than following it, which is the difference between an alert and a post-mortem. |
| Two nested pipelines | event ID 12 at Information, at client construction | This client has a resilience handler the package did not add. Expected if it came from `AddResilienceHandler`. If it came from `AddStandardResilienceHandler` on a client that already has `AddHttpResilience`, retries **multiply** — three configured attempts become nine origin calls — and the total timeout is applied twice. Check which. Information rather than Warning because the correct case produces it too; see the note under the alert table. |
| Backstop rejections | event ID 7, or `RateLimiterRejectedException` on a client with `RateLimiter:Enabled = false` | Concurrency reached `ConcurrencyLimiter:Backstop` (1,000 by default). Nothing is queued above it. Either the dependency slowed down and requests are piling up, or this client genuinely needs more concurrency than the backstop allows. The log line names the current value and the key to raise, because the exception type alone is indistinguishable from a configured rate limit. |
| Mutating methods repeatable | event ID 10 at Warning, at startup | This client can deliver a POST, PUT, PATCH, DELETE or unrecognized method to its origin more than once. Legitimate when the endpoint deduplicates on an idempotency key — the alert exists so the set of such clients is a list somebody maintains rather than a property nobody can enumerate. Inventory it per deploy; page on a client appearing that was not on the list. |
| Resilience disabled | event ID 6 at Warning, at startup | A client has no pipeline at all. Deliberate during an incident; otherwise a forgotten `Enabled` key, and the client has no retries, no timeouts and no circuit breaker. Alert on this in non-production too — it is cheapest to catch in the deployment that introduced it. |
| Attempt timeouts | attempt timeout rate rising | `Timeout:Attempt` is tighter than the dependency's real latency. |
| Client-timeout cancellations | bare `TaskCanceledException` with no `TimeoutRejectedException` alongside it | `Timeout:Client` fired, so either a long limiter queue wait or a response body that stopped arriving. Neither is visible to the pipeline's own timeouts, because both happen outside them — **and neither opens the circuit breaker**, see below. |

## The failure the resilience signals cannot see

A dependency that returns response headers promptly and then trickles or stalls the body degrades every call to the full `Timeout:Client` budget while every resilience signal stays green.

`Timeout:Client` does bound it — that is what a finite `HttpClient.Timeout` is for. But the pipeline sees that deadline as **caller cancellation**, and caller cancellation is deliberately excluded from the transient-failure predicate: it must not be, or a cancelled request would open a circuit. So a stalled body is never retried, never counted by the breaker, never surfaces as `TimeoutRejectedException`, and Polly's attempt histogram records each one as a *fast success* — the handler chain really did return quickly, with headers.

```text
symptom     p99 pinned at Timeout:Client, flat retry rate, circuit closed, health check Healthy
watch       http.client.request.duration  (System.Net.Http), not the Polly instruments
confirm     bare TaskCanceledException with no TimeoutRejectedException alongside it
mitigate    HttpCompletionOption.ResponseHeadersRead plus your own deadline on reading the stream
```

Nothing in a `DelegatingHandler` can fix this: under the default `HttpCompletionOption.ResponseContentRead` the body is buffered by `HttpClient` after the chain has returned. Owning that would mean owning response streaming, which is out of scope for a package that configures the platform. What is in scope is saying so.

## Logs

Structured, source-generated, no request or response content:

Polly's own telemetry already logs every retry twice at Warning, carrying the pipeline name, the outcome and the attempt number. This package adds only what those lines do not have, at levels that do not compete with them:

| Event ID | Level | Message |
| --- | --- | --- |
| 1 | Debug | retry attempt, with the computed delay, status and exception type |
| 2 | Warning | circuit breaker opened, with authority and break duration |
| 3 | Information | circuit breaker half-open |
| 4 | Information | circuit breaker closed |
| 5 | Debug | hedging attempt started |
| 6 | **Warning** | resilience registered but disabled for a client, with the key that would enable it. Emitted once per client at startup, when the host materializes its options — not on first use, because a rarely-exercised client would report it hours or days after the deploy that caused it. |
| 7 | Warning | the concurrency backstop rejected a request, with the current value and the key to raise |
| 8 | Warning | the rate limiter rejected a request, with the permit key to raise and the replicas x clients arithmetic |
| 9 | Warning | the concurrency limiter rejected a request, with the limit, the queue depth and the key to raise |
| 10 | **Warning** | this client may repeat a mutating request, with the methods and the key that allowed it. Emitted once per client at startup, for both mechanisms — the `DisableForUnsafeHttpMethods` flag and an explicit `RetryableMethods` allow-list — because the hazard at the origin is identical and only the review trail differs. Alerting on this event ID gives you a fleet inventory of every client that can duplicate a mutation. |
| 11 | Information | the failing-attempt rate, per replica, that this client's circuit breaker needs before it can open — and the caller-request rate that corresponds to at its retry count. Emitted once per client at startup. Not a defect: a busy client meets the rate easily. It is there because the arithmetic is invisible, and a client with the default thresholds and a few requests a second per replica has a breaker in its configuration, a breaker in its runbook, and no breaker in effect. |
| 13 | **Warning** | the redirect bound could not be applied to this client, because its primary handler is neither a `SocketsHttpHandler` nor an `HttpClientHandler` and has no `AllowAutoRedirect` to set. Emitted once per client. A stub or in-memory handler resolves no redirects and is unaffected — which is why this is a Warning and not the exception the `Connection:Enabled` path throws. A handler wrapping a `SocketsHttpHandler` of its own does resolve them, and then a 3xx from a listed authority reaches a destination the hedged client's allow-list never sees. |
| 12 | Information | this client carries more resilience handlers than the package added, with both counts. Emitted once per client, at the first construction of its handler chain — once, not once per handler rotation, or it would repeat every two minutes for the life of the process. Information rather than Warning because the state is frequently correct: `AddResilienceHandler`, the documented escape hatch, produces it too, and the package cannot tell that apart from a second `AddStandardResilienceHandler` nesting a pipeline. Treat every line as a review item. |

## Health checks

```csharp
app.MapHealthChecks("/healthz/live",  new() { Predicate = _ => false });
app.MapHealthChecks("/healthz/ready", new() { Predicate = r => !r.Tags.Contains("dependency") });
app.MapHealthChecks("/healthz/deps",  new() { Predicate = r =>  r.Tags.Contains("dependency") });
```

The resilience check reports Degraded at worst -- never Unhealthy -- and is tagged `dependency` by default.
The Degraded ceiling is the part that holds unconditionally; the tag is routing, and passing your own
`tags` to `AddHttpResilienceHealthChecks` replaces it rather than adding to it.

**Degraded is HTTP 200.** ASP.NET Core's default `ResultStatusCodes` maps Healthy and Degraded both to `200`; only Unhealthy maps to `503`. So an alert on the *status code* of `/healthz/deps` never fires, however many circuits are open. Alert on `http.resilience.circuit_breaker.state` — the row above — and read the endpoint's `data` payload during triage. If you want the diagnostic endpoint to answer 503, opt in explicitly with `ResultStatusCodes`, and only on that endpoint; see README.md.

**Never gate liveness or readiness on it.** An open circuit means a downstream is unhealthy, not that this process is. Restarting the pod or removing it from the load balancer sheds capacity during a dependency outage and amplifies it — you would be converting a partial degradation into a self-inflicted one.

## Kubernetes and EKS

- **Connection lifetime.** `PooledConnectionLifetime` (default 2 minutes) is what bounds DNS staleness. When `Connection:Enabled` is true the package sets the `IHttpClientFactory` handler lifetime to infinite, because running both rotation mechanisms would cycle connection pools twice as often for no benefit. With `Connection:Enabled` false a client keeps what the factory gives it, which on .NET 10 is a `SocketsHttpHandler` with a two-minute pooled lifetime rotated every two minutes — already sound, so this section is for the settings the factory does not express and for making the pool's age a number you state rather than inherit.
- **Rolling deployments.** New pods start with empty circuit breaker state, so they will send `MinimumThroughput` requests to a failing dependency before their breaker can open. Expect a small surge on every rollout during a dependency incident.
- **Scaling.** Every process-local budget above scales linearly with replica count. Autoscaling multiplies your outbound rate limit.
- **Hedged clients.** The authorities a hedged client may call are fixed at deploy time. Adding a downstream host means a configuration change and a restart, the same as any other resilience change.
- **Shutdown.** Caller cancellation propagates through the pipeline and stops retries immediately, so in-flight requests do not survive a pod's grace period doing retry backoff.

## Changing configuration

Resilience configuration is read once at startup. Changing it requires a restart — see [ARCHITECTURE.md](ARCHITECTURE.md#configuration-reload).
