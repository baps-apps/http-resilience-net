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

The repository contains unit tests and integration tests:

```bash
# Unit tests
dotnet test tests/HttpResilience.NET.Tests

# Integration tests
dotnet test tests/HttpResilience.NET.IntegrationTests

# All tests
dotnet test
```

## Project structure

```
src/
  HttpResilience.NET/          Core library (NuGet package)
tests/
  HttpResilience.NET.Tests/              Unit tests
  HttpResilience.NET.IntegrationTests/   Integration tests
samples/
  HttpResilience.NET.Sample/   Console sample app
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

## Configuration and options changes

When modifying `HttpResilienceOptions` or adding new resilience strategies:

- Add data-annotation validation attributes where appropriate.
- Update the sample `appsettings.json` in `samples/HttpResilience.NET.Sample`.
- Update `docs/IMPLEMENTATION.md` and the options-reference table in `README.md`.

## Commit messages

Use clear, imperative-mood commit messages:

```
Add per-authority pipeline selection mode
Fix retry jitter calculation for sub-second delays
Update README options-reference table
```

## Pull request checklist

- [ ] Code compiles without warnings (`dotnet build`).
- [ ] All existing and new tests pass (`dotnet test`).
- [ ] Public API changes include XML doc comments.
- [ ] `README.md` and relevant `docs/` pages are updated if behavior changed.
- [ ] `CHANGELOG.md` has an entry under `[Unreleased]`.
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

By contributing you agree that your contributions will be licensed under the [MIT License](LICENSE).
