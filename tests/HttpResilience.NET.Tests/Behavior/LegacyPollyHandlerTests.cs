using HttpResilience.NET.Internal;
using HttpResilience.NET.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;

namespace HttpResilience.NET.Tests.Behavior;

/// <summary>
/// A Polly v7 handler (<c>Microsoft.Extensions.Http.Polly</c>) left on a client this package configures nests two
/// retry layers. The package cannot tell a v7 retry from a v7 timeout, so it reports rather than refuses.
/// </summary>
/// <remarks>
/// Asserts the damage as well as the notice, like <c>AConsumersOwnStandardHandler_NestsTwoPipelines_AndSaysSo</c>:
/// one failing GET makes <b>twelve</b> origin calls (a v7 retry of three around this package's three attempts).
/// Fails if event 20 stops being logged, for the client's own handler or for one from
/// <c>ConfigureHttpClientDefaults</c>, or if it is logged for a client without one.
/// </remarks>
public class LegacyPollyHandlerTests
{
    private static IConfigurationSection Configuration() =>
        new ConfigurationBuilder().AddInMemoryCollection(Settings.Enabled().Build()).Build()
            .GetSection("HttpResilience");

    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APollyV7RetryHandler_NestsWithThePipeline_AndSaysSo(bool fromDefaults)
    {
        var services = new ServiceCollection();
        var logs = new ListLoggerProvider();
        services.AddLogging(logging => logging.AddProvider(logs));
        if (fromDefaults)
        {
            services.ConfigureHttpClientDefaults(defaults =>
                defaults.AddTransientHttpErrorPolicy(policy => policy.RetryAsync(3)));
        }

        services.AddHttpResilience(Configuration());

        var origin = new RecordingHandler();
        IHttpClientBuilder builder = services.AddHttpClient("test").AddHttpResilienceHandler();
        if (!fromDefaults)
        {
            builder.AddTransientHttpErrorPolicy(policy => policy.RetryAsync(3));
        }

        builder.ConfigurePrimaryHttpMessageHandler(() => origin);

        await using ServiceProvider provider = services.BuildServiceProvider();
        using HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("test");
        (await client.GetAsync("http://origin.test/x", TestContext.Current.CancellationToken)).Dispose();

        Assert.Equal(12, origin.Count);
        Assert.Single(logs.WithEvent(HttpResilienceLogging.ConfigurationCategory, 20));
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task AClientWithoutOne_LogsNoEvent20()
    {
        var services = new ServiceCollection();
        var logs = new ListLoggerProvider();
        services.AddLogging(logging => logging.AddProvider(logs));
        services.AddHttpResilience(Configuration());

        var origin = new RecordingHandler();
        services.AddHttpClient("test").AddHttpResilienceHandler().ConfigurePrimaryHttpMessageHandler(() => origin);

        await using ServiceProvider provider = services.BuildServiceProvider();
        using HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("test");
        (await client.GetAsync("http://origin.test/x", TestContext.Current.CancellationToken)).Dispose();

        Assert.Equal(3, origin.Count);
        Assert.Empty(logs.WithEvent(HttpResilienceLogging.ConfigurationCategory, 20));
    }
}
