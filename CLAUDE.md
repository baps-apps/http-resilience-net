# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Prerequisites

- .NET 10 SDK (pinned in `global.json` with `rollForward: latestFeature`)

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
```

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

### Configuration section

The default section name is `"HttpResilienceOptions"` (from `HttpResilienceConfigurationKeys`). Multi-tenant/per-client scenarios use `IConfigurationSection` overloads.

### Sample project

The sample (`samples/HttpResilience.NET.Sample/`) is intentionally standalone — it uses `<ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>` and pins package versions directly in its `.csproj`, independent of the solution-wide `Directory.Packages.props`.

### Package management

All other projects use central package management via `Directory.Packages.props` at the repo root. When adding packages to `src/` or `tests/` projects, add the version to `Directory.Packages.props` and reference without a version in the `.csproj`.
