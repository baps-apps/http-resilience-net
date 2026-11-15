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
/// A registered client that is switched off says so at startup, naming the key that did it.
/// </summary>
public class DisabledClientTests
{
    private static IConfigurationRoot Configuration(Settings settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings.Build()).Build();

    private static string[] Notices(ListLoggerProvider sink) =>
        [.. sink.Records.Where(record => record.Contains("is disabled for client", StringComparison.Ordinal))];

    /// <summary>
    /// A client with no resilience must be visible at startup, at a level a deployment check sees.
    /// </summary>
    /// <remarks>
    /// Fails if the notice hangs off <c>ConfigureHttpClient</c>: no client is created here, so a notice that
    /// waits for one never fires.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public void DisabledClient_WarnsAtStartup_BeforeAnyClientIsCreated()
    {
        var sink = new ListLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddProvider(sink);
        });
        services.AddHttpResilience(Configuration(Settings.Empty().ForClient("orders", "Enabled", "false")));
        services.AddHttpClient("orders").AddHttpResilienceHandler();

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Empty(Notices(sink));

        // Exactly what the host runs before it accepts traffic. No client is ever created.
        HostStartup.Run(provider);

        string[] notices = Notices(sink);
        Assert.Single(notices);
        Assert.StartsWith("[Warning]", notices[0], StringComparison.Ordinal);
        Assert.Contains("orders", notices[0], StringComparison.Ordinal);
        Assert.Contains("HttpResilience:Clients:orders:Enabled", notices[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// Once per client at startup, however many times its options are read or its clients created afterwards.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task DisabledClient_SaysSoOnce()
    {
        var sink = new ListLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddProvider(sink);
        });
        services.AddHttpResilience(Configuration(Settings.Empty().Set("Enabled", "false")));

        var origin = new RecordingHandler();
        services.AddHttpClient("orders").AddHttpResilienceHandler().ConfigurePrimaryHttpMessageHandler(() => origin);

        await using ServiceProvider provider = services.BuildServiceProvider();
        HostStartup.Run(provider);

        IHttpClientFactory factory = provider.GetRequiredService<IHttpClientFactory>();
        await factory.CreateClient("orders").GetAsync("http://origin.test/x", TestContext.Current.CancellationToken);
        await factory.CreateClient("orders").GetAsync("http://origin.test/x", TestContext.Current.CancellationToken);
        provider.GetRequiredService<IOptionsMonitor<HttpResilienceOptions>>().Get("orders");

        Assert.Single(Notices(sink));
    }

    /// <summary>
    /// A container with no logging registered must not fail because the notice had nowhere to write.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public void DisabledClient_WithoutLogging_StillStarts()
    {
        var services = new ServiceCollection();
        services.AddHttpResilience(Configuration(Settings.Empty().Set("Enabled", "false")));
        services.AddHttpClient("orders").AddHttpResilienceHandler();

        using ServiceProvider provider = services.BuildServiceProvider();

        HostStartup.Run(provider);
    }

    /// <summary>
    /// The root switch turns every client off, even one whose section and code say otherwise, and the
    /// notice names the root key as the cause.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task TheRootKillSwitch_OverridesTheClientSectionAndCode()
    {
        var sink = new ListLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddProvider(sink);
        });
        services.AddHttpResilience(Configuration(Settings.Enabled()
            .Set("Enabled", "false")
            .ForClient("orders", "Enabled", "true")));

        var origin = new RecordingHandler();
        services.AddHttpClient("orders")
            .AddHttpResilienceHandler(options => options.Enabled = true)
            .ConfigurePrimaryHttpMessageHandler(() => origin);

        await using ServiceProvider provider = services.BuildServiceProvider();
        HostStartup.Run(provider);

        await provider.GetRequiredService<IHttpClientFactory>().CreateClient("orders").GetAsync("http://origin.test/x", TestContext.Current.CancellationToken);

        Assert.Equal(1, origin.Count);
        Assert.Contains("'HttpResilience:Enabled'", Assert.Single(Notices(sink)), StringComparison.Ordinal);
    }

    /// <summary>
    /// Disabled-client notices are logged under the configuration category, so they can be filtered apart
    /// from per-request pipeline events.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public void TheNotice_IsLoggedUnderTheConfigurationCategory()
    {
        var sink = new ListLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddFilter("HttpResilience.NET.Configuration", LogLevel.None);
            logging.AddProvider(sink);
        });
        services.AddHttpResilience(Configuration(Settings.Empty().Set("Enabled", "false")));
        services.AddHttpClient("orders").AddHttpResilienceHandler();

        using ServiceProvider provider = services.BuildServiceProvider();
        HostStartup.Run(provider);

        Assert.Empty(Notices(sink));
    }
}
