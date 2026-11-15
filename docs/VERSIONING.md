# Versioning

[Semantic Versioning](https://semver.org/).

| Change | Version |
| --- | --- |
| Public API removed, renamed, moved or re-signed | MAJOR |
| Configuration key renamed or removed | MAJOR |
| **Default value change** that alters runtime behavior | MAJOR |
| **New validation rule** that rejects previously accepted configuration | MAJOR |
| Pipeline order or strategy semantics change | MAJOR |
| Exception type change for an existing failure | MAJOR |
| Metric name or tag, log category or event ID renamed or removed | MAJOR |
| Target framework change | MAJOR |
| Major version bump of `Microsoft.Extensions.Http.Resilience` or Polly (their behavior is this package's) | MAJOR |
| New optional key with a backward-compatible default | MINOR |
| New extension method, overload or telemetry dimension | MINOR |
| Bug fix with no behavior change for valid configuration | PATCH |
| Docs, tests, internal refactoring | PATCH |

A default change and a new validation rule are easy to miss: consumers inherit defaults without stating them,
and a configuration that started yesterday must not fail today.

A renamed key fails startup with a message naming its replacement, so a file written for an earlier major
cannot silently bind to nothing (`Retry:MaxAttempts` names `Retry:MaxRetries`).

## Support

The latest major gets every fix. The previous major gets **security fixes only, for six months** after the next
major is published, released as patches of its last version (2.x until 2027-05-14, as 2.0.x). Nothing else is
backported, and no older major is patched. [SECURITY.md](../SECURITY.md#supported-versions) holds the current
dates.

## Enforcement

- **Public API analyzer.** Every public member is recorded in `src/HttpResilience.NET/PublicAPI.Shipped.txt` /
  `PublicAPI.Unshipped.txt`; an unrecorded change fails the build. At release, move Unshipped into Shipped (a
  `*REMOVED*` line deletes its match from Shipped instead) and run `python3 scripts/check-public-api-shipped.py`.
- **Package validation.** `EnablePackageValidation` is on, and `dotnet pack` compares the package against
  `PackageValidationBaselineVersion`, the last published version, **including across a major** (3.0.0 is checked
  against 2.0.0). The public API analyzer catches changes to source; package validation catches the binary
  breaks a consumer compiled against the previous release would hit. A major's intended breaks are listed in
  `src/HttpResilience.NET/CompatibilitySuppressions.xml`, regenerated with `-p:ApiCompatGenerateSuppressionFile=true`
  and reviewed entry by entry against the CHANGELOG: an entry with no CHANGELOG line is an accidental break.
  After publishing, the baseline moves to the new version and the file is emptied.

## Namespaces

Registration extension methods live in `Microsoft.Extensions.DependencyInjection`, so they need no extra
`using`; their classes are prefixed (`HttpResilienceServiceCollectionExtensions`) to avoid collisions. Every other
public type is in `HttpResilience.NET`, with no sub-namespaces, and starts with `HttpResilience`.
