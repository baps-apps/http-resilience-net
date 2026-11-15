using System.Net;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HttpResilience.NET.Benchmarks;

/// <summary>
/// What the limiters cost when more than one thread is asking at once.
/// </summary>
/// <remarks>
/// A limiter is shared mutable state on the request path, so it is measured under contention. One operation
/// is <see cref="Concurrency"/> requests issued together and awaited; per-request cost is the mean divided
/// by that. Budgets are large enough that nothing is rejected.
/// </remarks>
[MemoryDiagnoser]
[HideColumns("Job")]
public class LimiterContentionBenchmarks
{
    [Params(1, 8, 64)]
    public int Concurrency { get; set; }

    private ServiceProvider _plain = null!;
    private ServiceProvider _rateLimited = null!;
    private ServiceProvider _concurrencyLimited = null!;

    private HttpClient _plainClient = null!;
    private HttpClient _rateLimitedClient = null!;
    private HttpClient _concurrencyLimitedClient = null!;

    [GlobalSetup]
    public void Setup()
    {
        _plain = Build(Settings());
        _rateLimited = Build(Settings(rateLimiter: true));
        _concurrencyLimited = Build(Settings(concurrencyLimiter: true));

        _plainClient = Client(_plain);
        _rateLimitedClient = Client(_rateLimited);
        _concurrencyLimitedClient = Client(_concurrencyLimited);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _plain.Dispose();
        _rateLimited.Dispose();
        _concurrencyLimited.Dispose();
    }

    [Benchmark(Baseline = true, Description = "no limiter")]
    public Task Plain() => FanOutAsync(_plainClient);

    [Benchmark(Description = "rate limiter")]
    public Task RateLimited() => FanOutAsync(_rateLimitedClient);

    [Benchmark(Description = "concurrency limiter")]
    public Task ConcurrencyLimited() => FanOutAsync(_concurrencyLimitedClient);

    private Task FanOutAsync(HttpClient client)
    {
        var requests = new Task<HttpResponseMessage>[Concurrency];
        for (int i = 0; i < requests.Length; i++)
        {
            requests[i] = client.GetAsync("https://origin.bench/x");
        }

        return Task.WhenAll(requests);
    }

    private static HttpClient Client(IServiceProvider provider) =>
        provider.GetRequiredService<IHttpClientFactory>().CreateClient("bench");

    private static Dictionary<string, string?> Settings(
        bool rateLimiter = false,
        bool concurrencyLimiter = false)
    {
        var settings = new Dictionary<string, string?>
        {
            ["HttpResilience:Enabled"] = "true",
            ["HttpResilience:Timeout:Total"] = "00:00:20",
            ["HttpResilience:Timeout:Attempt"] = "00:00:05",
            ["HttpResilience:Retry:MaxRetries"] = "2",
            ["HttpResilience:Retry:BaseDelay"] = "00:00:00.500",

            // Above anything this benchmark can reach, so no arm is ever rejected or queued. A rejection
            // would be measured as a cheap failure and would make the limiter look faster than no limiter.
            ["HttpResilience:ConcurrencyLimiter:Backstop"] = "10000"
        };

        if (rateLimiter)
        {
            settings["HttpResilience:RateLimiter:Enabled"] = "true";
            settings["HttpResilience:RateLimiter:PermitLimit"] = "2147483647";
            settings["HttpResilience:RateLimiter:Window"] = "01:00:00";
        }

        if (concurrencyLimiter)
        {
            settings["HttpResilience:ConcurrencyLimiter:Enabled"] = "true";
            settings["HttpResilience:ConcurrencyLimiter:Limit"] = "10000";
            settings["HttpResilience:ConcurrencyLimiter:QueueLimit"] = "1000";
        }

        return settings;
    }

    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        IConfigurationRoot configuration =
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddHttpResilience(configuration);
        services.AddHttpClient("bench")
            .AddHttpResilienceHandler()
            .ConfigurePrimaryHttpMessageHandler(() => new NoOpOrigin());

        return services.BuildServiceProvider();
    }

    private sealed class NoOpOrigin : HttpMessageHandler
    {
        private static readonly Task<HttpResponseMessage> _ok =
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => _ok;
    }
}
