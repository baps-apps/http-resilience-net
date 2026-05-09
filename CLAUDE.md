# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Prerequisites

- .NET 10 SDK (pinned in `global.json` with `rollForward: latestFeature`)
- `nuget.config` clears defaults and adds `baps-apps-packages` (GitHub Packages) for `CodeStyle.NET` and `OpenTelemetry.NET`. Restore from a fresh clone needs a GitHub PAT configured for that source — see `scripts/README.md`.

## Build & Test Commands

```bash
# Build the solution
dotnet build

# Run all tests
dotnet test

# Run a single test project
dotnet test tests/HttpResilience.NET.Tests/
dotnet test tests/HttpResilience.NET.IntegrationTests/

# Run a specific test class or method
dotnet test --filter "FullyQualifiedName~ServiceCollectionExtensionsTests"

# Run the sample console app
dotnet run --project samples/HttpResilience.NET.Sample/

# Pack the NuGet package
dotnet pack src/HttpResilience.NET/

# Publish to GitHub Packages (requires GITHUB_PAT env var; see scripts/README.md)
pwsh scripts/publish-package.ps1
```

Solution file is `HttpResilience.NET.slnx` (XML solution format, not legacy `.sln`). `dotnet` CLI commands resolve it automatically from repo root.

## Code Conventions

- `TreatWarningsAsErrors` is enabled globally — all warnings are build errors.
- `GenerateDocumentationFile` is enabled — public APIs require XML doc comments.
- Nullable reference types (`#nullable enable`) are used throughout.
- Target framework is `net10.0`.

## Architecture

The library provides a standardized HTTP client resilience configuration wrapper over `Microsoft.Extensions.Http.Resilience` (Polly). The core abstraction is `HttpResilienceOptions` — a single configuration class that drives all pipeline behaviour.

### Two-step registration pattern

Callers must call both:
1. `services.AddHttpResilienceOptions(configuration)` — registers `IOptions<HttpResilienceOptions>` in DI with startup validation. Validation short-circuits when `Enabled = false`.
2. `.AddHttpClientWithResilience(configuration)` — reads config and wires `SocketsHttpHandler` + resilience pipeline to the named `HttpClient`.

Both are required. Most `AddHttpClientWithResilience` overloads read config directly (`section.Bind()`), but the `configureInnerPipeline` overload resolves `IOptions<HttpResilienceOptions>` from DI (requires `AddHttpResilienceOptions` to have been called first).

### Pipeline types

- **Standard** (include `"Standard"` in `PipelineOrder`) — `AddStandardResilienceHandler`: timeout, retry, circuit breaker, optional rate limiter.
- **Hedging** (include `"Hedging"` in `PipelineOrder`) — `AddStandardHedgingHandler`: sends multiple requests in parallel, first success wins.

### Pipeline ordering

A single `PipelineOrder` list controls all handler ordering:
- `PipelineOrder` — list of strategy names outermost→innermost, e.g. `["Fallback", "Bulkhead", "Standard"]`.
- Must contain exactly one of `Standard` or `Hedging`.
- Required when `Enabled = true`.
- Handlers are added innermost-first (reversed from the order list) in `AddHandlersInOrder`.

### Key files

| File | Purpose |
|------|---------|
| `src/.../Extensions/ServiceCollectionExtensions.cs` | All public registration APIs and internal pipeline wiring |
| `src/.../Options/HttpResilienceOptions.cs` | Root config class |
| `src/.../Internal/HttpStandardResilienceHandlerConfig.cs` | Builds standard pipeline config from options |
| `src/.../Internal/HttpStandardHedgingHandlerConfig.cs` | Builds hedging pipeline config from options |
| `src/.../Internal/SocketsHttpHandlerFactory.cs` | Creates `SocketsHttpHandler` from `ConnectionOptions` |
| `src/.../Internal/RateLimiterFactory.cs` | Creates `FixedWindow`, `SlidingWindow`, or `TokenBucket` rate limiter |
| `src/.../Abstractions/IHttpFallbackHandler.cs` | Custom fallback interface; return `null` to use synthetic response |
| `src/.../Internal/HttpResilienceLogging.cs` | Structured logging via `LoggerMessage` source generation |
| `src/.../Internal/CircuitBreakerStateTracker.cs` | Thread-safe circuit breaker state tracking for health checks |
| `src/.../Internal/HttpResilienceHealthCheck.cs` | ASP.NET health check for aggregate circuit breaker state |
| `src/.../Extensions/HealthCheckExtensions.cs` | `AddHttpResilienceHealthChecks()` registration |
| `src/.../Extensions/HttpResilienceTelemetryExtensions.cs` | `AddHttpResilienceTelemetry()` and `HttpResilienceMeteringEnricher` |

### Configuration section

The default section name is `"HttpResilienceOptions"` (from `HttpResilienceConfigurationKeys`). Multi-tenant/per-client scenarios use `IConfigurationSection` overloads.

### Sample project

The sample (`samples/HttpResilience.NET.Sample/`) consumes the published `HttpResilience.NET` NuGet package (not a `ProjectReference`), so it exercises the same surface external consumers see. The `HttpResilience.NET` `PackageVersion` in `Directory.Packages.props` controls which version it pulls.

### Package management

All projects use central package management via `Directory.Packages.props` at the repo root. When adding packages, add the version to `Directory.Packages.props` and reference without a version in the `.csproj`.

### Build hardening (`Directory.Build.props`)

Applies to all projects: `TreatWarningsAsErrors=true`, `Deterministic=true`, `GenerateDocumentationFile=true`, embedded PDBs with SourceLink (`Microsoft.SourceLink.GitHub`). New projects inherit these automatically.

### Deeper docs

`docs/` holds long-form references that supplement this file: `ARCHITECTURE.md`, `IMPLEMENTATION.md`, `RECIPES.md`, `OPERATIONS.md`, `RUNBOOK.md`, `TROUBLESHOOTING.md`, `SECURITY-GOVERNANCE.md`, `VERSIONING.md`, `PRODUCTION-CHECKLIST.md`, `COMPARISON.md`. Consult them for nuance beyond what's summarised here.
