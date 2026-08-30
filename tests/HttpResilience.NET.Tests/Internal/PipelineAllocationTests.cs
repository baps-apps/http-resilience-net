using System.Net;
using HttpResilience.NET.Options;
using HttpResilience.NET.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;

namespace HttpResilience.NET.Tests.Internal;

/// <summary>
/// What this package allocates per request on top of the platform handler it configures, as a number a test
/// can fail on rather than a claim in a document.
/// </summary>
/// <remarks>
/// The documents said "identical allocation" for one revision longer than it was true. The fourth review made
/// <see cref="HttpClient.Timeout"/> finite so that a trickled response body could not hold a connection open
/// forever, and a finite timeout is what makes <see cref="HttpClient"/> build a
/// <see cref="CancellationTokenSource"/> per request. That is a real cost, worth paying, and worth knowing
/// about -- so it is pinned here, where changing it fails a test instead of quietly ageing a benchmark file.
/// <para>
/// Measured with <see cref="GC.GetAllocatedBytesForCurrentThread"/> rather than BenchmarkDotNet, so it runs in
/// the ordinary test suite. The bound is deliberately loose: this is a regression guard against a new
/// per-request allocation, not a micro-benchmark.
/// </para>
/// </remarks>
/// <summary>
/// Runs alone. <see cref="GC.GetAllocatedBytesForCurrentThread"/> is per thread, and an <c>await</c> resumes
/// on a pool thread that a concurrently running test may also be using -- so in a parallel suite the counter
/// picks up someone else's allocations. Measured in isolation the two numbers below are exact and repeatable
/// (304 and 0, six runs out of six); measured alongside everything else they were flaky about one run in five.
/// Serialising the collection is the fix, rather than widening the bounds until the flake hides.
/// </summary>
[CollectionDefinition(nameof(PipelineAllocationTests), DisableParallelization = true)]
[Collection(nameof(PipelineAllocationTests))]
public class PipelineAllocationTests
{
    private const int Iterations = 200;

    /// <summary>
    /// Production change that would make this fail: allocating anything new per request in the standard path.
    /// The headroom is one <c>CancellationTokenSource</c> and its registration, and no more.
    /// </summary>
    /// <remarks>
    /// Release only, and skipped rather than widened in Debug. The ceiling is a statement about what ships:
    /// Debug codegen adds display classes and unelided async state machines to the two arms unequally, which
    /// measured 400 B against a 384 B ceiling -- a number that says nothing about the shipped assembly and
    /// everything about the JIT's debug mode. Widening the ceiling to cover both would raise it past the
    /// second object it exists to exclude, so the assertion is made where it means something and the CI
    /// pipeline runs both configurations. The companion test below is a *difference between two
    /// package-configured clients*, so Debug overhead cancels and it runs everywhere.
    /// </remarks>
    [ReleaseOnlyFact]
    public async Task StandardPipeline_CostsOneCancellationTokenSourceOverThePlatformHandler()
    {
        long platform = await MeasureAsync(MicrosoftOnly);
        long package = await MeasureAsync(WithHttpResilience);

        long overhead = (package - platform) / Iterations;

        // Exactly 304 B -- one CancellationTokenSource, its timer and its registration -- agreeing with the
        // BenchmarkDotNet delta (1,336 B against the platform handler's 1,032 B) to the byte. The ceiling
        // leaves headroom for that one object and nothing like a second.
        Assert.InRange(overhead, 0, 384);
    }

    /// <summary>
    /// Per-authority selection is the one piece of per-request work this package does that is entirely its
    /// own, and <c>AuthorityIndex</c> exists so that it costs nothing. Pinned separately from the
    /// allocation-free unit test on the selector, because a caller could still allocate around it.
    /// </summary>
    [Fact]
    public async Task PerAuthoritySelection_AddsNoAllocationOverASinglePipeline()
    {
        long single = await MeasureAsync(WithHttpResilience);
        long perAuthority = await MeasureAsync(services => WithHttpResilience(
            services,
            settings => settings
                .Set("PipelineSelection:Mode", "ByAuthority")
                .Set("PipelineSelection:Authorities:0", "http://origin.test")));

        long overhead = (perAuthority - single) / Iterations;

        Assert.InRange(overhead, -64, 64);
    }

    private static void MicrosoftOnly(IServiceCollection services) =>
        services.AddHttpClient("bench")
            .AddStandardResilienceHandler()
            .Configure(options =>
            {
                options.Retry.MaxRetryAttempts = 2;
                options.CircuitBreaker.MinimumThroughput = 1000;
            });

    private static void WithHttpResilience(IServiceCollection services) =>
        WithHttpResilience(services, static settings => settings);

    private static void WithHttpResilience(IServiceCollection services, Func<Settings, Settings> configure)
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configure(Settings.Enabled()).Build())
            .Build();

        services.AddHttpResilience(configuration);
        services.AddHttpClient("bench").AddHttpResilience(string.Empty);
    }

    private static async Task<long> MeasureAsync(Action<IServiceCollection> register)
    {
        var services = new ServiceCollection();
        register(services);
        services.AddHttpClient("bench")
            .ConfigurePrimaryHttpMessageHandler(() => new RecordingHandler(HttpStatusCode.OK));

        await using ServiceProvider provider = services.BuildServiceProvider();
        HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("bench");

        // Warm every lazily built pipeline, handler chain and options instance before measuring.
        for (int i = 0; i < 20; i++)
        {
            (await client.GetAsync("http://origin.test/x")).Dispose();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Iterations; i++)
        {
            (await client.GetAsync("http://origin.test/x")).Dispose();
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
