using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using HttpResilience.NET.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// ---------------------------------------------------------------------------
// Configuration and services
// ---------------------------------------------------------------------------

IConfigurationRoot configuration = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: false)
    .AddEnvironmentVariables()
    .Build();

var services = new ServiceCollection();

services.AddLogging(builder => builder.AddSimpleConsole(options => options.TimestampFormat = "HH:mm:ss "));

// One call registers the schema, validates it at startup, and makes the root section available to every
// client registered below.
services.AddHttpResilience(configuration);

// That call also registers a hosted service which creates every client this package configured, once, at
// host start -- so in a real application the primary-handler mismatch demonstrated by the 'Broken' client at
// the end of this file fails the deployment rather than the first request that happens to reach it. Nothing
// runs it here, because this sample builds a raw ServiceProvider rather than an IHost, which is what lets
// the demonstration below show the failure rather than prevent it. Set
// HttpResilience:ValidateClientsOnStart to false to opt a real service out of it.

// error.type on Polly's metrics. Register the meter itself with
// metrics.AddMeter(HttpResilienceTelemetryExtensions.PollyMeterName) and .MeterName in a real app --
// this call adds the error.type tag and registers no meter. See README.md, "Telemetry".
services.AddHttpResilienceTelemetry();

// Circuit breaker state as a dependency health check. Never wire this to a liveness or readiness probe.
services.AddHttpResilienceHealthChecks();

// A stub origin stands in for real dependencies so the sample runs offline. It is added as the innermost
// delegating handler rather than as the primary handler: Connection:Enabled is on, and a client whose
// primary handler is not a SocketsHttpHandler has nothing for PooledConnectionLifetime to bound. Replacing
// the primary handler here would be the one wiring mistake this package refuses to let a service make.
services.AddSingleton<StubOrigin>();

// Root defaults only.
services.AddHttpClient("Default")
    .AddHttpResilience()
    .AddHttpMessageHandler(sp => new StubOriginHandler(sp.GetRequiredService<StubOrigin>()));

// Overrides from HttpResilience:Clients:Orders -- the section name defaults to the client name, so it is
// written once. Everything the section does not state is inherited from the root.
services.AddHttpClient("Orders")
    .AddHttpResilience()
    .AddHttpMessageHandler(sp => new StubOriginHandler(sp.GetRequiredService<StubOrigin>()));

// Retries POST. The decision is visible in configuration and reviewable in a diff, and the endpoint is
// expected to deduplicate on an idempotency key.
services.AddHttpClient("Payments")
    .AddHttpResilience()
    .AddHttpMessageHandler(sp => new StubOriginHandler(sp.GetRequiredService<StubOrigin>()));

// Hedging is selected in code, never by a configuration value, because it multiplies outbound traffic. The
// authorities it may call are listed in configuration, because the hedging handler keeps a breaker, a limiter
// and a metric series per authority for the life of the process.
services.AddHttpClient("Search")
    .AddHedgedHttpResilience()
    .AddHttpMessageHandler(sp => new StubOriginHandler(sp.GetRequiredService<StubOrigin>()));

// One client, several hosts, isolated circuit breakers, bounded by an authority allow-list.
services.AddHttpClient("Partner")
    .AddHttpResilience()
    .AddHttpMessageHandler(sp => new StubOriginHandler(sp.GetRequiredService<StubOrigin>()));

await using ServiceProvider provider = services.BuildServiceProvider();

// This is what the host does on startup: misconfiguration fails here, before any traffic is served.
foreach (IStartupValidator validator in provider.GetServices<IStartupValidator>())
{
    validator.Validate();
}

ILogger logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Sample");
IHttpClientFactory factory = provider.GetRequiredService<IHttpClientFactory>();
StubOrigin origin = provider.GetRequiredService<StubOrigin>();

// ---------------------------------------------------------------------------
// 1. A transient failure is retried
// ---------------------------------------------------------------------------

