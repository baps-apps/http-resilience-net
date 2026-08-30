# Versioning

HttpResilience.NET follows [Semantic Versioning](https://semver.org/).

| Change | Version |
| --- | --- |
| Public API removal, rename, namespace move or signature change | MAJOR |
| Configuration key renamed or removed | MAJOR |
| Default value change that alters runtime behavior | MAJOR |
| A new validation rule that rejects previously accepted configuration | MAJOR |
| Pipeline ordering or strategy semantics change | MAJOR |
| Exception type change for an existing failure | MAJOR |
| New optional configuration key with a backwards-compatible default | MINOR |
| New extension method or overload | MINOR |
| New telemetry dimension | MINOR |
| Bug fix with no behavior change for valid configuration | PATCH |
| Documentation, tests, internal refactoring | PATCH |

Two of these are easy to get wrong and are called out deliberately:

- **A default change is MAJOR.** Consumers inherit defaults without stating them, so changing one changes behavior in every service that did not override it — with no diff anywhere to show it.
- **A new validation rule is MAJOR.** It turns a configuration that started yesterday into one that fails to start today, which for an operator is indistinguishable from a breaking change.

## Enforcement

**The public API is a build gate.** `Microsoft.CodeAnalysis.PublicApiAnalyzers` holds every public type and
member in `src/HttpResilience.NET/PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt`. Adding, removing or
changing the signature of anything public fails the build until the corresponding line is edited, so the
change is visible as a diff in review rather than at pack time. At release, move the contents of
`PublicAPI.Unshipped.txt` into `PublicAPI.Shipped.txt` -- and that step is itself gated: on a tagged build CI
runs `scripts/check-public-api-shipped.py`, which fails when anything is still listed as unshipped. Without
it the move is a human step nothing checks, and skipping it once leaves every member "unshipped" for good.

`EnablePackageValidation` is on as a second check. Its baseline follows one rule:
**`PackageValidationBaselineVersion` names the last published version within the same major.** It stays unset
for the first release of a major, because a major exists precisely to make intentional breaks, and validating
one against the previous major would report every deliberate removal as a failure and train everyone to
ignore the gate. From the first patch or minor after a major, the baseline is set and the check is real.
Until then the API analyzer above is the gate.

## Scheduled removals

One public member exists only to fail:
`HttpResilience.NET.Options.RetryOptions.MaxAttempts`. It is `[Obsolete]`, it is bound so that a stale
configuration file is refused rather than silently ignored, and it is read in exactly one place --
`HttpResilienceOptionsValidator.ValidateRenamedKeys`. Keeping it is what turns a file written for 1.x into a
startup failure with a message instead of a client quietly running three attempts where its author meant two.

**It is removed in 3.0**, which is the next opportunity, because removing a public member is MAJOR by the
table above. By then a 1.x configuration file is old enough that failing to bind it is no worse than failing
to recognise it. Nothing else in the public surface is scheduled for removal; when something is, it is listed
here at the same time as it is deprecated, so "what breaks next major" is answerable from one place.

## Namespaces

Registration extension methods live in `Microsoft.Extensions.DependencyInjection`, which is where the .NET
convention puts extensions on `IServiceCollection` and `IHttpClientBuilder` -- so they appear in IntelliSense
in any `Program.cs` without a package-specific `using`. The classes are prefixed (`HttpResilienceServiceCollectionExtensions`,
not `ServiceCollectionExtensions`) because that namespace is shared: an unprefixed `ServiceCollectionExtensions`
collides with the one almost every other package ships. Options and configuration types stay in
`HttpResilience.NET.*`, which is theirs alone.

## Target framework

`net10.0`. A TFM change is MAJOR.

## Dependencies

The package depends only on first-party Microsoft packages and Polly. A major version bump of `Microsoft.Extensions.Http.Resilience` or Polly is treated as MAJOR here, because their behavior is this package's behavior.
