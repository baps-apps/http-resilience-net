# Architecture and Pipeline Overview

One-page view of how HttpResilience.NET fits together: handler stack, pipeline order, and per-authority selection.

---

## High-level flow

```
  Application code
        │
        ▼
  IHttpClientFactory.CreateClient("MyClient")
        │
        ▼
  ┌─────────────────────────────────────────────────────────────────┐
  │  Resilience pipeline (when Enabled = true)                      │
  │  Order determined by PipelineOrder list.                        │
  │  Outermost (first to execute) at top.                           │
  ├─────────────────────────────────────────────────────────────────┤
  │  Optional: Fallback    (if Fallback.Enabled and in list)        │
  │  Optional: Bulkhead    (if Bulkhead.Enabled and in list)        │
  │  Optional: RateLimiter (if RateLimiter.Enabled and in list)     │
  │  Standard OR Hedging   (exactly one, required in list)          │
  ├─────────────────────────────────────────────────────────────────┤
  │  Primary: SocketsHttpHandler (connection pool, connect timeout)  │
  └─────────────────────────────────────────────────────────────────┘
        │
        ▼
  Network (TCP/TLS to dependency)
```

---

## Pipeline order

**PipelineOrder** is a list of strategy names from outermost to innermost (e.g. `["Fallback", "Bulkhead", "RateLimiter", "Standard"]`). Handlers are added in reverse order internally so the first element is outermost.

| Position   | Strategy        | When added                          |
|------------|-----------------|-------------------------------------|
| Outermost  | Fallback        | If `Fallback.Enabled` and in list   |
|            | Bulkhead        | If `Bulkhead.Enabled` and in list   |
|            | RateLimiter     | If `RateLimiter.Enabled` and in list|
|            | Standard or Hedging | Exactly one; required in list   |
| Innermost  | (primary)       | SocketsHttpHandler                  |

---

## Sequence: request with retry and fallback

```mermaid
sequenceDiagram
    participant App
    participant Pipeline
    participant Fallback
    participant Standard
    participant Primary
    participant Network

    App->>Pipeline: SendAsync(request)
    Pipeline->>Fallback: Execute
    Fallback->>Standard: Execute
    Standard->>Primary: Execute (attempt 1)
    Primary->>Network: HTTP
    Network-->>Primary: 503
    Primary-->>Standard: 503
    Standard->>Standard: Retry (attempt 2)
    Standard->>Primary: Execute (attempt 2)
    Primary->>Network: HTTP
    Network-->>Primary: 503
    Primary-->>Standard: 503
    Standard-->>Fallback: Failure (all retries exhausted)
    Fallback->>Fallback: Produce synthetic 503
    Fallback-->>Pipeline: HttpResponseMessage(503)
    Pipeline-->>App: HttpResponseMessage(503)
```

---

## Per-authority pipeline selection

When **PipelineSelection:Mode** is **ByAuthority**:

- The same named `HttpClient` can call **multiple hosts**.
- A **separate pipeline instance** (circuit breaker, rate limiter, etc.) is used per **authority** (scheme + host + port).
- One failing host does not open the circuit for another.

```
  HttpClient("MultiHost")
        │
        ├── request to https://api-a.example.com  →  Pipeline instance A
        │
        └── request to https://api-b.example.com  →  Pipeline instance B
```

When **Mode** is **None** (default), a single pipeline instance is shared for all requests from that named client.

---

## Configuration and options flow

```mermaid
flowchart LR
    subgraph Config
        appsettings[appsettings.json]
        env[Environment / other sources]
    end
    subgraph Startup
        Bind[AddHttpResilienceOptions]
        Validate[ValidateOnStart when Enabled = true]
        Register[AddHttpClient + AddHttpClientWithResilience]
    end
    subgraph Runtime
        Factory[IHttpClientFactory]
        Client[HttpClient]
        Pipeline[Resilience pipeline]
    end
    appsettings --> Bind
    env --> Bind
    Bind --> Validate
    Validate --> Register
    Register --> Factory
    Factory --> Client
    Client --> Pipeline
```

---

## References

- [IMPLEMENTATION.md](IMPLEMENTATION.md) — Option semantics and configuration details
- [OPERATIONS.md](OPERATIONS.md) — Telemetry, health checks, and alerts
- [PRODUCTION-CHECKLIST.md](PRODUCTION-CHECKLIST.md) — Pre-go-live checklist
