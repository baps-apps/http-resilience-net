using System.Net;
using HttpResilience.NET.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

// Asserts, in a Native AOT binary, that configuration values actually reached the options and the pipeline.
// Trimming a reflection-based binder does not fail loudly -- it silently leaves a client running on defaults
// it never configured -- so the check that matters is a bound value read back at run time, not a clean build.

var configuration = new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["HttpResilience:Enabled"] = "true",
        ["HttpResilience:Timeout:Total"] = "00:00:12",
        ["HttpResilience:Timeout:Attempt"] = "00:00:03",
        ["HttpResilience:Retry:MaxRetries"] = "2",
        ["HttpResilience:Retry:BaseDelay"] = "00:00:00",
        ["HttpResilience:Retry:BackoffType"] = "Constant",
        ["HttpResilience:CircuitBreaker:MinimumThroughput"] = "1000",
        ["HttpResilience:CircuitBreaker:SamplingDuration"] = "00:00:30",
        ["HttpResilience:ConcurrencyLimiter:Backstop"] = "250",
        ["HttpResilience:Clients:Orders:Retry:MaxRetries"] = "3",
        ["HttpResilience:Clients:Orders:PipelineSelection:Mode"] = "ByAuthority",
        ["HttpResilience:Clients:Orders:PipelineSelection:Authorities:0"] = "http://origin.test"
    })
    .Build();

var services = new ServiceCollection();
services.AddHttpResilience(configuration);
services.AddHttpResilienceTelemetry();
services.AddHttpResilienceHealthChecks();
services.AddHttpClient("Orders")
    .AddHttpResilience()
    .AddHttpMessageHandler(() => new CountingHandler());

await using ServiceProvider provider = services.BuildServiceProvider();

foreach (IStartupValidator validator in provider.GetServices<IStartupValidator>())
{
    validator.Validate();
}

var failures = new List<string>();

HttpResilienceOptions options = provider.GetRequiredService<IOptionsMonitor<HttpResilienceOptions>>().Get("Orders");
Check(options.Enabled, "Enabled bound");
Check(options.Timeout.Total == TimeSpan.FromSeconds(12), "TimeSpan bound");
Check(options.Retry.MaxRetries == 3, "per-client override bound");
Check(options.Retry.BackoffType == RetryBackoffType.Constant, "enum bound by name");
Check(options.ConcurrencyLimiter.Backstop == 250, "backstop inherited from the root");
Check(options.PipelineSelection.Mode == PipelineSelectionMode.ByAuthority, "nested enum bound");
Check(options.PipelineSelection.Authorities is ["http://origin.test"], "string list bound");

HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("Orders");
HttpResponseMessage response = await client.GetAsync("http://origin.test/x");
Check(response.StatusCode == HttpStatusCode.InternalServerError, "request completed through the pipeline");
Check(CountingHandler.Calls == 4, $"retried to the configured count (saw {CountingHandler.Calls})");

if (failures.Count > 0)
{
    Console.Error.WriteLine($"AOT smoke FAILED:{Environment.NewLine}  {string.Join($"{Environment.NewLine}  ", failures)}");
    return 1;
}

Console.WriteLine("AOT smoke passed: configuration bound and the pipeline ran under Native AOT.");
return 0;

void Check(bool condition, string what)
{
    if (!condition)
    {
        failures.Add(what);
    }
}

internal sealed class CountingHandler : DelegatingHandler
{
    private static int _calls;

    public static int Calls => Volatile.Read(ref _calls);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError) { RequestMessage = request });
    }
}
