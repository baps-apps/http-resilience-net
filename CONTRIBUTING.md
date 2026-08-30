# Contributing to HttpResilience.NET

Thank you for your interest in contributing! This guide covers what you need to get started.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) or later
- A Git client
- An editor that supports C# (Visual Studio, VS Code with C# Dev Kit, Rider, etc.)

## Getting started

```bash
git clone https://github.com/baps-apps/http-resilience-net.git
cd http-resilience-net
dotnet restore
dotnet build
```

## Running tests

```bash
# Behavior and unit tests
dotnet test tests/HttpResilience.NET.Tests

# Against a real Kestrel and real sockets
dotnet test tests/HttpResilience.NET.IntegrationTests

# All tests
dotnet test
```

**Run `dotnet test -c Release` before calling a suite green.** Bare `dotnet test` builds Debug, where every
`[ReleaseOnlyFact]` — the allocation ceilings — reports as skipped rather than run. CI runs both legs.

The other gates CI applies, all runnable locally:

```bash
dotnet format whitespace --verify-no-changes          # whitespace only, on purpose — see CLAUDE.md
dotnet list package --vulnerable --include-transitive # CI fails on any hit
python3 scripts/check-benchmark-docs.py               # docs/benchmarks figures vs the raw reports
dotnet publish tests/HttpResilience.NET.AotSmoke -c Release -r osx-arm64   # proves the AOT claim
```

## Project structure

```text
src/
  HttpResilience.NET/          Core library (NuGet package)
tests/
  HttpResilience.NET.Tests/              Behavior + unit tests
  HttpResilience.NET.IntegrationTests/   Real Kestrel and real sockets
  HttpResilience.NET.AotSmoke/           Native AOT publish, asserts bound values at run time
benchmarks/
  HttpResilience.NET.Benchmarks/         BenchmarkDotNet; reports land in docs/benchmarks/
samples/
  HttpResilience.NET.Sample/   Console sample app, smoke-tested in CI
docs/                          Extended documentation
```

## Making changes

1. **Fork** the repository and create a branch from `main`.
2. **Write or update tests** for any new functionality or bug fix.
3. **Follow existing code style** – the solution uses central package management (`Directory.Packages.props`), nullable reference types, and implicit usings.
4. **Keep commits focused** – one logical change per commit with a clear message.
5. **Run the full test suite** before pushing: `dotnet test`.
6. **Open a pull request** against `main` with a description of _what_ changed and _why_.

## Coding guidelines

- Target `net10.0`.
- Use nullable annotations (`#nullable enable`) throughout.
- Prefer `IConfiguration` / `IConfigurationSection` binding over hard-coded values.
- Add XML doc comments on public APIs (`GenerateDocumentationFile` is enabled).
- Keep methods small and single-purpose; resilience strategies should be independently testable.
- Avoid adding new package dependencies unless strictly necessary – changes go through `Directory.Packages.props`.
- **Record every public member in `src/HttpResilience.NET/PublicAPI.Unshipped.txt` in the same commit.**
  `Microsoft.CodeAnalysis.PublicApiAnalyzers` plus `TreatWarningsAsErrors` make an unrecorded addition,
  removal or signature change a build error, and RS0016 quotes the exact line to add. Moving Unshipped into
  Shipped is a release step, not a per-PR one.
- **No reflective option binding.** `EnableConfigurationBindingGenerator` is what makes the `IsTrimmable` and
  `IsAotCompatible` declarations true; a reflective binding call reintroduced here fails the build.
- The package **configures** `Microsoft.Extensions.Http.Resilience` and implements no strategy of its own.
  The twelve rules that keep it that way are in [CLAUDE.md](CLAUDE.md) and
  [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md); a change that violates one should expect to be pushed back on.

## Configuration and options changes

When adding or changing a key on `HttpResilienceOptions`:

- **Add the rule to `Internal/HttpResilienceOptionsValidator.cs`, not a data annotation.** Annotations cannot
  express a `TimeSpan` range or a relationship between two properties, and they are scoped to a single options
  name so they silently skip every per-client registration. Every message names the config path, the value,
  the expectation and the reason.
- **Add the key to the table in [docs/CONFIGURATION.md](docs/CONFIGURATION.md).** That is the only place the
  whole schema is listed, and `ConfigurationReferenceTests` fails the build when a key is missing from it.
- Decide whether the key is a _value_ option or a _structural_ one. A new key is a value option by default;
  one that decides **which handlers exist** goes in `Internal/StructuralDecisions.cs`, and that should be a
  deliberate act. Anything built from options belongs in a factory that reads `IOptionsMonitor`, never in a
  closure over the registration snapshot.
- Update the sample `appsettings.json` in `samples/HttpResilience.NET.Sample`; `SampleConfigurationTests`
  holds it to the validator, and CI runs the sample.
- Update `README.md` where the concept is explained, and [docs/RECIPES.md](docs/RECIPES.md) if there is a
  configuration worth showing.

## Tests

Tests assert **behavior**, not that registration does not throw — the 1.0 suite had 64 tests that almost all
asserted "did not throw" and caught none of the four critical defects.

- One file per guarantee under `Behavior/`, wired through the public API.
- **A test that cannot fail proves nothing.** Name the production change that would make a new safety test
  fail, then make that change and watch it fail.
- "A consumer also does X to this client" is a standing axis, in `Behavior/ConsumerBoundaryTests.cs`.
- Claims about platform behavior get measured — a `MeterListener`, an origin call count, the health-state
  dictionary — not assumed.

## Commit messages

Use clear, imperative-mood commit messages:

```text
Add per-authority pipeline selection mode
Fix retry jitter calculation for sub-second delays
Update the configuration reference for TokenBucket keys
```

## Pull request checklist

- [ ] Code compiles without warnings (`dotnet build`). `TreatWarningsAsErrors` is on, so this also gates
      analyzer, code-style and XML-doc regressions.
- [ ] All existing and new tests pass in **both** configurations: `dotnet test` and `dotnet test -c Release`.
- [ ] Public API changes include XML doc comments **and** a line in `PublicAPI.Unshipped.txt`.
- [ ] `README.md`, [docs/CONFIGURATION.md](docs/CONFIGURATION.md) and the other relevant `docs/` pages are
      updated if behavior changed. A claim in a document that nothing compares against the code is the kind
      that drifts.
- [ ] `CHANGELOG.md` has an entry, under `[Unreleased]` if no version is being cut, classified by
      [docs/VERSIONING.md](docs/VERSIONING.md) — a default change and a new validation rule are both MAJOR.
- [ ] No secrets, credentials, or personal paths are committed.

## Reporting issues

Open a [GitHub issue](https://github.com/baps-apps/http-resilience-net/issues) with:

- A clear title and description.
- Steps to reproduce (if applicable).
- Expected vs. actual behavior.
- .NET SDK version and OS.

## Versioning

This project follows [Semantic Versioning](https://semver.org/). See [docs/VERSIONING.md](docs/VERSIONING.md) for the full policy.

## License

Proprietary. Copyright (c) BAPS, all rights reserved. This repository carries no open-source license and
grants no rights outside the organization; contributions are made as work for the organization and are
covered by the same terms. The package is published to GitHub Packages under `baps-apps` and must not be
pushed to nuget.org.
