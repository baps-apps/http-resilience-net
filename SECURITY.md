# Security policy

## Reporting a vulnerability

**Do not open a public issue, discussion or pull request for a security problem.**

Report it privately through GitHub:
[Report a vulnerability](https://github.com/baps-apps/http-resilience-net/security/advisories/new)
(the repository's **Security** tab, then **Report a vulnerability**). Include:

- the package version and the .NET and `Microsoft.Extensions.Http.Resilience` versions;
- the configuration (the `HttpResilience` section and any code passed to `AddHttpResilience` or
  `AddHttpResilienceHandler`), with secrets removed;
- what happens, what you expected, and the smallest program or test that shows it.

You should get an acknowledgement within 3 working days and an assessment within 10. Fixes are released as a
patch version and announced in a GitHub security advisory and in [CHANGELOG.md](CHANGELOG.md).

## Supported versions

| Version | Security fixes |
| --- | --- |
| 3.x (latest major) | Yes |
| 2.x (previous major) | Yes, until **2027-05-14** (six months after 3.0.0), as 2.0.x patches; then no |
| 1.x and earlier | No: upgrade to 3.x ([CHANGELOG.md](CHANGELOG.md) lists every breaking change) |

The previous major gets security fixes only, for six months after the next major is published, so services can
migrate across its breaking changes without running an unpatched version. Upgrading to 3.x needs more than a
recompile: [CHANGELOG.md](CHANGELOG.md) lists the behavior changes that compile without error.

## What counts as a vulnerability here

This package configures `Microsoft.Extensions.Http.Resilience`; it handles no credentials and records no request
data. In scope, for example:

- an unsafe HTTP method (POST, PUT, PATCH, DELETE or an unknown method) retried or hedged without the explicit
  per-client opt-in;
- a hedged client reaching an authority outside `PipelineSelection:Authorities`;
- request data (paths, query strings, headers, bodies) in a log entry or a metric tag, or any unbounded metric
  dimension;
- a TLS setting, client certificate or certificate callback dropped or weakened by `Connection:Enabled`;
- a configuration value that bypasses startup validation and reaches the platform.

Vulnerabilities in `Microsoft.Extensions.Http.Resilience`, Polly or the .NET runtime belong to their own
projects: report them to Microsoft (<https://msrc.microsoft.com>) or to Polly
(<https://github.com/App-vNext/Polly/security>).

The package's security posture, its idempotency rules and the review expected for a change that opts a client in
to repeating unsafe methods are in [docs/SECURITY-GOVERNANCE.md](docs/SECURITY-GOVERNANCE.md).
