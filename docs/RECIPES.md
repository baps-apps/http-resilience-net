# Recipes

Every recipe assumes `builder.Services.AddHttpResilience(builder.Configuration);` once in `Program.cs`.

## Internal API, ordinary traffic

The defaults: 20 s total, 5 s per attempt, 2 retries from 500 ms with jitter, safe methods only. Nothing to
configure.

```csharp
builder.Services.AddHttpClient<IOrdersApi, OrdersApi>().AddHttpResilienceHandler("Orders");
```

Passing `"Orders"` makes the client read `HttpResilience:Clients:Orders`; without it a typed client reads
`Clients:IOrdersApi`.

## A slow endpoint (reports, exports)

```json
{
  "HttpResilience": {
    "Clients": {
      "Reports": {
        "Timeout": { "Total": "00:03:00", "Attempt": "00:00:45" },
        "Retry": { "MaxRetries": 1 },
        "CircuitBreaker": { "SamplingDuration": "00:02:00", "MinimumThroughput": 5 }
      }
    }
  }
}
```

`SamplingDuration` must be at least 2 × `Attempt`. A quiet client needs a low `MinimumThroughput`, or its
breaker never opens.

## Retrying a POST safely

Only when the endpoint deduplicates on an idempotency key:

```json
{ "HttpResilience": { "Clients": { "Payments": { "Retry": { "RetryableMethods": [ "GET", "POST" ] } } } } }
```

```csharp
request.Headers.TryAddWithoutValidation("Idempotency-Key", operationId.ToString());
```

Only the listed methods are retried, so HEAD, OPTIONS and TRACE are not, here. The client logs startup Warning
event 10. This belongs in the client's section; the root may list only safe methods. Retried content must be
replayable: buffer a `StreamContent` with `LoadIntoBufferAsync()`.

## Respecting a third-party quota

A vendor allows 300 requests per minute; you run 6 replicas:

```json
{
  "HttpResilience": {
    "Clients": {
      "Vendor": {
        "RateLimiter": {
          "Enabled": true,
          "Algorithm": "SlidingWindow",
          "PermitLimit": 50,
          "Window": "00:01:00",
          "SegmentsPerWindow": 6,
          "QueueLimit": 10
        }
      }
    }
  }
}
```

`300 ÷ 6 = 50` per replica. **The limiter is process-local and cannot enforce a cluster-wide quota**: if the
replica count changes or a second client calls the vendor, this number is wrong. Enforce a true global quota at
a gateway. `SlidingWindow` avoids `FixedWindow`'s double burst across a window boundary. Queued requests wait
outside `Timeout:Total`.

## Protecting your own capacity

Stop one slow dependency from tying up all your outbound concurrency:

```json
{ "HttpResilience": { "Clients": { "Legacy": { "ConcurrencyLimiter": { "Enabled": true, "Limit": 20, "QueueLimit": 50 } } } } }
```

One slot covers a request and its retries. `Limit` must be at most `ConcurrencyLimiter:Backstop` (1,000). Keep
`QueueLimit` small: the wait is outside `Timeout:Total`. Watch
`http_resilience.limiter.queued_requests{http_resilience.limiter.kind="concurrency"}`.

## Tail latency on a replicated read (hedging)

```csharp
builder.Services.AddHttpClient("Search").AddHttpResilienceHedgingHandler();
```

```json
{
  "HttpResilience": {
    "Clients": {
      "Search": {
        "Timeout": { "Total": "00:00:06", "Attempt": "00:00:02" },
        "Hedging": { "Delay": "00:00:00.300", "MaxHedgedAttempts": 1 },
        "PipelineSelection": { "Authorities": [ "https://search.internal" ] }
      }
    }
  }
}
```

Set `Delay` near the dependency's p95, so only slow requests are hedged (zero hedges every request).
`Timeout:Total` must be at least `Delay × MaxHedgedAttempts + Attempt`. Only safe methods are hedged,
redirects are not followed, and unlisted hosts are refused.

## One client, several hosts

```json
{
  "HttpResilience": {
    "Clients": {
      "Partner": {
        "PipelineSelection": {
          "Mode": "ByAuthority",
          "Authorities": [ "https://a.partner.example", "https://b.partner.example" ]
        }
      }
    }
  }
}
```

Each listed host gets its own circuit breaker; unlisted hosts share one pipeline. Rate and concurrency limits
stay per client. Without a rate limiter the backstop is per pipeline, so two hosts allow up to 3 × `Backstop`
in flight.

## Turning resilience off during an incident

```json
{ "HttpResilience": { "Enabled": false } }
```

The root kill switch overrides every client section and code delegate; each client logs startup Warning event
6. Requests pass straight through (no retries, timeouts, breaker or limiters; `HttpClient.Timeout` is back to
its 100 s default). `Connection` settings, including a hedged client's redirect bound, still apply. For one
client, use `HttpResilience:Clients:{name}:Enabled: false`.

To keep timeouts and the breaker but stop adding load, disable only retries:

```json
{ "HttpResilience": { "Retry": { "Enabled": false } } }
```

Both need a restart.

## Something the schema does not cover

Add a platform handler after this package's; it sits inside this pipeline:

```csharp
builder.Services.AddHttpClient("Legacy")
    .AddHttpResilienceHandler()
    .AddResilienceHandler("legacy-quirk", pipeline =>
        pipeline.AddRetry(new HttpRetryStrategyOptions
        {
            ShouldHandle = args => ValueTask.FromResult(
                args.Outcome.Result?.Headers.Contains("X-Vendor-Retry") == true)
        }));
```

