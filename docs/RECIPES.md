# Configuration Recipes

Opinionated configuration examples for common scenarios. For full option semantics see [IMPLEMENTATION.md](IMPLEMENTATION.md).

---

## Baseline internal REST API

Standard pipeline with sensible defaults for typical service-to-service calls.

```json
{
  "HttpResilienceOptions": {
    "Enabled": true,
    "PipelineOrder": ["Standard"],
    "Connection": {
      "Enabled": true,
      "MaxConnectionsPerServer": 20,
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

---

## Latency-critical calls with hedging

Minimize tail latency for replicated backends behind a load balancer.

```json
{
  "HttpResilienceOptions": {
    "Enabled": true,
    "PipelineOrder": ["RateLimiter", "Hedging"],
    "Connection": {
      "Enabled": true,
      "MaxConnectionsPerServer": 100,
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
      "Algorithm": "TokenBucket",
      "TokenBucketCapacity": 500,
      "TokensPerPeriod": 500,
      "ReplenishmentPeriodSeconds": 1
    }
  }
}
```

---

## Protecting against downstream overload

Bulkhead + rate limiter to prevent one dependency from consuming all outbound capacity.

```json
{
  "HttpResilienceOptions": {
    "Enabled": true,
    "PipelineOrder": ["Bulkhead", "RateLimiter", "Standard"],
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
      "Algorithm": "FixedWindow",
      "PermitLimit": 200,
      "WindowSeconds": 1,
      "QueueLimit": 50
    }
  }
}
```

---

## Graceful degradation with fallback

Return a synthetic response when a non-critical dependency is unavailable.

```json
{
  "HttpResilienceOptions": {
    "Enabled": true,
    "PipelineOrder": ["Fallback", "Standard"],
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

For advanced scenarios, implement `IHttpFallbackHandler` and pass it to `AddHttpClientWithResilience`.

---

## Multi-tenant / multi-environment

Use multiple option sets in the same process (per-tenant or per-region).

**appsettings.json:**

```json
{
  "HttpResilienceOptions": {
    "Enabled": true,
    "PipelineOrder": ["Standard"]
  },
  "HttpResilienceOptions:TenantA": {
    "Enabled": true,
    "PipelineOrder": ["Standard"],
    "Timeout": { "TotalRequestTimeoutSeconds": 20, "AttemptTimeoutSeconds": 10 }
  },
  "HttpResilienceOptions:TenantB": {
    "Enabled": true,
    "PipelineOrder": ["Hedging"],
    "Timeout": { "TotalRequestTimeoutSeconds": 5, "AttemptTimeoutSeconds": 2 },
    "Hedging": { "DelaySeconds": 0, "MaxHedgedAttempts": 1 }
  }
}
```

**Registration:**

```csharp
// Base options (once)
services.AddHttpResilienceOptions(configuration);

// Per-tenant clients
var tenantASection = configuration.GetSection("HttpResilienceOptions:TenantA");
services.AddHttpClient("TenantA")
    .AddHttpClientWithResilience(tenantASection);

var tenantBSection = configuration.GetSection("HttpResilienceOptions:TenantB");
services.AddHttpClient("TenantB")
    .AddHttpClientWithResilience(tenantBSection);
```

For per-tenant custom inner pipelines, use `AddHttpClientWithResilience(tenantSection, requestTimeoutSeconds: null, fallbackHandler: null, configureInnerPipeline: inner => { ... })`.
