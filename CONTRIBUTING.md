# Contributing

## Prerequisites

- .NET 10 SDK 10.0.404 or a later 10.0.4xx patch (`global.json`, `rollForward: latestPatch`). The floor is the
  current security release, so an unpatched SDK cannot build or publish the package; raise it with each one.
- A GitHub PAT with `read:packages`: `nuget.config` restores `CodeStyle.NET` and the package validation baseline
  (`HttpResilience.NET`, previous release) from the internal feed, so `dotnet restore` and `dotnet pack` fail
  without it ([scripts/README.md](scripts/README.md#restoring-from-baps-apps-packages)).
- Python 3 for the documentation checks; PowerShell 7 (`pwsh`) for the publish scripts.

## Build and test

```bash
dotnet build -c Release
dotnet test -c Release        # the suite of record
```

**Use `-c Release`.** Debug skips every `[ReleaseOnlyFact]` (the allocation ceilings) rather than running it.
After a root build, build a test project explicitly before trusting a `--no-build` run.

| Project | Contents |
| --- | --- |
| `tests/HttpResilience.NET.Tests` | Behavior (one file per guarantee, `Behavior/`) and unit tests |
| `tests/HttpResilience.NET.IntegrationTests` | `TestServer` end to end; real Kestrel sockets for redirects and HTTP/2 |
| `tests/HttpResilience.NET.AotSmoke` | Native AOT binary running `SmokeScenarios`: bound values, both pipelines, `Connection:Enabled` and host start (also run under JIT by the integration tests) |
| `benchmarks/HttpResilience.NET.Benchmarks` | BenchmarkDotNet; quoted reports live in `docs/benchmarks/` |
| `samples/HttpResilience.NET.Sample` | Console sample on the generic host, so startup validation, the startup client check and the startup notices run; `SampleConfigurationTests` validates its `appsettings.json` |

## Release gates

There is no CI for changes. Run these locally before merging and before releasing (the one workflow is the
weekly [platform-drift run](#platform-drift)):

```bash
dotnet test -c Release
dotnet test -c Release -p:TestMinimumDependencies=true  # the same suite on the minimum dependency versions the
                                                      # package declares (Directory.Packages.props)
dotnet format whitespace --verify-no-changes          # whitespace only: full `dotnet format` reports
                                                      # trim warnings the real build never emits
dotnet list package --vulnerable --include-transitive # any hit blocks the change
dotnet list src/HttpResilience.NET package --deprecated --include-transitive  # any hit blocks the change; the
                                                      # shipped project only: the tests need the deprecated
                                                      # Polly.Extensions.Http, the v7 stack event 20 detects
python3 scripts/check-benchmark-docs.py               # docs/benchmarks allocation figures vs the raw reports
python3 scripts/check-benchmark-docs.py --against <dir>/results  # vs a fresh run, when hot-path code changed
python3 scripts/check-readme-links.py                 # README links absolute: it is the package page
python3 scripts/check-public-api-shipped.py           # releases: PublicAPI.Unshipped.txt must be empty
dotnet pack src/HttpResilience.NET -c Release          # package validation against the last published version

# Proves trim/AOT at run time (a trimmed reflective binder leaves defaults instead of throwing): publishes and
# runs the AotSmoke binary. Plain `dotnet test` runs the same scenarios under JIT.
HTTPRESILIENCE_AOT_SMOKE=1 dotnet test tests/HttpResilience.NET.IntegrationTests -c Release --filter "FullyQualifiedName~NativeAotSmokeTests"
```

The Native AOT publish needs the platform's native toolchain (Xcode command-line tools on macOS, `clang` and
`zlib` headers on Linux). The Homebrew .NET SDK on macOS links against Homebrew's OpenSSL: the test appends
`/opt/homebrew/lib` (or `/usr/local/lib`) to `LIBRARY_PATH` when `libssl.dylib` is there. If the link still fails
with `ld: library 'ssl' not found`, install it (`brew install openssl@3`) or put its `lib` directory on
`LIBRARY_PATH`.

### Platform drift

The package declares minimum dependency versions with no upper bound, and its handler groups rely on list order
in `IHttpClientFactory` that the platform does not document. [`.github/workflows/platform-drift.yml`](.github/workflows/platform-drift.yml)
runs the Release suite every Monday, and on demand (**Actions > Platform drift > Run workflow**), on four
channels: `pinned` (the versions in `Directory.Packages.props`, the control), `current` (newest stable within
the `global.json` .NET major: the graph most consumers resolve), `latest` (newest stable Microsoft.Extensions,
Http.Resilience and Polly on nuget.org, a newer .NET major included) and `preview` (prereleases included). A failed
scheduled run opens or comments on an issue labelled `platform-drift`; the run's summary lists the versions
used, and its artifacts hold the test results and the resolved package graph.

Reproduce a channel locally (bash, which splits the arguments):

```bash
dotnet test -c Release $(python3 scripts/resolve-platform-versions.py latest)
```

When `current` passes, raise the test versions in `Directory.Packages.props` to match, so `pinned` follows; the
tests target `net10.0`, so `pinned` never moves to `latest`'s newer major. The feed authentication step needs
the `CodeStyle.NET` package to grant this repository read access (package settings, **Manage Actions access**),
or a `PACKAGES_READ_TOKEN` secret with `read:packages`.

## Rules for changes

- **The package configures `Microsoft.Extensions.Http.Resilience`; it implements no strategy.** The rules that
  keep it that way are in [CLAUDE.md](CLAUDE.md) (Architecture) and
  [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md#design-rules). Expect push-back on a change that breaks one.
- **Never read configuration at registration.** Anything built from options goes in a factory or delegate that
  reads `IOptionsMonitor` when the client is built. A key that decides whether a handler exists adds a predicate
  to that handler's `AddHandlerGroup` call, and its pipeline delegate must add nothing when the predicate is
  false.
- **Each validation rule lives in one place, never in data annotations**: `HttpResilienceOptionsValidator`
  (values, platform limits), `NamedPipelineOptionsValidator` (per client, inert keys), `RootSectionRules`
  (root-only), `RenamedKeyRules` / `UnknownKeyRules` / `SchemaVersionRules` (keys) and
  `UnusedClientSectionValidator`. Messages name the key, value, expectation and reason.
- **Every public member goes in `src/HttpResilience.NET/PublicAPI.Unshipped.txt` in the same commit**; an
  unrecorded change is a build error (RS0016 quotes the line).
- **No reflective option binding.** The configuration binding source generator is what makes the trim/AOT
  declarations true.
- **Dependencies** go through `Directory.Packages.props`. The library references each at its **minimum**
  version and references directly every package whose types it uses; raise a minimum only when an API needs it.
- **New or changed key**: add it to the table in [docs/CONFIGURATION.md](docs/CONFIGURATION.md)
  (`ConfigurationReferenceTests` fails otherwise), update the sample's `appsettings.json` (and
  `SampleConfigurationTests` if you add a client), and update `README.md` / `docs/RECIPES.md` where relevant.
- **Behavior change**: add a `CHANGELOG.md` entry under `[Unreleased]`, classified by
  [docs/VERSIONING.md](docs/VERSIONING.md) (a default change and a new validation rule are both MAJOR).

## Tests

- Assert behavior (origin call counts, delivered bodies, gauge readings), never just "did not throw".
- **A test that cannot fail proves nothing.** For a safety test, make the production change that should break
  it, watch it fail, revert.
- "A consumer also does X to this client" is a standing axis: `Behavior/ConsumerBoundaryTests.cs`.
- Measure platform behavior (`MeterListener`, origin counts); do not assume it.
- No sleeps where a deterministic mechanism exists (`RecordingHandler.WaitForCountAsync`, `FakeTimeProvider`), and
  wait for a condition, never for "nothing changed for N ms".
- Every test states `Timeout = TestTimeouts.Default` and passes `TestContext.Current.CancellationToken` to
  anything that takes one; `EveryTest_HasATimeout` and xUnit1051 enforce both.

## Releasing

1. Run the [release gates](#release-gates).
2. Move `PublicAPI.Unshipped.txt` into `PublicAPI.Shipped.txt`, review the diff as the API change list, then
   run `python3 scripts/check-public-api-shipped.py`. A `*REMOVED*` line is not copied: delete the matching line
   from Shipped instead.
3. After publishing, set `PackageValidationBaselineVersion` to the version just published and empty
   `CompatibilitySuppressions.xml` (its breaks are now in the baseline). Update `PackageReleaseNotes` in the
   csproj for the next release, leading with anything that changes behavior without a compile error.
4. Move `[Unreleased]` in `CHANGELOG.md` under the new version, and replace `Unreleased` in the version's
   heading (`## [3.0.0] - Unreleased`) with the publish date.
5. Commit steps 2 and 4 and the `<Version>` bump. The script refuses a working tree with uncommitted or
   untracked changes, builds from that commit and tags it; step 3 goes in a commit after publishing.
6. `pwsh scripts/publish-package.ps1` ([scripts/README.md](scripts/README.md#package-publishing)). It runs the
   release gates above (except a fresh benchmark run) and the Release test suite, and stops on the first
   failure; it signs and verifies the package, and refuses to start without a certificate
   ([Package signing](scripts/README.md#package-signing)) unless `-AllowUnsigned` is passed. Publishing
   a version that already exists replaces it and moves its tag. Consumers that already restored it keep the
   old copy in their NuGet cache until they clear it (`dotnet nuget locals all --clear`).

### Patching the previous major

Security fixes only, inside the window in [SECURITY.md](SECURITY.md#supported-versions) (2.x until 2027-05-14):

1. Branch `release/2.x` from the tag `v2.0.0` (later patches: from the branch). Its first commit copies the
   current `scripts/` directory, so the patch is published with today's release gates and signing.
2. Make the fix with a test, set `<Version>` to the next patch (`2.0.1`), and add its `## [2.0.1]` section to that
   branch's `CHANGELOG.md`.
3. Publish from the branch with `pwsh scripts/publish-package.ps1`, then add the same section to `main`'s
   CHANGELOG, below the current major's.

## License

Proprietary. Copyright (c) BAPS, all rights reserved. Contributions are work for the organization under the
same terms. The package is published to GitHub Packages under `baps-apps` and must never be pushed to
nuget.org.