origin.Reset(failuresBeforeSuccess: 2);
HttpResponseMessage recovered = await factory.CreateClient("Default").GetAsync("https://origin.example/orders");
logger.LogInformation("Default GET  -> {Status} after {Calls} origin call(s)", (int)recovered.StatusCode, origin.Calls);

// ---------------------------------------------------------------------------
// 2. A POST is delivered exactly once, even though it fails
// ---------------------------------------------------------------------------

origin.Reset(alwaysFail: true);
using var order = new StringContent("""{"sku":"ABC","qty":1}""", Encoding.UTF8, "application/json");
HttpResponseMessage posted = await factory.CreateClient("Orders").PostAsync("https://origin.example/orders", order);
logger.LogInformation(
    "Orders POST  -> {Status} after {Calls} origin call(s). Only GET, HEAD, OPTIONS and TRACE are retried by default.",
    (int)posted.StatusCode, origin.Calls);

// ---------------------------------------------------------------------------
// 3. A POST is retried, because this client opted in explicitly
// ---------------------------------------------------------------------------

origin.Reset(failuresBeforeSuccess: 1);
using var payment = new StringContent("""{"amount":100}""", Encoding.UTF8, "application/json");
HttpResponseMessage paid = await factory.CreateClient("Payments").PostAsync("https://origin.example/pay", payment);
logger.LogInformation(
    "Payments POST -> {Status} after {Calls} origin call(s). Retry:RetryableMethods includes POST.",
    (int)paid.StatusCode, origin.Calls);

// ---------------------------------------------------------------------------
// 4. Hedging races a second attempt for tail latency
// ---------------------------------------------------------------------------

origin.Reset(slowFirstCall: TimeSpan.FromSeconds(1));
HttpResponseMessage hedged = await factory.CreateClient("Search").GetAsync("https://origin.example/search?q=x");
logger.LogInformation(
    "Search GET   -> {Status} after {Calls} origin call(s). The faster attempt won.",
    (int)hedged.StatusCode, origin.Calls);

// ---------------------------------------------------------------------------
// 5. Rate limiter scope, read the supported way
// ---------------------------------------------------------------------------
//
// Through the gauge, not by resolving the limiter out of the container. The limiter is a keyed
// singleton under a key this package owns and keeps internal, deliberately: keyed service keys share
// one namespace per service type, RateLimiter is a BCL type, and an application keying its own
// limiter by a domain name like "Search" would otherwise replace the one the pipeline enforces --
// silently, in either direction. What is supported is the instrument, which is also what the runbook
// alerts on.

long? availablePermits = null;
using (var meterListener = new MeterListener())
{
    meterListener.InstrumentPublished = (instrument, listener) =>
    {
        if (instrument.Meter.Name == HttpResilienceTelemetryExtensions.MeterName &&
            instrument.Name == "http.resilience.limiter.available_permits")
        {
            listener.EnableMeasurementEvents(instrument);
        }
    };
    meterListener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
    {
        // One instrument reports every limiter a client owns, so the kind has to be matched as well as the
        // client: this client has a rate limiter and, because that limiter took the platform's slot, a
        // concurrency backstop in a handler of its own.
        bool isThisClient = false;
        bool isRateLimiter = false;

        foreach (KeyValuePair<string, object?> tag in tags)
        {
            isThisClient |= tag.Key == "http.client.name" && (string?)tag.Value == "Search";
            isRateLimiter |= tag.Key == "http.resilience.limiter.kind" && (string?)tag.Value == "rate";
        }

        if (isThisClient && isRateLimiter)
        {
            availablePermits = measurement;
        }
    });
    meterListener.Start();
    meterListener.RecordObservableInstruments();
}

logger.LogInformation(
    "Search limiter has {Available} permit(s) available, read from http.resilience.limiter.available_permits (kind=rate). This is a process-local, per-client budget: the fleet-wide rate is replicas x clients x this number.",
    availablePermits);

