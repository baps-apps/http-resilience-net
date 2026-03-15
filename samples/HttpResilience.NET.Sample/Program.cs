using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using HttpResilience.NET.Abstractions;
using HttpResilience.NET.Extensions;

// ---------------------------------------------------------------------------
// Build configuration and services
// ---------------------------------------------------------------------------

var configuration = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: false)
    .AddEnvironmentVariables()
    .Build();

var services = new ServiceCollection();

services.AddLogging(builder =>
{
    builder.AddSimpleConsole(options => options.TimestampFormat = "HH:mm:ss ");
});

// Register HTTP resilience options from the default "HttpResilienceOptions" section (validates at startup).
services.AddHttpResilienceOptions(configuration);

// Enable Polly telemetry so resilience metrics (retries, circuit breaker, etc.) can be collected.
services.AddHttpResilienceTelemetry();

// ---------------------------------------------------------------------------
// Scenario 1: Standard pipeline (default section)
// Typical internal REST API: retry, circuit breaker, timeouts.
// ---------------------------------------------------------------------------
services.AddHttpClient("Standard", client =>
{
    client.BaseAddress = new Uri("https://httpbin.org/");
})
.AddHttpClientWithResilience(configuration);

// ---------------------------------------------------------------------------
// Scenario 2: Per-client timeout override
// Same config as default but longer total timeout (e.g. for background jobs).
// ---------------------------------------------------------------------------
services.AddHttpClient("LongTimeout", client =>
{
    client.BaseAddress = new Uri("https://httpbin.org/");
})
.AddHttpClientWithResilience(configuration, requestTimeoutSeconds: 60);

// ---------------------------------------------------------------------------
// Scenario 3: Hedging pipeline (section-based config)
// Latency-sensitive: multiple requests, first success wins. Uses nested section.
// ---------------------------------------------------------------------------
var hedgingSection = configuration.GetSection("HttpResilienceOptions:Hedging");
services.AddHttpClient("Hedging", client =>
{
    client.BaseAddress = new Uri("https://httpbin.org/");
})
.AddHttpClientWithResilience(hedgingSection);

// ---------------------------------------------------------------------------
// Scenario 4: Fallback (synthetic response on failure)
// Returns 503 + body when all retries fail; no exception thrown.
// ---------------------------------------------------------------------------
var fallbackSection = configuration.GetSection("HttpResilienceOptions:WithFallback");
services.AddHttpClient("WithFallback", client =>
{
    client.BaseAddress = new Uri("https://httpbin.org/");
})
.AddHttpClientWithResilience(fallbackSection);

// ---------------------------------------------------------------------------
// Scenario 5: Custom fallback handler (IHttpFallbackHandler)
// Invoked first on failure; can return cached response, call alternate URL, or log.
// Returning null falls through to the synthetic response from options.
// In production you might resolve IHttpFallbackHandler from DI (if the API supports a factory).
// ---------------------------------------------------------------------------
services.AddHttpClient("WithCustomFallback", client =>
{
    client.BaseAddress = new Uri("https://httpbin.org/");
})
.AddHttpClientWithResilience(fallbackSection, requestTimeoutSeconds: null, fallbackHandler: new SampleCustomFallbackHandler());

// ---------------------------------------------------------------------------
// Scenario 6: Bulkhead + rate limiter (section-based config)
// Protects downstream: limits concurrent requests and requests per window.
// ---------------------------------------------------------------------------
var bulkheadSection = configuration.GetSection("HttpResilienceOptions:BulkheadAndRateLimit");
services.AddHttpClient("BulkheadAndRateLimit", client =>
{
    client.BaseAddress = new Uri("https://httpbin.org/");
})
.AddHttpClientWithResilience(bulkheadSection);

// ---------------------------------------------------------------------------
// Scenario 7: Multi-tenant / per-section config
// Different named clients use different option sets (e.g. TenantA vs TenantB).
// ---------------------------------------------------------------------------
var tenantASection = configuration.GetSection("HttpResilienceOptions:TenantA");
var tenantBSection = configuration.GetSection("HttpResilienceOptions:TenantB");

services.AddHttpClient("TenantA", client =>
{
    client.BaseAddress = new Uri("https://httpbin.org/");
})
.AddHttpClientWithResilience(tenantASection);

services.AddHttpClient("TenantB", client =>
{
    client.BaseAddress = new Uri("https://httpbin.org/");
})
.AddHttpClientWithResilience(tenantBSection);

// ---------------------------------------------------------------------------
// Build and run
// ---------------------------------------------------------------------------

using var provider = services.BuildServiceProvider();
var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
var httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();

var logger = loggerFactory.CreateLogger("Sample");

async Task RunScenarioAsync(string name, string clientName, string path = "/get")
{
    logger.LogInformation("--- Scenario: {Name} (client: {Client}) ---", name, clientName);
    try
    {
        var client = httpClientFactory.CreateClient(clientName);
        using var response = await client.GetAsync(path);
        logger.LogInformation("  Status: {StatusCode}", (int)response.StatusCode);
        if (response.Content.Headers.ContentLength is > 0 and var len)
            logger.LogInformation("  Content-Length: {Length}", len);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "  Request failed (expected in some demos if endpoint is down)");
    }

    logger.LogInformation("");
}

// Run all scenarios in sequence to demonstrate each use case.
logger.LogInformation("HttpResilience.NET sample – running all scenarios");
logger.LogInformation("");

await RunScenarioAsync("1. Standard pipeline (retry, circuit breaker, timeouts)", "Standard");
await RunScenarioAsync("2. Long timeout override (60s total)", "LongTimeout");
await RunScenarioAsync("3. Hedging pipeline (section-based config)", "Hedging");
await RunScenarioAsync("4. Fallback (synthetic 503 on failure)", "WithFallback");
await RunScenarioAsync("5. Custom fallback handler (IHttpFallbackHandler)", "WithCustomFallback");
await RunScenarioAsync("6. Bulkhead + rate limiter", "BulkheadAndRateLimit");
await RunScenarioAsync("7a. Multi-tenant: TenantA (section-based)", "TenantA");
await RunScenarioAsync("7b. Multi-tenant: TenantB (hedging section)", "TenantB");

logger.LogInformation("Done.");

// ---------------------------------------------------------------------------
// Sample custom fallback handler: observes the failure and returns null so the
// pipeline uses the synthetic response from Fallback options (StatusCode + ResponseBody).
// In production you could return a new HttpResponseMessage (e.g. cached or from another URL).
// ---------------------------------------------------------------------------
file sealed class SampleCustomFallbackHandler : IHttpFallbackHandler
{
    public ValueTask<HttpResponseMessage?> TryHandleAsync(HttpFallbackContext context, CancellationToken cancellationToken)
    {
        if (context.HasResult && context.Result is { } response)
            Console.WriteLine("  [CustomFallback] Observed failed response: " + (int)response.StatusCode);
        else if (context.Exception is { } ex)
            Console.WriteLine("  [CustomFallback] Observed exception: " + ex.Message);

        // Return null to use the synthetic response from Fallback options (StatusCode, ResponseBody).
        // Alternatively: return ValueTask.FromResult<HttpResponseMessage?>(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("cached") });
        return ValueTask.FromResult<HttpResponseMessage?>(null);
    }
}
