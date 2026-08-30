# Recipes

> A retried request must carry **replayable** content. A retry re-sends the same `HttpRequestMessage`, so
> `StringContent`, `ByteArrayContent` and `JsonContent` replay correctly and a single-pass stream does not:
> measured against a real endpoint, a `StreamContent` over a non-seekable stream retried three times delivers
> the body once and then an empty body twice, without throwing. Buffer it with
> `await content.LoadIntoBufferAsync()`, or build fresh content per attempt. This matters for any client with
> a mutating method in `Retry:RetryableMethods`.

## Internal API, ordinary traffic

Root defaults are already this. Nothing per-client is needed.

```json
{
  "HttpResilience": {
    "Enabled": true,
    "Timeout": { "Total": "00:00:20", "Attempt": "00:00:05" },
    "Retry": { "MaxRetries": 2, "BaseDelay": "00:00:00.500" }
  }
}
```

```csharp
builder.Services.AddHttpResilience(builder.Configuration);
builder.Services.AddHttpClient<IOrdersApi, OrdersApi>().AddHttpResilience();
```

> A typed client's section name comes from `IHttpClientFactory`, not from this schema, and for the
> two-generic overload it is `TClient`: the registration above reads `HttpResilience:Clients:IOrdersApi`.
> Nothing is needed here because this recipe states no per-client section — but the moment one is wanted,
> pass the name (`AddHttpResilience("Orders")`) rather than guessing it. A section no client reads fails
> startup, so a wrong guess is loud rather than silent.

## A slow endpoint (reports, exports)

The retry schedule has to fit the total budget or startup fails, so widen both.

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

`SamplingDuration` must be at least double `Timeout:Attempt`. A low-traffic client also needs a lower `MinimumThroughput`, or its breaker never engages.

## Retrying a POST safely

Only when the endpoint deduplicates on an idempotency key.

```json
{
  "HttpResilience": {
    "Clients": {
      "Payments": {
        "Retry": { "MaxRetries": 2, "RetryableMethods": [ "GET", "POST" ] }
      }
    }
  }
}
```

Set the key on the request, not in resilience configuration:

```csharp
request.Headers.TryAddWithoutValidation("Idempotency-Key", operationId.ToString());
```

## Respecting a third-party quota

A vendor allows 300 requests per minute and you run 6 replicas.

```json
{
  "HttpResilience": {
    "Clients": {
      "Vendor": {
        "RateLimiter": {
          "Enabled": true,
          "Algorithm": "TokenBucket",
          "TokenLimit": 50,
          "TokensPerPeriod": 50,
          "ReplenishmentPeriod": "00:01:00",
          "QueueLimit": 10
        }
      }
    }
  }
}
```

`300 ÷ 6 replicas = 50` per replica per minute. If the replica count changes, this number is wrong — it is a deploy-time constant, not a global quota. `TokenBucket` smooths bursts; `FixedWindow` allows a double burst across a window boundary.

## Protecting your own capacity

Stop one slow dependency from consuming all your outbound concurrency.

```json
{
  "HttpResilience": {
    "Clients": {
      "Legacy": {
        "ConcurrencyLimiter": { "Enabled": true, "Limit": 20, "QueueLimit": 50 }
      }
    }
  }
}
```

Keep `QueueLimit` small — it is capped at 1,000, and long before that a queue stops being backpressure. Queued requests hold their content buffers in memory for as long as they wait, and the wait is outside `Timeout:Total`.

`ConcurrencyLimiter:Limit` must be at most `ConcurrencyLimiter:Backstop` (1,000 by default). The backstop is the platform's own limiter, applied inside the handler, so a `Limit` above it is never reached — the excess would be rejected there rather than queued here. Startup validation rejects that combination, and that rule is also what makes it safe to skip the separate backstop handler when a client enables both a rate limiter and a cap of its own: the cap is then the tighter bound.

## Tail latency on a replicated read

```csharp
builder.Services.AddHttpClient("Search").AddHedgedHttpResilience();
```

```json
{
  "HttpResilience": {
    "Clients": {
      "Search": {
        "Timeout": { "Total": "00:00:06", "Attempt": "00:00:02" },
        "Hedging": { "Delay": "00:00:00.300", "MaxHedgedAttempts": 1 },
        "PipelineSelection": {
          "Authorities": [ "https://search.internal" ]
        }
      }
    }
  }
}
```

Set `Delay` above your p50 so the primary attempt usually wins outright. A `Delay` of zero issues every attempt at once and multiplies load unconditionally.

`Authorities` is required here. The hedging handler keeps a circuit breaker, a concurrency limiter and a metric series per authority for the life of the process, so the set of destinations has to be fixed at deploy time; a request to an unlisted authority is rejected before it reaches the pipeline.

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

Each listed authority gets its own circuit breaker. Anything unlisted shares one pipeline, which is what keeps the pipeline count fixed regardless of what hosts requests are aimed at.

Limits are not partitioned this way: `RateLimiter` and `ConcurrencyLimiter` budgets are per client and shared across authorities, because they bound this process's own capacity rather than one host's health.

## Turning resilience off during an incident

```json
{ "HttpResilience": { "Enabled": false } }
```

Requests then pass straight through with no retries, timeouts or breaker. Connection settings still apply — `Connection` is independent of `Enabled`, so you do not silently lose connection-pool tuning at the same time.

To keep timeouts and the circuit breaker but stop amplifying load, disable only retries:

