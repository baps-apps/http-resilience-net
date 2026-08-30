using System.Net;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HttpResilience.NET.Benchmarks;

/// <summary>
/// The hedging pipeline, which nothing measured until now.
/// </summary>
/// <remarks>
/// Two pieces of this package's own work sit on the hedged request path and on no other. The authority
/// allow-list handler runs once per logical request; the <c>ActionGenerator</c> guard that closes the timer
/// path runs once per <i>supplementary attempt considered</i>, and it reads
/// <c>IOptionsMonitor&lt;HttpResilienceOptions&gt;.Get(name)</c> when it does -- a concurrent-dictionary
/// probe on the attempt path rather than at pipeline build.
/// <para>
/// Both arms use a <b>slow</b> origin. An origin that answers immediately never starts a hedged attempt at
/// all, so a fast-origin benchmark would measure the pipeline with its distinguishing feature switched off --
/// the same mistake that let a suite of hedging tests pass while POST bodies arrived four times.
/// </para>
/// <para>
/// The delay is what dominates the wall clock here, so read the <b>allocation</b> columns and the difference
/// between the safe and suppressed arms rather than the means. That is the number this package controls.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[HideColumns("Job")]
public class HedgingOverheadBenchmarks
{
    /// <summary>How long the origin holds an attempt. Comfortably past the hedging delay below.</summary>
    private static readonly TimeSpan _originDelay = TimeSpan.FromMilliseconds(20);

    private ServiceProvider _hedged = null!;
    private ServiceProvider _standard = null!;

    private HttpClient _hedgedClient = null!;
    private HttpClient _standardClient = null!;

    [GlobalSetup]
    public void Setup()
    {
        _hedged = BuildHedged();
        _standard = BuildStandard();

        _hedgedClient = Client(_hedged);
        _standardClient = Client(_standard);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _hedged.Dispose();
        _standard.Dispose();
    }

    /// <summary>
    /// The baseline: the same slow origin through the standard pipeline, which starts no second attempt.
    /// </summary>
    [Benchmark(Baseline = true, Description = "standard pipeline, slow origin")]
    public Task<HttpResponseMessage> Standard() => _standardClient.GetAsync(Url);

    /// <summary>
    /// A safe method against a slow origin: the hedging timer fires and a supplementary attempt really is
    /// started, so this is the arm that pays for the fan-out.
    /// </summary>
    [Benchmark(Description = "hedged GET (attempt is started)")]
    public Task<HttpResponseMessage> HedgedSafe() => _hedgedClient.GetAsync(Url);

    /// <summary>
    /// The same slow origin with a POST: the timer fires, the <c>ActionGenerator</c> guard is consulted and
    /// returns null, and no second request goes on the wire. The delta against the row above is what the
    /// safety guarantee costs when it is doing its job.
    /// </summary>
    [Benchmark(Description = "hedged POST (attempt is suppressed)")]
    public async Task<HttpResponseMessage> HedgedUnsafe()
    {
        using var content = new StringContent("{}");
        return await _hedgedClient.PostAsync(Url, content);
    }

    private const string Url = "https://origin.bench/x";

    private static HttpClient Client(IServiceProvider provider) =>
        provider.GetRequiredService<IHttpClientFactory>().CreateClient("bench");

    private static Dictionary<string, string?> Settings() => new()
    {
        ["HttpResilience:Enabled"] = "true",
        ["HttpResilience:Timeout:Total"] = "00:00:20",
        ["HttpResilience:Timeout:Attempt"] = "00:00:05",
        ["HttpResilience:Retry:MaxRetries"] = "2",
        ["HttpResilience:Retry:BaseDelay"] = "00:00:00.500",
        ["HttpResilience:Hedging:Delay"] = "00:00:00.005",
        ["HttpResilience:Hedging:MaxHedgedAttempts"] = "1",
        ["HttpResilience:PipelineSelection:Authorities:0"] = "https://origin.bench"
    };

    private static ServiceProvider BuildHedged()
    {
        IConfigurationRoot configuration =
            new ConfigurationBuilder().AddInMemoryCollection(Settings()).Build();

        var services = new ServiceCollection();
        services.AddHttpResilience(configuration);
        services.AddHttpClient("bench")
            .AddHedgedHttpResilience()
            .ConfigurePrimaryHttpMessageHandler(() => new SlowOrigin(_originDelay));

        return services.BuildServiceProvider();
    }

    private static ServiceProvider BuildStandard()
    {
        Dictionary<string, string?> settings = Settings();

        // A standard client declares no closed destination set, so it must not carry an allow-list.
        settings.Remove("HttpResilience:PipelineSelection:Authorities:0");

        IConfigurationRoot configuration =
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddHttpResilience(configuration);
        services.AddHttpClient("bench")
            .AddHttpResilience()
            .ConfigurePrimaryHttpMessageHandler(() => new SlowOrigin(_originDelay));

        return services.BuildServiceProvider();
    }

    /// <summary>Holds every attempt long enough for the hedging timer to fire.</summary>
    private sealed class SlowOrigin : HttpMessageHandler
    {
        private readonly TimeSpan _delay;

        public SlowOrigin(TimeSpan delay) => _delay = delay;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
