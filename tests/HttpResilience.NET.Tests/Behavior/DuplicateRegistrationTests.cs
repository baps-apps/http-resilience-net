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
/// Resilience can be added to a client once; a second call fails instead of nesting pipelines.
/// </summary>
public class DuplicateRegistrationTests
{
    private static IConfigurationRoot Configuration(Settings settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings.Build()).Build();

    /// <summary>
    /// Two pipelines nest rather than merge, so retries multiply: three attempts each became nine origin
    /// calls, with nothing thrown and nothing logged.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public void RegisteringResilienceTwice_FailsWithAnActionableMessage()
    {
        var services = new ServiceCollection();
        services.AddHttpResilience(Configuration(Settings.Enabled()));

        IHttpClientBuilder builder = services.AddHttpClient("orders").AddHttpResilienceHandler();

        InvalidOperationException exception =
            Assert.Throws<InvalidOperationException>(() => builder.AddHttpResilienceHandler());

        Assert.Contains("already configured", exception.Message, StringComparison.Ordinal);
        Assert.Contains("orders", exception.Message, StringComparison.Ordinal);
        Assert.Contains("AddResilienceHandler", exception.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void RegisteringResilienceTwice_AcrossSeparateBuilders_AlsoFails()
    {
        var services = new ServiceCollection();
        services.AddHttpResilience(Configuration(Settings.Enabled()));

        // The realistic shape: a shared registration extension adds resilience, and the application adds it
        // again on its own builder for the same client name.
        services.AddHttpClient("orders").AddHttpResilienceHandler();

        Assert.Throws<InvalidOperationException>(() => services.AddHttpClient("orders").AddHttpResilienceHandler());
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ClientsWithDistinctNames_AreUnaffectedByTheGuard()
    {
        var services = new ServiceCollection();
        services.AddHttpResilience(Configuration(Settings.Enabled()));

        var origin = new RecordingHandler();
        services.AddHttpClient("a").AddHttpResilienceHandler().ConfigurePrimaryHttpMessageHandler(() => origin);
        services.AddHttpClient("b").AddHttpResilienceHandler().ConfigurePrimaryHttpMessageHandler(() => origin);

        await using ServiceProvider provider = services.BuildServiceProvider();
        IHttpClientFactory factory = provider.GetRequiredService<IHttpClientFactory>();

        await factory.CreateClient("a").GetAsync("http://origin.test/x", TestContext.Current.CancellationToken);
        Assert.Equal(3, origin.Count);
    }
}
