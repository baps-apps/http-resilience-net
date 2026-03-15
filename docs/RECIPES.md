## Configuration Recipes

This document provides **opinionated configuration examples** for common scenarios.

For full option semantics, see `IMPLEMENTATION.md`. For operational guidance, see `OPERATIONS.md`.

---

### Baseline internal REST API (standard pipeline)

**Goal**: Reasonable defaults for typical internal HTTP calls between services.

```json
{
  "HttpResilienceOptions": {
    "Enabled": true,
    "PipelineType": "Standard",
    "Connection": {
      "Enabled": true,
      "MaxConnectionsPerServer": 20,
      "PooledConnectionIdleTimeoutSeconds": 120,
      "PooledConnectionLifetimeSeconds": 600,
      "ConnectTimeoutSeconds": 10
    },
    "Timeout": {
      "TotalRequestTimeoutSeconds": 30,
      "AttemptTimeoutSeconds": 10
    },
    "Retry": {
      "MaxRetryAttempts": 3,
      "BaseDelaySeconds": 1,
      "BackoffType": "Exponential",
      "UseJitter": true,
      "UseRetryAfterHeader": true
    },
    "CircuitBreaker": {
      "MinimumThroughput": 100,
      "FailureRatio": 0.1,
      "SamplingDurationSeconds": 30,
      "BreakDurationSeconds": 5
    }
  }
}
```

- **When to use**: Most synchronous REST calls in line-of-business apps.

---

### Latency-critical calls with hedging

**Goal**: Minimize tail latency for replicated backends (e.g. behind a load balancer).

```json
{
  "HttpResilienceOptions": {
    "Enabled": true,
    "PipelineType": "Hedging",
    "Connection": {
      "Enabled": true,
      "MaxConnectionsPerServer": 100,
      "PooledConnectionIdleTimeoutSeconds": 60,
      "PooledConnectionLifetimeSeconds": 300,
      "ConnectTimeoutSeconds": 5
    },
    "Timeout": {
      "TotalRequestTimeoutSeconds": 5,
      "AttemptTimeoutSeconds": 2
    },
    "CircuitBreaker": {
      "MinimumThroughput": 100,
      "FailureRatio": 0.2,
      "SamplingDurationSeconds": 30,
      "BreakDurationSeconds": 10
    },
    "Hedging": {
      "DelaySeconds": 1,
      "MaxHedgedAttempts": 1
    },
    "RateLimiter": {
      "Enabled": true,
      "PermitLimit": 500,
      "WindowSeconds": 1,
      "QueueLimit": 50,
      "Algorithm": "TokenBucket",
      "TokenBucketCapacity": 500,
      "TokensPerPeriod": 500,
      "ReplenishmentPeriodSeconds": 1
    }
  }
}
```

- **When to use**: Read-heavy, latency-sensitive endpoints where a small extra load is acceptable to reduce tails.

---

### Protecting against downstream overload (bulkhead + rate limit)

**Goal**: Ensure a misbehaving dependency cannot consume all outbound capacity.

```json
{
  "HttpResilienceOptions": {
    "Enabled": true,
    "PipelineType": "Standard",
    "Timeout": {
      "TotalRequestTimeoutSeconds": 60,
      "AttemptTimeoutSeconds": 20
    },
    "Retry": {
      "MaxRetryAttempts": 1,
      "BaseDelaySeconds": 2,
      "BackoffType": "Exponential",
      "UseJitter": true
    },
    "Bulkhead": {
      "Enabled": true,
      "Limit": 50,
      "QueueLimit": 100
    },
    "RateLimiter": {
      "Enabled": true,
      "PermitLimit": 200,
      "WindowSeconds": 1,
      "QueueLimit": 50,
      "Algorithm": "FixedWindow"
    },
    "PipelineOrder": "ConcurrencyThenFallback"
  }
}
```

- **When to use**: Hot dependencies that risk saturating outbound connections/threads.

---

### Graceful degradation with fallback

**Goal**: Return a synthetic or cached response when a downstream is unavailable.

```json
{
  "HttpResilienceOptions": {
    "Enabled": true,
    "PipelineType": "Standard",
    "Timeout": {
      "TotalRequestTimeoutSeconds": 10,
      "AttemptTimeoutSeconds": 5
    },
    "Retry": {
      "MaxRetryAttempts": 2,
      "BaseDelaySeconds": 1,
      "BackoffType": "Exponential",
      "UseJitter": true
    },
    "Fallback": {
      "Enabled": true,
      "StatusCode": 503,
      "OnlyOn5xx": true,
      "ResponseBody": "Service temporarily unavailable. Please try again later."
    }
  }
}
```

- **When to use**: Non-critical dependencies (e.g. recommendations, analytics) where a friendly failure is better than an exception.

For more advanced scenarios, implement `IHttpFallbackHandler` and pass it into `AddHttpClientWithResilience`.

---

### Multi-tenant / multi-environment configuration

**Goal**: Use multiple **option sets** in the same process (e.g. per-tenant or per-region).

Example `appsettings.json`:

```json
{
  "HttpResilienceOptions": {
    "Enabled": true,
    "PipelineType": "Standard"
  },
  "HttpResilienceOptions:TenantA": {
    "Enabled": true,
    "PipelineType": "Standard",
    "Timeout": { "TotalRequestTimeoutSeconds": 20, "AttemptTimeoutSeconds": 10 }
  },
  "HttpResilienceOptions:TenantB": {
    "Enabled": true,
    "PipelineType": "Hedging",
    "Timeout": { "TotalRequestTimeoutSeconds": 5, "AttemptTimeoutSeconds": 2 }
  }
}
```

Registration:

```csharp
// Bind the default section once for global options
services.AddHttpResilienceOptions(configuration);

// Tenant A client uses a nested section
var tenantASection = configuration.GetSection("HttpResilienceOptions:TenantA");
services.AddHttpClient("TenantA")
    .AddHttpClientWithResilience(tenantASection);

// Tenant B client uses a different nested section
var tenantBSection = configuration.GetSection("HttpResilienceOptions:TenantB");
services.AddHttpClient("TenantB")
    .AddHttpClientWithResilience(tenantBSection);
```

This pattern lets platform teams define a **base** `HttpResilienceOptions` and override only what is needed per tenant or environment. When using the **custom inner pipeline** overload with per-tenant config, use `AddHttpClientWithResilience(tenantSection, requestTimeoutSeconds: null, fallbackHandler: null, configureInnerPipeline: inner => { ... })` so the primary handler (connection, timeouts) is built from that section; see `IMPLEMENTATION.md` (Inner pipeline order).

