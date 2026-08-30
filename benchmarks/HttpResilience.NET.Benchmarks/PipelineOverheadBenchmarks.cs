using System.Net;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;

namespace HttpResilience.NET.Benchmarks;

/// <summary>
/// Measures what this package costs on top of the pipeline it configures.
/// </summary>
/// <remarks>
/// The origin is an in-memory handler so the transport is not the variable: everything measured here is
/// per-request pipeline and telemetry work. The number that matters is the gap between
/// <see cref="MicrosoftStandardHandler"/> and <see cref="HttpResilienceStandard"/> -- that gap is this
/// package's overhead, and it should be within noise.
/// </remarks>
[MemoryDiagnoser]
// Only the job column is hidden. Error, StdDev and RatioSD stay: a committed table that reports a
// ratio without its dispersion invites a reader to believe a 1.2x difference that is inside the noise,
// and that is exactly what an earlier revision of these results did.
[HideColumns("Job")]
public class PipelineOverheadBenchmarks
{
    private ServiceProvider _bare = null!;
    private ServiceProvider _microsoft = null!;
    private ServiceProvider _standard = null!;
    private ServiceProvider _withRateLimiter = null!;
    private ServiceProvider _withConcurrencyLimiter = null!;
    private ServiceProvider _withBothLimiters = null!;
    private ServiceProvider _withTelemetry = null!;
    private ServiceProvider _byAuthority = null!;

    private HttpClient _bareClient = null!;
    private HttpClient _microsoftClient = null!;
    private HttpClient _standardClient = null!;
    private HttpClient _rateLimitedClient = null!;
    private HttpClient _concurrencyLimitedClient = null!;
    private HttpClient _bothLimitersClient = null!;
    private HttpClient _telemetryClient = null!;
    private HttpClient _byAuthorityClient = null!;

    /// <summary>Number of distinct authorities exercised in the per-authority benchmark.</summary>
    [Params(1, 100)]
    public int Authorities { get; set; }

    private string[] _urls = null!;
    private int _cursor;

    [GlobalSetup]
    public void Setup()
    {
        _urls = [.. Enumerable.Range(0, Math.Max(Authorities, 1)).Select(i => $"https://host-{i}.bench/x")];

        _bare = BuildBare();
        _microsoft = BuildMicrosoft();
        _standard = Build(Settings());
        _withRateLimiter = Build(Settings(rateLimiter: true));
        _withConcurrencyLimiter = Build(Settings(concurrencyLimiter: true));
        _withBothLimiters = Build(Settings(rateLimiter: true, concurrencyLimiter: true));
        _withTelemetry = Build(Settings(), telemetry: true);
        _byAuthority = Build(Settings(authorities: _urls));

        _bareClient = Client(_bare);
        _microsoftClient = Client(_microsoft);
        _standardClient = Client(_standard);
        _rateLimitedClient = Client(_withRateLimiter);
        _concurrencyLimitedClient = Client(_withConcurrencyLimiter);
        _bothLimitersClient = Client(_withBothLimiters);
        _telemetryClient = Client(_withTelemetry);
        _byAuthorityClient = Client(_byAuthority);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        foreach (ServiceProvider provider in new[]
        {
            _bare, _microsoft, _standard, _withRateLimiter, _withConcurrencyLimiter, _withBothLimiters,
            _withTelemetry, _byAuthority
        })
        {
            provider.Dispose();
        }
    }

    private string NextUrl() => _urls[_cursor++ % _urls.Length];

    [Benchmark(Baseline = true, Description = "IHttpClientFactory only")]
    public Task<HttpResponseMessage> Bare() => _bareClient.GetAsync(NextUrl());

    [Benchmark(Description = "Microsoft standard handler")]
    public Task<HttpResponseMessage> MicrosoftStandardHandler() => _microsoftClient.GetAsync(NextUrl());

    [Benchmark(Description = "HttpResilience standard")]
    public Task<HttpResponseMessage> HttpResilienceStandard() => _standardClient.GetAsync(NextUrl());

    [Benchmark(Description = "+ rate limiter")]
    public Task<HttpResponseMessage> WithRateLimiter() => _rateLimitedClient.GetAsync(NextUrl());

