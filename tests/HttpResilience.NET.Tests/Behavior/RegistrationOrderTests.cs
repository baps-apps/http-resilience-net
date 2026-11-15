using System.Net;
using HttpResilience.NET.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Polly.RateLimiting;

namespace HttpResilience.NET.Tests.Behavior;

/// <summary>
/// Registration reads no configuration, so call order does not matter and configuration sources added after
/// registration are honoured, including the ones that decide which handlers a client has.
/// </summary>
/// <remarks>
/// Fails if any handler or option is decided from configuration while services are being registered.
/// </remarks>
public class RegistrationOrderTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task AClientRegisteredBeforeTheRootSection_ReadsItsConfiguration()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(Settings.Enabled().Build())
            .Build();

        var services = new ServiceCollection();
        var origin = new RecordingHandler();
        services.AddHttpClient("orders").AddHttpResilienceHandler().ConfigurePrimaryHttpMessageHandler(() => origin);
        services.AddHttpResilience(configuration);

        await using ServiceProvider provider = services.BuildServiceProvider();
        (await provider.GetRequiredService<IHttpClientFactory>().CreateClient("orders")
            .GetAsync("http://origin.test/x", TestContext.Current.CancellationToken)).Dispose();

        // The 500 was retried twice: the pipeline exists and read MaxRetries from the section.
        Assert.Equal(3, origin.Count);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task AConfigurationSourceAddedAfterRegistration_DecidesTheHandlers()
    {
        // ConfigurationManager is what WebApplicationBuilder.Configuration is: sources can be added at any time.
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["HttpResilience:Enabled"] = "false" });

        var services = new ServiceCollection();
        services.AddHttpResilience(configuration);
        var origin = new RecordingHandler(HttpStatusCode.OK);
        services.AddHttpClient("orders").AddHttpResilienceHandler().ConfigurePrimaryHttpMessageHandler(() => origin);

        // Added after every registration, as a remote configuration provider would be.
        configuration.AddInMemoryCollection(Settings.Enabled()
            .Set("RateLimiter:Enabled", "true")
            .Set("RateLimiter:PermitLimit", "1")
            .Set("RateLimiter:Window", "01:00:00")
            .Build());

        await using ServiceProvider provider = services.BuildServiceProvider();
        HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("orders");

        (await client.GetAsync("http://origin.test/x", TestContext.Current.CancellationToken)).Dispose();
        await Assert.ThrowsAsync<RateLimiterRejectedException>(() => client.GetAsync("http://origin.test/x", TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void ADisabledClient_CarriesNoResilienceHandlers()
    {
        var services = new ServiceCollection();
        services.AddHttpResilience(new ConfigurationBuilder()
            .AddInMemoryCollection(Settings.Hedged().Set("Enabled", "false").Build())
            .Build());

        var handlers = new List<string>();
        services.AddHttpClient("standard").AddHttpResilienceHandler()
            .ConfigureAdditionalHttpMessageHandlers((list, _) => handlers.AddRange(list.Select(h => h.GetType().Name)));
        services.AddHttpClient("hedged").AddHttpResilienceHedgingHandler()
            .ConfigureAdditionalHttpMessageHandlers((list, _) => handlers.AddRange(list.Select(h => h.GetType().Name)));

        using ServiceProvider provider = services.BuildServiceProvider();
        IHttpClientFactory factory = provider.GetRequiredService<IHttpClientFactory>();
        using HttpClient standard = factory.CreateClient("standard");
        using HttpClient hedged = factory.CreateClient("hedged");

        Assert.Empty(handlers);
        Assert.Equal(TimeSpan.FromSeconds(100), standard.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(100), hedged.Timeout);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void AConsumersOwnTimeout_OnADisabledClient_IsKept()
    {
        var services = new ServiceCollection();
        services.AddHttpResilience(new ConfigurationBuilder()
            .AddInMemoryCollection(Settings.Empty().Set("Enabled", "false").Build())
            .Build());
        services.AddHttpClient("orders")
            .AddHttpResilienceHandler()
            .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(7));

        using ServiceProvider provider = services.BuildServiceProvider();
        using HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("orders");

        Assert.Equal(TimeSpan.FromSeconds(7), client.Timeout);
    }
}
