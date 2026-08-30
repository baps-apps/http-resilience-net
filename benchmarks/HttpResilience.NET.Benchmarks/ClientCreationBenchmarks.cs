using System.Net;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HttpResilience.NET.Benchmarks;

/// <summary>
/// The cost of <c>IHttpClientFactory.CreateClient</c>, which a typed client pays per request.
/// </summary>
/// <remarks>
/// Every <c>ConfigureHttpClient</c> action registered for a client runs on each call, not once. A disabled
/// client carries one such action -- the notice that says resilience is registered but switched off -- so it
/// has to cost a field read after the first call rather than a container lookup and a dictionary probe.
/// </remarks>
[MemoryDiagnoser]
// Only the job column is hidden. Error, StdDev and RatioSD stay: a committed table that reports a
// ratio without its dispersion invites a reader to believe a 1.2x difference that is inside the noise,
// and that is exactly what an earlier revision of these results did.
[HideColumns("Job")]
public class ClientCreationBenchmarks
{
    private ServiceProvider _bare = null!;
    private ServiceProvider _enabled = null!;
    private ServiceProvider _disabled = null!;

    private IHttpClientFactory _bareFactory = null!;
    private IHttpClientFactory _enabledFactory = null!;
    private IHttpClientFactory _disabledFactory = null!;

    [GlobalSetup]
    public void Setup()
    {
        _bare = BuildBare();
        _enabled = Build(enabled: true);
        _disabled = Build(enabled: false);

        _bareFactory = _bare.GetRequiredService<IHttpClientFactory>();
        _enabledFactory = _enabled.GetRequiredService<IHttpClientFactory>();
        _disabledFactory = _disabled.GetRequiredService<IHttpClientFactory>();

        // The first call on a disabled client logs; measure the steady state that follows it.
        _disabledFactory.CreateClient("bench").Dispose();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _bare.Dispose();
        _enabled.Dispose();
        _disabled.Dispose();
    }

    [Benchmark(Baseline = true, Description = "IHttpClientFactory only")]
    public HttpClient Bare() => _bareFactory.CreateClient("bench");

    [Benchmark(Description = "resilience enabled")]
    public HttpClient Enabled() => _enabledFactory.CreateClient("bench");

    [Benchmark(Description = "resilience registered but disabled")]
    public HttpClient Disabled() => _disabledFactory.CreateClient("bench");

    private static ServiceProvider Build(bool enabled)
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HttpResilience:Enabled"] = enabled ? "true" : "false",
                ["HttpResilience:Timeout:Total"] = "00:00:20",
                ["HttpResilience:Timeout:Attempt"] = "00:00:05",
                ["HttpResilience:Retry:MaxRetries"] = "2",
                ["HttpResilience:Retry:BaseDelay"] = "00:00:00.500"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilience(configuration);
        services.AddHttpClient("bench")
            .AddHttpResilience()
            .AddHttpMessageHandler(() => new NoOpOrigin());

        return services.BuildServiceProvider();
    }

    private static ServiceProvider BuildBare()
    {
        var services = new ServiceCollection();
        services.AddHttpClient("bench").AddHttpMessageHandler(() => new NoOpOrigin());
        return services.BuildServiceProvider();
    }

    private sealed class NoOpOrigin : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}
