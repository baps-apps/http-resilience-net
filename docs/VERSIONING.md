## Versioning and Compatibility Policy

This document describes how `HttpResilience.NET` is versioned and what compatibility guarantees it provides.

---

### Semantic versioning

The library follows **Semantic Versioning (SemVer)**:

- **MAJOR** version (X.y.z):
  - May introduce **breaking changes**, such as:
    - Public API changes (extensions, options classes, enums).
    - Breaking changes to configuration schema or default behavior.
- **MINOR** version (x.Y.z):
  - Adds new features in a **backwards-compatible** way:
    - New options with safe defaults.
    - New overloads or extension methods.
    - New documentation or samples.
- **PATCH** version (x.y.Z):
  - Bug fixes and internal improvements that do **not** change public APIs or documented behavior.

---

### Configuration compatibility

Configuration compatibility is treated as part of the public contract.

We guarantee that:

- **Valid configs stay valid**:
  - A configuration file that is valid for version `1.y.z` remains valid for all `1.y+1.z+` versions.
  - Validation rules (ranges, allowed values) may become stricter **only** to reject configurations that would not behave correctly at runtime.
- **New options are additive**:
  - New properties/options are added with **safe defaults**.
  - If you do not set them, existing behavior remains unchanged.

If a breaking config change is ever required (e.g. renaming keys or removing options), it will:

- Only happen in a **major** version.
- Be called out explicitly in the release notes and documentation.

---

### Defaults and behavior changes

Defaults have a large impact on behavior and operations. We follow these rules:

- **Within a major version**:
  - Defaults will not change in ways that meaningfully alter behavior (e.g. significantly increasing retries or timeouts).
  - If a tweak is unavoidable, it will be documented and released as a minor version, with clear upgrade notes.
- **Across major versions**:
  - Defaults may change to better recommendations (e.g. stronger timeouts, safer circuit breaker settings).
  - Migration guidance will be provided.

---

### Deprecation policy

When an API or option is planned for removal:

- It will be marked as **obsolete** (where applicable) and documented as such.
- A recommended alternative will be provided.
- Actual removal will occur in a **later major** version.

---

### Upgrading guidance

When upgrading:

- **Patch updates** (e.g. 1.0.0 → 1.0.3):
  - Safe to apply broadly after running tests; behavior is expected to be identical, except for bug fixes.
- **Minor updates** (e.g. 1.0.0 → 1.2.0):
  - Review release notes for:
    - New features you may want to opt into.
    - Any stricter validation that may reject previously-accepted misconfigurations.
- **Major updates** (e.g. 1.x → 2.0):
  - Review migration notes carefully.
  - Plan for testing and potential configuration or code adjustments.

---

### Support expectations

- The library is intended for use on **current and supported .NET releases**.
- For the **1.x** line, the primary target is `net10.0`; future major versions may add or adjust target frameworks in line with the .NET support lifecycle.
- Within a given major version, bug fixes and non-breaking enhancements will continue as long as the underlying .NET version remains in mainstream support.

