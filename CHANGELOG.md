# Changelog

All notable changes to **HttpResilience.NET** are documented in this file.

This project follows [Semantic Versioning](https://semver.org/):
**MAJOR** for breaking API/behavior changes, **MINOR** for backwards-compatible features, **PATCH** for bug fixes.

## [1.0.0] - 2026-05-09

Initial public release. Targets `net10.0`. Built for high-throughput microservices: hot paths are allocation-light, telemetry enrichment is cache-backed, and per-client resilience state is isolated by design.

### Added

#### Pipelines

- **Standard pipeline** — timeout, retry, and circuit breaker strategies via `AddStandardResilienceHandler`. Include `"Standard"` in `PipelineOrder` to enable.
- **Hedging pipeline** — multiple concurrent requests with first-success-wins semantics for tail-latency-sensitive calls via `AddStandardHedgingHandler`. Include `"Hedging"` in `PipelineOrder` to enable. Mutually exclusive with Standard.
- **Retry strategy** — exponential, linear, and constant backoff modes with optional jitter and `Retry-After` honoring. `BaseDelaySeconds` is `double` for sub-second delays.
- **Rate limiting** — optional Polly rate limiter (`FixedWindow`, `SlidingWindow`, `TokenBucket`). Registered as a per-client keyed singleton (key = HttpClient name), so each named HttpClient gets its own limiter and the container owns disposal. Resolve directly via `GetRequiredKeyedService<RateLimiter>(clientName)` if needed.
- **Fallback** — optional synthetic response on total failure, or custom fallback via `IHttpFallbackHandler` (return `null` to fall through to synthetic response). Synthetic-only path is fully synchronous (no async state-machine allocation).
- **Bulkhead** — optional Polly concurrency limiter to cap outbound concurrent requests.

#### Configuration

- **`HttpResilienceOptions`** root configuration model with data-annotation validation, bound from `IConfiguration` or `IConfigurationSection`.
- **`PipelineOrder`** list controlling strategy ordering outermost→innermost (e.g. `["Fallback", "Bulkhead", "Standard"]`). Required when `Enabled = true`; must contain exactly one of `Standard` or `Hedging`.
- **`PipelineSelection:Mode`** (`None` / `ByAuthority`) for per-authority pipeline instances.
- **`Enabled`** feature flag — set to `false` to disable all resilience without code changes; validation short-circuits when disabled.
- **`ConnectionOptions.EnableMultipleHttp2Connections`** (default `true`) — maps to `SocketsHttpHandler.EnableMultipleHttp2Connections`. Allows multiple concurrent HTTP/2 TCP connections to a single origin so high-throughput clients are not bottlenecked on one connection's stream limit. Set to `false` to opt out.

#### Public API

- **`AddHttpResilienceOptions`** — registers `IOptions<HttpResilienceOptions>` in DI with startup validation. Validator registered via `TryAddEnumerable` so duplicate registrations across multiple HttpClients do not run validation N times.
- **`AddHttpClientWithResilience`** — wires `SocketsHttpHandler` plus resilience pipeline onto any named or typed `HttpClient`. Overloads accept `IConfiguration`, `IConfigurationSection`, or DI-resolved options.
- **`AddHttpResilienceTelemetry`** — metrics enrichment with `error.type`, `request.name`, `request.dependency.name` tags.
- **`AddHttpResilienceHealthChecks`** — aggregate circuit breaker health check for ASP.NET.
- **`configurePipeline`** delegate — add extra strategies outermost.
- **`configureInnerPipeline`** delegate — full code-level control over the inner pipeline (resolves options from DI).
- **`CircuitBreakerStateTracker.Enumerate()`** — public method returning a live, copy-free enumerator over tracked client states; backs the allocation-light health check path.

#### Internals

- **`SocketsHttpHandlerFactory`** — configurable pool size, idle timeout, connection lifetime, connect timeout, and HTTP/2 multi-connection toggle.
- **Structured logging** for retry, circuit breaker, and fallback events via `LoggerMessage` source generation. Retry log carries `int? StatusCode` as a discrete field (no per-retry `HttpStatusCode.ToString()` allocation).
- **`CircuitBreakerStateTracker`** — thread-safe state tracking backing the health check, with manual struct-enumerator iteration in `HasOpenCircuits` (no LINQ, no closure).
- **`PipelineStrategyNames.Allowed`** — backed by `FrozenSet<string>` for fast `Contains` lookups during validation.

#### Tooling

- Sample console application in `samples/HttpResilience.NET.Sample` (standalone package versions, independent of solution-wide central package management).
- Unit and integration test suites.
- Documentation: [IMPLEMENTATION](docs/IMPLEMENTATION.md), [ARCHITECTURE](docs/ARCHITECTURE.md), [COMPARISON](docs/COMPARISON.md), [OPERATIONS](docs/OPERATIONS.md), [RUNBOOK](docs/RUNBOOK.md), [RECIPES](docs/RECIPES.md), [TROUBLESHOOTING](docs/TROUBLESHOOTING.md), [VERSIONING](docs/VERSIONING.md), [SECURITY-GOVERNANCE](docs/SECURITY-GOVERNANCE.md), [PRODUCTION-CHECKLIST](docs/PRODUCTION-CHECKLIST.md).

### Performance

The following choices keep the per-request hot path allocation-light by design:

- **Metering enricher** ([HttpResilienceMeteringEnricher](src/HttpResilience.NET/Internal/HttpResilienceMeteringEnricher.cs)):
  - Canonical `HttpStatusCode.{n}` strings precomputed into a `FrozenDictionary<int, string>` lookup table — no per-event interpolation.
  - Tag enumeration uses an index-based `for` loop over `IList<KeyValuePair<>>` instead of `foreach`, avoiding the interface-enumerator allocation.
  - Single-pass scan over `context.Tags` collects both `pipeline.name` and `strategy.name` in one traversal.
  - `pipeline/strategy` composite request name cached in a `ConcurrentDictionary` keyed by the small fixed pipeline/strategy name set; saturates fast then is allocation-free.
  - `scheme://host[:port]` dependency name cached per `Uri` instance via `ConditionalWeakTable<Uri, string>` so repeated calls to the same host are allocation-free.
  - `tag.Value as string` fast path avoids virtual `ToString()` when the underlying value is already a string.
- **Health check** — `HttpResilienceHealthCheck` lazily allocates the data dictionary and unhealthy list only when a non-closed breaker is observed; closed-state probes return without allocating. `state.ToString()` replaced by a small static `string[]` lookup keyed by `(int)CircuitState`.
- **Primary handler factory** — uses `IOptionsMonitor<HttpResilienceOptions>` (singleton) instead of `IOptionsSnapshot` (scoped) inside `ConfigurePrimaryHttpMessageHandler` so handler builds do not allocate a fresh per-scope options copy. Hot-reload semantics preserved.
- **Fallback fast path** — `ExecuteFallbackAsync` builds the synthetic response synchronously when no custom `IHttpFallbackHandler` is configured; the async state machine is reserved for the slow path.

## Pre-1.0 history

No public releases prior to 1.0.0.
