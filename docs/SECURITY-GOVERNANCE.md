## Security and Governance

This document provides guidance for using `HttpResilience.NET` in security-conscious and governed environments.

---

### Security posture

`HttpResilience.NET`:

- Configures **outbound HTTP resilience** only (no inbound behavior).
- Does not handle authentication/authorization headers or tokens.
- Does not log or export request/response bodies.

Telemetry enrichment is intentionally minimal:

- `error.type`: exception type or HTTP status code (numeric).
- `request.dependency.name`: `scheme://host[:port]` only.
- `request.name`: logical operation/pipeline name (no payload).

This keeps the library suitable for use in environments with:

- PII and sensitive payloads in HTTP bodies.
- Strict logging/telemetry policies.

---

### Recommendations for secure use

- **Do not include secrets in hostnames**:
  - `request.dependency.name` is derived from URI authority; ensure hostnames themselves do not contain secrets or sensitive tenant identifiers.

- **Control who can change resilience configuration**:
  - Treat `HttpResilienceOptions` as an **operational control plane**:
    - Store in central configuration (e.g. shared appsettings, configuration service).
    - Restrict write access to platform/SRE teams.

- **Review fallback behavior**:
  - `Fallback:ResponseBody` is plain text; do not put secrets or detailed diagnostics into this field.
  - Custom `IHttpFallbackHandler` should follow your normal logging and data-handling policies.

---

### Governance and centralization patterns (documentation-only)

Many enterprises want a **central team** to own resilience standards while allowing app teams some flexibility.

Patterns to consider:

- **Central base configuration + local overrides**:
  - Define a shared base `HttpResilienceOptions` in a central configuration file.
  - Allow services to override only a small subset (e.g. timeouts) via per-service sections.
  - Example: base under `HttpResilienceOptions`, service-specific under `HttpResilienceOptions:MyService`.

- **Separate configurations per dependency class**:
  - Group dependencies by characteristics (e.g. “critical internal API”, “external SaaS API”, “batch job endpoint”).
  - Provide named sections like `HttpResilienceOptions:Critical`, `HttpResilienceOptions:External`, etc.
  - Consumers choose the appropriate section via `AddHttpClientWithResilience(IConfigurationSection)`.

- **Review and approval workflow**:
  - Changes to central `HttpResilienceOptions` (or derived sections) should:
    - Go through code review.
    - Be tested in lower environments with observability (see `OPERATIONS.md`).

These patterns can be implemented without changing the library; they are practices for **how** you structure configuration and ownership across teams.