```json
{ "HttpResilience": { "Retry": { "Enabled": false } } }
```

Both require a restart.

## Something the schema does not cover

Use the platform API directly. It is already referenced.

```csharp
builder.Services.AddHttpClient("Legacy")
    .AddHttpResilience()
    .AddResilienceHandler("legacy-quirk", pipeline =>
        pipeline.AddRetry(new HttpRetryStrategyOptions
        {
            ShouldHandle = args => ValueTask.FromResult(
                args.Outcome.Result?.Headers.Contains("X-Vendor-Retry") == true)
        }));
```

Handlers added after `AddHttpResilience` sit inside its pipeline.

**`AddResilienceHandler`, not `AddStandardResilienceHandler`.** The names are one word apart and the outcomes
are not:

```csharp
// DO NOT. Nests a second pipeline: retries multiply rather than add, so three configured
// attempts become NINE origin calls, and the total timeout is applied twice. Nothing throws.
builder.Services.AddHttpClient("Legacy")
    .AddHttpResilience()
    .AddStandardResilienceHandler();
```

A second `AddHttpResilience` fails at startup; the platform call above does not, because the package cannot
tell it from the recommended line — both add a `ResilienceHandler`, and the difference is an internal field. It
is reported at Information (event 12), naming both possibilities.

## Adjusting one value in code

```csharp
builder.Services.AddHttpClient("Orders")
    .AddHttpResilience(configure: options => options.Retry.MaxRetries = 1);
```

The delegate runs after configuration binding, and the result is validated like any other configuration.

`services.Configure<HttpResilienceOptions>("Orders", …)` and `services.PostConfigure<HttpResilienceOptions>("Orders", …)` work too, and reach the pipeline: it reads these options when it is built, which is after every configuration stage has run. The `configure` parameter above is still the clearest place to put it, because the change sits on the line that registers the client.

The exception is a setting that decides **which handlers the client has** — `Enabled`, `RateLimiter:Enabled`, `ConcurrencyLimiter:Enabled`, `PipelineSelection:Mode`, `Connection:Enabled`, and `Connection:AllowAutoRedirect` when it resolves to `false`. A client's handler chain is composed while the service collection is being built, so a later change to one of those would be reported without being in effect. Startup fails instead, with a message naming the setting. Use the `configure` parameter or the configuration section for those.

## Setting `HttpClient.Timeout`

Use `Timeout:Client`. Setting it in code fails at client creation:

```csharp
// Fails: "State it as Timeout:Client instead."
builder.Services.AddHttpClient("Reports")
    .AddHttpResilience()
    .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromMinutes(5));
```

```json
{
  "HttpResilience": {
    "Clients": {
      "Reports": {
        "Timeout": { "Total": "00:02:00", "Attempt": "00:00:30", "Client": "00:05:00" }
      }
    }
  }
}
```

The reason is that `Timeout:Client` is validated to be strictly greater than `Timeout:Total`, and a value set in
code is outside the schema where validation cannot see it. At or below the total budget it truncates the
pipeline instead of backing it up, with a bare `TaskCanceledException` carrying none of the pipeline's context.

Every other use of `ConfigureHttpClient` — a default header, a base address — is untouched.

## Exporting any of this to a collector

The package references no OpenTelemetry package. It publishes on a `Meter` and an `ILogger`, and nothing leaves
the process until the service wires an SDK — with none wired, everything below is silent and no error says so.

```csharp
builder.Services.AddHttpResilienceTelemetry();   // the error.type tag. Registers no meter.

builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics
        .AddMeter(HttpResilienceTelemetryExtensions.PollyMeterName)  // "Polly"
        .AddMeter(HttpResilienceTelemetryExtensions.MeterName)       // "HttpResilience.NET"
        .AddHttpClientInstrumentation()
        .AddOtlpExporter())
    .WithTracing(tracing => tracing
        .AddHttpClientInstrumentation()   // one span per attempt; this package adds none
        .AddOtlpExporter());

builder.Logging.AddOpenTelemetry(logging => logging.AddOtlpExporter());
```

Services on `OpenTelemetry.NET` package call `AddObservability(configuration)` instead. From **2.7.0**
both meter names are registered by default, so there is nothing to configure; on **2.6.x or earlier** add them to
`OpenTelemetryOptions:Meters`. `AddHttpResilienceTelemetry()` is its own call at every version. Signal-by-signal
detail is in [OPERATIONS.md](OPERATIONS.md#the-packages-own-instruments).

## Watching a limiter fill up before it rejects

```csharp
metrics.AddMeter(HttpResilienceTelemetryExtensions.MeterName);
```

```text
http.resilience.limiter.available_permits{http.client.name="Orders", http.resilience.limiter.kind="concurrency"}
http.resilience.limiter.queued_requests{http.client.name="Orders", http.resilience.limiter.kind="concurrency"}
```

`kind` is `rate`, `concurrency` or `backstop`. Graph `queued_requests` for any client with a `QueueLimit` above
0: that wait happens **outside** `Timeout:Total`, so it is latency no pipeline timeout explains.

The `backstop` series exists only when a rate limiter has displaced the backstop into a handler of its own. In
the ordinary case Polly builds it inside its own limiter slot, one per pipeline — which is what makes it per
authority under `ByAuthority` — so there is no instance to read. Set `ConcurrencyLimiter:Limit` if you need a
concurrency number you can watch.