// ---------------------------------------------------------------------------
// 6. Dependency health
// ---------------------------------------------------------------------------

HealthReport report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync();
logger.LogInformation("Dependency health: {Status}. Degraded here would mean a downstream is unhealthy, not this process.", report.Status);

// ---------------------------------------------------------------------------
// 7. Connection settings are owned, not merely requested
// ---------------------------------------------------------------------------
// Enabling Connection:Enabled disables IHttpClientFactory handler rotation, because
// PooledConnectionLifetime is supposed to bound connection age instead. A primary handler that cannot
// carry that setting would leave nothing recycling the pool or re-resolving DNS for the life of the
// process. That is refused rather than accepted quietly:

var broken = new ServiceCollection();
broken.AddHttpResilience(configuration);
broken.AddHttpClient("Broken")
    .AddHttpResilience("Default")
    .ConfigurePrimaryHttpMessageHandler(() => new StubPrimaryHandler());

await using ServiceProvider brokenProvider = broken.BuildServiceProvider();
try
{
    _ = brokenProvider.GetRequiredService<IHttpClientFactory>().CreateClient("Broken");
    logger.LogError("Expected the primary-handler guard to fire.");
}
catch (InvalidOperationException expected)
{
    logger.LogInformation("Primary-handler guard: {Message}", expected.Message);
}

// The settings the guard protects, read back from the options the pipeline was built from:
ConnectionOptions connection = provider
    .GetRequiredService<IOptionsMonitor<HttpResilienceOptions>>().Get("Default").Connection;
logger.LogInformation(
    "Default connection settings: PooledConnectionLifetime={Lifetime}, ConnectTimeout={ConnectTimeout}. " +
    "Factory handler rotation is disabled, so the lifetime is what bounds DNS staleness.",
    connection.PooledConnectionLifetime, connection.ConnectTimeout);

/// <summary>
/// Stands in for a real dependency so the sample runs with no network access.
/// </summary>
/// <remarks>
/// Shared state, separate handler. A <see cref="DelegatingHandler"/> instance may not be reused across
/// clients -- <c>IHttpClientFactory</c> sets its inner handler once -- so the counters live here and each
/// client gets its own <see cref="StubOriginHandler"/> over them.
/// </remarks>
internal sealed class StubOrigin
{
    private int _calls;
    private int _failuresBeforeSuccess;
    private bool _alwaysFail;
    private TimeSpan _slowFirstCall;

    public int Calls => Volatile.Read(ref _calls);

    public void Reset(int failuresBeforeSuccess = 0, bool alwaysFail = false, TimeSpan slowFirstCall = default)
    {
        Interlocked.Exchange(ref _calls, 0);
        _failuresBeforeSuccess = failuresBeforeSuccess;
        _alwaysFail = alwaysFail;
        _slowFirstCall = slowFirstCall;
    }

    public async Task<HttpResponseMessage> RespondAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        int call = Interlocked.Increment(ref _calls);

        if (call == 1 && _slowFirstCall > TimeSpan.Zero)
        {
            await Task.Delay(_slowFirstCall, cancellationToken);
        }

        HttpStatusCode status = _alwaysFail || call <= _failuresBeforeSuccess
            ? HttpStatusCode.ServiceUnavailable
            : HttpStatusCode.OK;

        return new HttpResponseMessage(status) { RequestMessage = request };
    }
}

/// <summary>
/// The innermost delegating handler, so the sample answers requests without leaving the process while the
/// package keeps ownership of the primary handler.
/// </summary>
internal sealed class StubOriginHandler : DelegatingHandler
{
    private readonly StubOrigin _origin;

    public StubOriginHandler(StubOrigin origin) => _origin = origin;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        _origin.RespondAsync(request, cancellationToken);
}

/// <summary>
/// A primary handler this package cannot apply connection settings to, used to show the guard firing.
/// </summary>
internal sealed class StubPrimaryHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request });
}