What you add is yours: this package's validation and safe-method guard do not apply, so the retry above repeats
a POST too. **Use `AddResilienceHandler`, never `AddStandardResilienceHandler`**: the latter nests a second
pipeline (9 origin calls instead of 3, total timeout applied twice) and is only reported (event 12).

## A .NET Aspire service (ServiceDefaults)

ServiceDefaults add the platform's standard handler to every client via `ConfigureHttpClientDefaults`. Keep
them as they are. On each client this package configures, that handler is replaced by this package's pipeline
(event 17, Information, once per client), so the two never nest; every other client keeps it. No extra call is
needed, and registration order does not matter:

```csharp
builder.AddServiceDefaults();
builder.Services.AddHttpResilience(builder.Configuration);

builder.Services.AddHttpClient("Orders").AddHttpResilienceHandler();
```

This package's pipeline takes the replaced handler's place, so service discovery, which ServiceDefaults add
inside it, stays inside: every retry and hedged attempt is resolved again (and can reach another endpoint), and
`PipelineSelection:Authorities` lists the logical names the code calls (`https://catalog`), not resolved
addresses. Keep `AddStandardResilienceHandler()` in ServiceDefaults for that reason. Removing it while keeping
`AddServiceDiscovery()` leaves nothing to replace, so the resolver stays outside this package's pipeline: every
retry goes to the endpoint resolved once, and a hedged client refuses the resolved address.

A client with `Enabled: false` (or the root kill switch) loses the defaults' handler too (event 19), so it has
no resilience at all: the platform's standard handler retries every method, POST included, so falling back to
it would make the kill switch repeat mutations. `RemoveAllResilienceHandlers()` is unnecessary: before
`AddHttpResilienceHandler()` it is harmless; after it, it removes this package's pipeline (event 16).

## Migrating from Microsoft.Extensions.Http.Polly (Polly v7)

Remove `AddPolicyHandler`, `AddTransientHttpErrorPolicy` and `AddPolicyHandlerFromRegistry` from every client
this package configures, including any in `ConfigureHttpClientDefaults`. They are not replaced: a v7 retry left
in place nests with this package's pipeline (one failing GET reaches the origin twelve times with a v7 retry
of three) and its predicate retries POST. Each such client logs startup **Warning event 20**.

| Polly v7 policy | Here |
| --- | --- |
| `RetryAsync` / `WaitAndRetryAsync` | `Retry:MaxRetries`, `BaseDelay`, `BackoffType`, `UseJitter` |
| `TimeoutAsync` | `Timeout:Attempt` (per attempt) or `Timeout:Total` |
| `CircuitBreakerAsync` / `AdvancedCircuitBreakerAsync` | `CircuitBreaker:*` |
| `BulkheadAsync` | `ConcurrencyLimiter:Limit` and `QueueLimit` |
| anything else | `AddResilienceHandler` after `AddHttpResilienceHandler` |

## Adjusting one value in code

```csharp
builder.Services.AddHttpClient("Orders")
    .AddHttpResilienceHandler(options => options.Retry.MaxRetries = 1);
```

Runs after configuration binding and is validated like configuration. It can change anything, including
`Enabled` and the limiter switches.

## One configuration file shared by many services

An organization-wide file, a config server or a shared Helm values file usually lists more clients than any one
service registers, and is read by services on different versions of this package. Both fail startup by default
(an unused `Clients` section, a key an older version does not know).

State the version the file was written for, and allow unused sections. A service on an older minor version
then starts and logs the keys it does not know yet (Warning event 18, naming each), while a misspelled key in a
file written for that service's own version still fails startup. A file stating a newer major fails startup:
keep one file per major during a major upgrade.

```json
{
  "HttpResilience": {
    "SchemaVersion": "3.1",
    "AllowUnusedClientSections": true,
    "Timeout": { "Total": "00:00:20", "Attempt": "00:00:05" },
    "Clients": {
      "Orders":   { "Timeout": { "Total": "00:00:10" } },
      "Payments": { "Retry": { "RetryableMethods": [ "GET", "POST" ] } }
    }
  }
}
```

Raise `SchemaVersion` only when the file starts using a key a newer version added: until then every service keeps
full typo checking. While it is ahead, on the older services a misspelled key in that file is only a warning,
and a misnamed section configures nothing. Keep each service's own overrides in its own `appsettings.json`,
where the checks stay on, and review the shared file. A value out of range fails startup either way.

`AllowUnknownKeys: true` (in configuration, or `options.AllowUnknownKeys = true` on
`HttpResilienceStartupOptions` in code) turns the unknown-key check off entirely, warning included. Prefer
`SchemaVersion`.

## Setting `HttpClient.Timeout`

Use `Timeout:Client`; it must be greater than `Timeout:Total`:

```json
{ "HttpResilience": { "Clients": { "Reports": { "Timeout": { "Total": "00:02:00", "Attempt": "00:00:15", "Client": "00:05:00" } } } } }
```

`.ConfigureHttpClient(c => c.Timeout = ...)` with a different value fails client creation. A typed client's
constructor that sets `Timeout` is not detected and can truncate the pipeline. Other `ConfigureHttpClient`
settings (headers, base address) are untouched.