    [Benchmark(Description = "+ concurrency limiter")]
    public Task<HttpResponseMessage> WithConcurrencyLimiter() => _concurrencyLimitedClient.GetAsync(NextUrl());

    /// <summary>
    /// A rate limiter displaces the handler's own limiter, so the concurrency backstop is re-added outside
    /// it -- unless the client already has a cap of its own, which bounds concurrency below the backstop and
    /// makes the extra handler redundant. This measures that it really is redundant rather than merely
    /// argued to be.
    /// </summary>
    [Benchmark(Description = "+ rate limiter + concurrency cap")]
    public Task<HttpResponseMessage> WithBothLimiters() => _bothLimitersClient.GetAsync(NextUrl());

    [Benchmark(Description = "+ telemetry enrichment")]
    public Task<HttpResponseMessage> WithTelemetry() => _telemetryClient.GetAsync(NextUrl());

    [Benchmark(Description = "+ per-authority pipelines")]
    public Task<HttpResponseMessage> ByAuthority() => _byAuthorityClient.GetAsync(NextUrl());

    private static HttpClient Client(IServiceProvider provider) =>
        provider.GetRequiredService<IHttpClientFactory>().CreateClient("bench");

    private static Dictionary<string, string?> Settings(
        bool rateLimiter = false,
        bool concurrencyLimiter = false,
        string[]? authorities = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["HttpResilience:Enabled"] = "true",
            ["HttpResilience:Timeout:Total"] = "00:00:20",
            ["HttpResilience:Timeout:Attempt"] = "00:00:05",
            ["HttpResilience:Retry:MaxRetries"] = "2",
            ["HttpResilience:Retry:BaseDelay"] = "00:00:00.500"
        };

        if (rateLimiter)
        {
            settings["HttpResilience:RateLimiter:Enabled"] = "true";

            // The budget has to be larger than anything BenchmarkDotNet can spend inside one window, or the
            // limiter starts rejecting and the scenario reports no result at all. What is being measured is
            // the cost of acquiring a permit, not what happens when one is refused.
            settings["HttpResilience:RateLimiter:PermitLimit"] = "2147483647";
            settings["HttpResilience:RateLimiter:Window"] = "01:00:00";
        }

        if (concurrencyLimiter)
        {
            settings["HttpResilience:ConcurrencyLimiter:Enabled"] = "true";
            settings["HttpResilience:ConcurrencyLimiter:Limit"] = "1000";
            settings["HttpResilience:ConcurrencyLimiter:QueueLimit"] = "1000";
        }

        if (authorities is { Length: > 0 })
        {
            settings["HttpResilience:PipelineSelection:Mode"] = "ByAuthority";
            for (int i = 0; i < authorities.Length; i++)
            {
                settings[$"HttpResilience:PipelineSelection:Authorities:{i}"] =
                    new Uri(authorities[i]).GetLeftPart(UriPartial.Authority);
            }
        }

        return settings;
    }

    private static ServiceProvider Build(Dictionary<string, string?> settings, bool telemetry = false)
    {
        IConfigurationRoot configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddHttpResilience(configuration);
        if (telemetry)
        {
            services.AddHttpResilienceTelemetry();
        }

        services.AddHttpClient("bench")
            .AddHttpResilience()
            .ConfigurePrimaryHttpMessageHandler(() => new NoOpOrigin());

        return services.BuildServiceProvider();
    }

    private static ServiceProvider BuildBare()
    {
        var services = new ServiceCollection();
        services.AddHttpClient("bench").ConfigurePrimaryHttpMessageHandler(() => new NoOpOrigin());
        return services.BuildServiceProvider();
    }

    private static ServiceProvider BuildMicrosoft()
    {
        var services = new ServiceCollection();
        services.AddHttpClient("bench")
            .AddStandardResilienceHandler()
            .Configure(options =>
            {
                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(20);
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(5);
                options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
                options.Retry.MaxRetryAttempts = 2;
            });

        services.AddHttpClient("bench").ConfigurePrimaryHttpMessageHandler(() => new NoOpOrigin());
        return services.BuildServiceProvider();
    }

    private sealed class NoOpOrigin : HttpMessageHandler
    {
        private static readonly Task<HttpResponseMessage> _ok =
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => _ok;
    }
}
