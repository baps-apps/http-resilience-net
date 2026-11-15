using System.Diagnostics.Metrics;
using System.Net.Http.Headers;
using System.Net;
using System.Threading.RateLimiting;
using BclConcurrencyLimiterOptions = System.Threading.RateLimiting.ConcurrencyLimiterOptions;
using HttpResilience.NET.Internal;
using HttpResilience.NET.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.RateLimiting;
using Polly.Timeout;

namespace HttpResilience.NET.Tests.Behavior;

/// <summary>
/// Options changed after registration reach the running pipeline.
/// </summary>
public class LiveOptionsTests
{
    private static IConfigurationRoot Configuration(Settings settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings.Build()).Build();

    /// <summary>
    /// What the options report and what the pipeline runs are the same number, whoever set it.
    /// </summary>
    /// <remarks>
    /// The pipeline reads <see cref="IOptionsMonitor{TOptions}"/> when built, after every <c>Configure</c> and
    /// <c>PostConfigure</c>. Fails if it uses a captured snapshot: options would report one attempt while the
    /// origin saw three.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task PostRegistrationConfigure_ReachesTheRunningPipeline()
    {
        var services = new ServiceCollection();
        services.AddHttpResilience(Configuration(Settings.Enabled().Set("Retry:MaxRetries", "2")));

        var origin = new RecordingHandler();
        services.AddHttpClient("t").AddHttpResilienceHandler().ConfigurePrimaryHttpMessageHandler(() => origin);
        services.Configure<HttpResilienceOptions>("t", options => options.Retry.MaxRetries = 1);

        await using ServiceProvider provider = services.BuildServiceProvider();
        (await provider.GetRequiredService<IHttpClientFactory>().CreateClient("t")
            .GetAsync("http://origin.test/x", TestContext.Current.CancellationToken)).Dispose();

        HttpResilienceOptions effective =
            provider.GetRequiredService<IOptionsMonitor<HttpResilienceOptions>>().Get("t");

        Assert.Equal(1, effective.Retry.MaxRetries);
        Assert.Equal(2, origin.Count);
    }

    /// <summary>
    /// The supported way to adjust values in code, and the one that does reach the pipeline.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ConfigureParameter_ReachesBothThePipelineAndTheOptions()
    {
        var services = new ServiceCollection();
        services.AddHttpResilience(Configuration(Settings.Enabled().Set("Retry:MaxRetries", "2")));

        var origin = new RecordingHandler();
        services.AddHttpClient("t")
            .AddHttpResilienceHandler(configure: options => options.Retry.MaxRetries = 1)
            .ConfigurePrimaryHttpMessageHandler(() => origin);

        await using ServiceProvider provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IHttpClientFactory>().CreateClient("t").GetAsync("http://origin.test/x", TestContext.Current.CancellationToken);

        Assert.Equal(1, provider.GetRequiredService<IOptionsMonitor<HttpResilienceOptions>>()
            .Get("t").Retry.MaxRetries);
        Assert.Equal(2, origin.Count);
    }
}
