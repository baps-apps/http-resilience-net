using System.Net;
using HttpResilience.NET.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;

namespace HttpResilience.NET.Tests.Internal;

/// <summary>
/// What this package allocates per request on top of the platform handler it configures.
/// </summary>
/// <remarks>
/// The finite <see cref="HttpClient.Timeout"/> makes <see cref="HttpClient"/> build one
/// <see cref="CancellationTokenSource"/> per request; that cost is pinned here. Measured with
/// <see cref="GC.GetAllocatedBytesForCurrentThread"/> so it runs in the ordinary suite. The bound is a guard
/// against new per-request allocations, not a micro-benchmark.
/// </remarks>
/// <summary>
/// Runs alone: <see cref="GC.GetAllocatedBytesForCurrentThread"/> is per thread, and an <c>await</c> can
/// resume on a pool thread another test is using.
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
    /// Release only: Debug codegen adds display classes and async state machines unequally to the two arms.
    /// The companion test below compares two package-configured clients, so Debug overhead cancels.
    /// </remarks>
    [ReleaseOnlyFact]
    public async Task StandardPipeline_CostsOneCancellationTokenSourceOverThePlatformHandler()
    {
        long platform = await MeasureAsync(MicrosoftOnly);
        long package = await MeasureAsync(WithHttpResilience);

        long overhead = (package - platform) / Iterations;

        // Exactly 304 B (one CancellationTokenSource, its timer and registration), matching the
        // BenchmarkDotNet delta. The ceiling leaves room for that one object only.
        Assert.InRange(overhead, 0, 384);
    }

    /// <summary>
    /// Per-authority selection is this package's own per-request work, and must allocate nothing. Pinned
    /// separately from the selector's unit test because a caller could still allocate around it.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
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

    /// <summary>
    /// <c>CreateClient</c> runs every <c>ConfigureHttpClient</c> action on each call, and a typed client is often
    /// created per request, so this package must add nothing to it beyond applying <c>Timeout:Client</c>.
    /// </summary>
    /// <remarks>
    /// Fails if per-call state comes back into the client actions, such as snapshotting
    /// <see cref="HttpClient.Timeout"/> per handler group (171 B per call when it was there). Covers a disabled
    /// client too, whose handler groups are all inactive. Synchronous, so the per-thread counter is exact.
    /// </remarks>
    [ReleaseOnlyFact]
    public async Task CreateClient_AllocatesNothingOverTheBareFactory()
    {
        long bare = await MeasureCreateClientAsync(static _ => { });
        long enabled = await MeasureCreateClientAsync(services => WithHttpResilience(services));
        long disabled = await MeasureCreateClientAsync(services =>
            WithHttpResilience(services, static settings => settings.Set("Enabled", "false")));

        Assert.InRange((enabled - bare) / Iterations, 0, 8);
        Assert.InRange((disabled - bare) / Iterations, 0, 8);
    }

    private static async Task<long> MeasureCreateClientAsync(Action<IServiceCollection> register)
    {
        var services = new ServiceCollection();
        register(services);
        services.AddHttpClient("bench");

        await using ServiceProvider provider = services.BuildServiceProvider();
        IHttpClientFactory factory = provider.GetRequiredService<IHttpClientFactory>();

        // Builds the handler chain and every options instance before measuring.
        for (int i = 0; i < 20; i++)
        {
            factory.CreateClient("bench").Dispose();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Iterations; i++)
        {
            factory.CreateClient("bench").Dispose();
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
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
        services.AddHttpClient("bench").AddHttpResilienceHandler();
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
