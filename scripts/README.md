# Scripts

Scripts for restoring from, and publishing the **HttpResilience.NET** NuGet package to, GitHub Packages, plus
the documentation checks run before a release.

| Script | Purpose |
| --- | --- |
| `publish-package.ps1` | Build, pack and push the package; then tag and create a GitHub release |
| `delete-package.ps1` | Delete one version of the package from GitHub Packages |
| `check-benchmark-docs.py` | Fail if an allocation figure in `docs/benchmarks/README.md` disagrees with its raw report, or with a fresh run (`--against`) |
| `check-public-api-shipped.py` | Fail if `PublicAPI.Unshipped.txt` still lists any API (run when cutting a release) |
| `check-readme-links.py` | Fail if `README.md` links a repository file by relative path (breaks on the package page) |
| `resolve-platform-versions.py` | Print the `-p:` arguments that move tests to the platform versions of one channel (`pinned`, `current`, `latest` or `preview`), for the platform-drift workflow ([CONTRIBUTING.md](../CONTRIBUTING.md#platform-drift)) |

**Cross-platform:** the PowerShell scripts run on Windows, macOS, and Linux with PowerShell 7 or later (`pwsh`).
Windows PowerShell 5.1 (`powershell.exe`) is not supported; each script declares `#Requires -Version 7.0` and
stops with a clear error there. The Python scripts need Python 3 and only the standard library.

## Prerequisites

**PowerShell 7 or later (`pwsh`)** must be installed, including on Windows:

- **Windows**: [Microsoft Store](https://aka.ms/powershell) or [GitHub](https://github.com/PowerShell/PowerShell/releases)
- **macOS**: `brew install --cask powershell`
- **Linux**: [Installation guide](https://learn.microsoft.com/en-us/powershell/scripting/install/installing-powershell-on-linux)

Verify:

```bash
pwsh --version
```

---

## Restoring from baps-apps-packages

The repository's `nuget.config` clears the default sources and adds `baps-apps-packages`
(`https://nuget.pkg.github.com/baps-apps/index.json`), mapped to `CodeStyle.NET` and `HttpResilience.NET` (the
previous release, downloaded by `dotnet pack` as the package validation baseline). GitHub Packages requires
authentication even to read, so a fresh clone does not restore or pack without a PAT with `read:packages`. A service
consuming `HttpResilience.NET` needs the same source and credentials.

Keep the credentials out of the committed `nuget.config`: store them in your user-level NuGet config under the
same source name, for example:

```bash
dotnet nuget add source https://nuget.pkg.github.com/baps-apps/index.json \
  --name baps-apps-packages \
  --username YOUR_GITHUB_USERNAME \
  --password YOUR_GITHUB_PAT \
  --store-password-in-clear-text \
  --configfile ~/.nuget/NuGet/NuGet.Config
```

---

## Package publishing

`publish-package.ps1` runs every release gate, tests, builds, packs, signs and pushes the package, then creates a
git tag and a GitHub release. It needs PowerShell 7, the .NET SDK, Python 3 and the native toolchain the Native
AOT smoke test links with.

What it does, in order:

1. **Refuses to run on a dirty working tree**: any uncommitted change or untracked file (an untracked `.cs`
   under `src/` would be compiled in) stops it before anything runs. It records the `HEAD` commit, and reads the
   version from the committed `<Version>` in the `.csproj`, which it never edits. A version passed as an
   argument must equal it; otherwise the script stops and asks you to commit the version bump first.
   **Refuses to start without a signing certificate** (see [Package signing](#package-signing)) unless
   `-AllowUnsigned` is passed.
2. **Release gates** ([CONTRIBUTING.md](../CONTRIBUTING.md#release-gates)); the first failure stops the script
   before anything is packed or pushed:
   - `dotnet format whitespace --verify-no-changes`
   - `dotnet list package --vulnerable --include-transitive`: any vulnerable package fails it
   - `dotnet list package --deprecated --include-transitive` on the shipped project: any deprecated package fails
     it. The test project is excluded because it needs the deprecated `Polly.Extensions.Http` it tests against
   - `check-benchmark-docs.py` (against the checked-in reports; a fresh `--against` run stays a manual step,
     because it takes about half an hour), `check-readme-links.py` and `check-public-api-shipped.py`
   - the Native AOT smoke test (`HTTPRESILIENCE_AOT_SMOKE=1`). On macOS it adds Homebrew's `lib` directory to
     `LIBRARY_PATH` itself, which the Homebrew .NET SDK's native link needs.
3. `dotnet test` on the solution in Release, the suite of record (Debug skips the allocation ceilings), twice:
   on the minimum dependency versions the package declares (`-p:TestMinimumDependencies=true`), then on the
   latest. Any failure stops the script before anything is packed or pushed.
4. `dotnet build` and `dotnet pack` in Release into `nupkgs/` (`pack` runs package validation against
   `PackageValidationBaselineVersion`), with `-p:ContinuousIntegrationBuild=true`: source paths become `/_/`, so
   the package is the same from any machine and carries no local path. Then it checks again that the tree is
   clean and `HEAD` is still the recorded commit; if either changed during the run, nothing is pushed.
5. Signs the package, then verifies the signature; a failure of either stops the script. Skipped only under
   `-AllowUnsigned`, with a warning.
6. Pushes `nupkgs/HttpResilience.NET.<version>.nupkg`. **If that version already exists on the feed, it deletes
   it and pushes again.** This is intended: a version may be republished. Consumers that already restored it
   keep the old copy in their NuGet cache until they clear it (`dotnet nuget locals all --clear`).
7. Creates the tag `v<version>` on the commit recorded in step 1, the one the package was built from,
   deleting an existing local and remote tag of that name first, and pushes it to `origin`.
8. Deletes any GitHub release (including drafts) for that tag and creates a new one, with the matching
   `## [<version>]` section of `CHANGELOG.md` as the body, or an empty body if there is none.

Tag and release failures are reported as warnings; the package push has already happened.

### GitHub PAT

1. Go to <https://github.com/settings/tokens>
2. Generate new token (classic)
3. Scopes: `write:packages` and `read:packages`; `delete:packages` for `delete-package.ps1` (and for the
   overwrite in step 6); `repo` (or `public_repo`) for the GitHub release in step 8
4. Set the token as `GITHUB_PAT`, or pass it as the second argument

### Configuration

- **Package ID:** `HttpResilience.NET` (`PackageId` in the `.csproj`)
- **Project:** `src/HttpResilience.NET/HttpResilience.NET.csproj`
- **Output:** `nupkgs/` at repo root
- **Namespace:** GitHub organization that hosts the feed and the repository (default: `baps-apps`, in both
  scripts). Override with `GITHUB_NAMESPACE` only if the repository moves to a different organization; the
  version lookup and delete calls use the organization packages API, so a user account does not work. The
  repository name `http-resilience-net` is fixed in the script.

### Publish

```powershell
# Set PAT (required)
$env:GITHUB_PAT = "your_token_here"

# Commit the release first (<Version>, CHANGELOG, PublicAPI files); the tree must be clean
git status --porcelain --untracked-files=all   # prints nothing

# Publish the committed <Version>
pwsh scripts/publish-package.ps1

# Or state the version you expect: stops unless the committed <Version> is 3.0.1
pwsh scripts/publish-package.ps1 3.0.1
```

**macOS/Linux:** the scripts carry a `#!/usr/bin/env pwsh` shebang, so after `chmod +x` they run directly:

```bash
chmod +x scripts/publish-package.ps1
./scripts/publish-package.ps1
```

### Package signing

Signing is required: the script refuses to start without a certificate unless `-AllowUnsigned` is passed. A
signed package lets a consumer prove it came from BAPS and was not altered on the feed; GitHub Packages accepts
signed packages as they are.

**1. Get a code-signing certificate.** Either:

- **From the organization's internal CA** (enough for a private feed): a certificate with the Code Signing
  extended key usage (`1.3.6.1.5.5.7.3.3`) and a 2048-bit or larger RSA key, issued to the team or the
  organization, not to a person.
- **From a public CA** (DigiCert, Sectigo and others): needed only if the package is ever consumed outside
  machines that trust the internal CA.

Self-signed certificates sign, but fail verification (`NU3018: untrusted root`) everywhere, the publishing
machine included.

**2. Make it available to the script.** One of:

```powershell
# A .pfx file. Keep it outside the repository; the password from a secret store, not shell history.
$env:NUGET_SIGN_CERT_PATH = "/secure/path/baps-codesign.pfx"
$env:NUGET_SIGN_CERT_PASSWORD = "..."

# Or a certificate already in the current user's certificate store (Windows), by SHA-256 fingerprint.
$env:NUGET_SIGN_CERT_FINGERPRINT = "<SHA-256 fingerprint>"

# Optional: an RFC 3161 timestamp server (default http://timestamp.digicert.com). The timestamp keeps the
# signature valid after the certificate expires.
$env:NUGET_SIGN_TIMESTAMPER = "http://timestamp.digicert.com"

pwsh scripts/publish-package.ps1
```

The script runs `dotnet nuget sign` and then `dotnet nuget verify --all`; the verify step fails unless the
publishing machine trusts the certificate's root, which is the same check a consumer will make.

**3. Have consumers require the signature.** In each consuming repository's `nuget.config`:

```xml
<config>
  <add key="signatureValidationMode" value="require" />
</config>
<trustedSigners>
  <author name="BAPS">
    <certificate fingerprint="<SHA-256 fingerprint>" hashAlgorithm="SHA256" allowUntrustedRoot="false" />
  </author>
  <!-- With require, every package needs a trusted signer, nuget.org's included: see below. -->
</trustedSigners>
```

`dotnet nuget trust author BAPS <package.nupkg>` writes the author entry from a signed package, and
`dotnet nuget trust repository nuget.org <any nuget.org package.nupkg>` adds nuget.org's current repository
certificate (it is rotated, so do not copy a fingerprint from elsewhere). Set `allowUntrustedRoot="true"` only
when consumers' machines cannot be made to trust the internal CA root. Reference:
[signed packages](https://learn.microsoft.com/nuget/consume-packages/installing-signed-packages).

### Delete a package version

Removes one version of HttpResilience.NET from GitHub Packages. The version argument is required; a version
that does not exist exits successfully with nothing to do. It does not touch the git tag or the GitHub release.
PAT needs `delete:packages` (and `read:packages` for the lookup).

```powershell
$env:GITHUB_PAT = "your_token_here"
pwsh scripts/delete-package.ps1 3.0.0
```

---

## Manual build and pack

From repo root:

```bash
dotnet build src/HttpResilience.NET/HttpResilience.NET.csproj --configuration Release
dotnet pack src/HttpResilience.NET/HttpResilience.NET.csproj --configuration Release --no-build --output nupkgs
```

Packages are produced in `nupkgs/` (e.g. `HttpResilience.NET.3.0.0.nupkg`). A package for release needs
`-p:ContinuousIntegrationBuild=true` on both commands, as `publish-package.ps1` passes; without it the embedded
PDB records this machine's absolute source paths.

`dotnet pack` runs package validation against `PackageValidationBaselineVersion` (the last published
version). A break it reports is either a mistake to fix or an intended break: for the latter, regenerate
`src/HttpResilience.NET/CompatibilitySuppressions.xml` with `-p:ApiCompatGenerateSuppressionFile=true` and review
its diff like the PublicAPI files ([docs/VERSIONING.md](../docs/VERSIONING.md#enforcement)).

---

## Manual push

The feed URL can be passed directly, as the publish script does:

```bash
dotnet nuget push nupkgs/HttpResilience.NET.3.0.0.nupkg \
  --api-key YOUR_GITHUB_PAT \
  --source https://nuget.pkg.github.com/baps-apps/index.json
```

---

## Documentation checks

```bash
python3 scripts/check-benchmark-docs.py        # docs/benchmarks/README.md allocation figures vs the raw reports
python3 scripts/check-benchmark-docs.py --against <dir>/results  # the same figures vs a fresh run
python3 scripts/check-public-api-shipped.py    # release step: PublicAPI.Unshipped.txt must be empty
python3 scripts/check-readme-links.py          # README.md links absolute, so they work on the package page
```

`check-benchmark-docs.py` compares only the Allocated column; timings depend on the machine. With
`--against <dir>/results` (a run with `--artifacts <dir>`) it compares the docs to that run instead, exactly except
for the two hedged rows, whose allocation depends on scheduling (10% tolerance). Run `check-public-api-shipped.py`
after moving `PublicAPI.Unshipped.txt` into `PublicAPI.Shipped.txt` when cutting a release
([docs/VERSIONING.md](../docs/VERSIONING.md)).

---

## Troubleshooting

| Issue | What to do |
| --- | --- |
| **401 on restore** | The `baps-apps-packages` source has no credentials, or the PAT lacks `read:packages`; see [Restoring from baps-apps-packages](#restoring-from-baps-apps-packages). |
| **403 Forbidden** | Confirm PAT has `write:packages` (and `delete:packages` for deletes); check namespace/org. |
| **Package already exists** | The script deletes and republishes. If the delete fails, the PAT lacks `delete:packages`. |
| **Working tree has uncommitted or untracked changes** | Commit or stash them (`git stash -u` for untracked files); the package is built only from a commit. |
| **Version requested does not match the committed .csproj** | Set `<Version>` in `HttpResilience.NET.csproj`, commit it, and run the script again. |
| **Version not found in .csproj** | Add `<Version>` to the first `<PropertyGroup>` in `HttpResilience.NET.csproj` and commit it. |
| **Working tree changed / HEAD moved while testing and packing** | Something edited a tracked file or committed during the run; nothing was pushed. Find the change, commit or discard it, and run again. |
| **Package file not found** | Ensure `dotnet pack` succeeds and produces `nupkgs/HttpResilience.NET.<Version>.nupkg`. |
| **Could not create GitHub release** | The PAT lacks `repo` / `public_repo`; create it at `https://github.com/baps-apps/http-resilience-net/releases/new?tag=v<version>`. |

---

## Reference

- [GitHub Packages – NuGet](https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-nuget-registry)
- [Managing PATs](https://github.com/settings/tokens)
