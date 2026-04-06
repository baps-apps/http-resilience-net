# Changelog

All notable changes to **HttpResilience.NET** are documented in this file.

This project follows [Semantic Versioning](https://semver.org/):
**MAJOR** for breaking API/behavior changes, **MINOR** for backwards-compatible features, **PATCH** for bug fixes.

## [1.0.0] - Initial release

### Added

- **Standard pipeline** — timeout, retry (exponential/linear/constant backoff with optional jitter and `Retry-After` support), and circuit breaker strategies via `AddHttpClientWithResilience`.
- **Hedging pipeline** — multiple concurrent requests with first-success-wins semantics for tail-latency-sensitive calls.
- **Rate limiting** — optional Polly rate limiter (FixedWindow, SlidingWindow, TokenBucket) around the inner handler.
- **Fallback** — optional synthetic response on total failure, or custom fallback via `IHttpFallbackHandler`.
- **Bulkhead** — optional Polly concurrency limiter to cap outbound concurrent requests.
- **HttpResilienceOptions** configuration model with data-annotation validation and binding from `IConfiguration` / `IConfigurationSection`.
- **AddHttpResilienceOptions** extension for one-time options registration with startup validation.
- **AddHttpClientWithResilience** extension for wiring resilience onto any named or typed `HttpClient`.
- **AddHttpResilienceTelemetry** extension for resilience metrics enrichment (`error.type`, `request.name`, `request.dependency.name`).
- **AddHttpResilienceHealthChecks** extension for aggregate circuit breaker health monitoring.
- **SocketsHttpHandler factory** with configurable connection pool, idle timeout, connection lifetime, and connect timeout.
- **PipelineOrder** list for controlling strategy ordering (outermost to innermost) without code changes.
- **PipelineSelection:Mode** (`None` / `ByAuthority`) for per-authority pipeline instances.
- **Enabled** feature flag — set to `false` to disable all resilience without code changes.
- **Structured logging** via `LoggerMessage` source generation for retry, circuit breaker, and fallback events.
- **Custom pipeline delegate** (`configurePipeline`) for adding extra strategies outermost.
- **Custom inner pipeline delegate** (`configureInnerPipeline`) for full code-level control.
- Sample console application in `samples/HttpResilience.NET.Sample`.
- Unit tests and integration tests.
- Documentation: IMPLEMENTATION, ARCHITECTURE, COMPARISON, OPERATIONS, RUNBOOK, RECIPES, TROUBLESHOOTING, VERSIONING, SECURITY-GOVERNANCE, and PRODUCTION-CHECKLIST.
