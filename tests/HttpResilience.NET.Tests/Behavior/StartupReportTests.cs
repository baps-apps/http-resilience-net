using HttpResilience.NET.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HttpResilience.NET.Tests.Behavior;

/// <summary>
/// The startup notices (events 6, 10, 11, 14, 15, 21) are logged once, at host start, from each client's final
/// options -- after every consumer <c>PostConfigure</c> and after validation.
/// </summary>
public class StartupReportTests
{
    private const string Unsafe = "may send unsafe HTTP methods more than once";
    private const string Disabled = "is disabled for client";
    private const string Summary = "pipeline:";

    private static (ServiceCollection Services, ListLoggerProvider Sink) Services(Settings settings)
    {
        var sink = new ListLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddProvider(sink);
        });
        services.AddHttpResilience(new ConfigurationBuilder().AddInMemoryCollection(settings.Build()).Build());
        return (services, sink);
    }

    private static string[] Matching(ListLoggerProvider sink, string text) =>
        [.. sink.Records.Where(record => record.Contains(text, StringComparison.Ordinal))];

    /// <summary>
    /// A consumer's <c>PostConfigure</c> registered after the client is reflected in event 10.
    /// </summary>
    /// <remarks>
    /// Fails if the notice is evaluated inside the options pipeline: it would run before this
    /// <c>PostConfigure</c> and miss the POST.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public void AConsumersPostConfigure_ThatAllowsPost_IsReported()
    {
        (ServiceCollection services, ListLoggerProvider sink) = Services(Settings.Enabled());
        services.AddHttpClient("orders").AddHttpResilienceHandler();
        services.PostConfigure<HttpResilienceOptions>("orders", options => options.Retry.RetryableMethods.Add("POST"));

        using ServiceProvider provider = services.BuildServiceProvider();
        HostStartup.Run(provider);

        Assert.Contains("POST may be retried", Assert.Single(Matching(sink, Unsafe)), StringComparison.Ordinal);
    }

    /// <summary>
    /// A consumer's <c>PostConfigure</c> that re-enables a client disabled in configuration produces no false
    /// "disabled" warning, and the client gets its summary instead.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public void AConsumersPostConfigure_ThatEnablesTheClient_IsNotReportedDisabled()
    {
        (ServiceCollection services, ListLoggerProvider sink) = Services(
            Settings.Enabled().ForClient("orders", "Enabled", "false"));
        services.AddHttpClient("orders").AddHttpResilienceHandler();
        services.PostConfigure<HttpResilienceOptions>("orders", options => options.Enabled = true);

        using ServiceProvider provider = services.BuildServiceProvider();
        HostStartup.Run(provider);

        Assert.Empty(Matching(sink, Disabled));
        Assert.Single(Matching(sink, Summary));
    }

    /// <summary>
    /// Invalid configuration fails startup with nothing logged about the client first.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public void InvalidConfiguration_FailsStartup_WithoutNotices()
    {
        (ServiceCollection services, ListLoggerProvider sink) = Services(Settings.Enabled()
            .ForClient("orders", "Retry:RetryableMethods:0", "POST")
            .ForClient("orders", "Retry:MaxRetries", "50"));
        services.AddHttpClient("orders").AddHttpResilienceHandler();

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.ThrowsAny<Exception>(() => HostStartup.Run(provider));
        Assert.Empty(Matching(sink, "HttpResilience.NET.Configuration"));
    }

    /// <summary>
    /// Building options per scope, as <c>IOptionsSnapshot</c> does per request, logs nothing.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public void OptionsBuiltPerScope_LogNoNotices()
    {
        (ServiceCollection services, ListLoggerProvider sink) = Services(Settings.Enabled()
            .ForClient("orders", "Retry:RetryableMethods:0", "POST"));
        services.AddHttpClient("orders").AddHttpResilienceHandler();

        using ServiceProvider provider = services.BuildServiceProvider();
        for (int i = 0; i < 20; i++)
        {
            using IServiceScope scope = provider.CreateScope();
            _ = scope.ServiceProvider.GetRequiredService<IOptionsSnapshot<HttpResilienceOptions>>().Get("orders");
        }

        Assert.Empty(Matching(sink, "HttpResilience.NET.Configuration"));
    }

    /// <summary>
    /// Opting out of client creation at startup does not silence the security notice.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public void OptingOutOfTheStartupProbe_StillReportsUnsafeMethods()
    {
        (ServiceCollection services, ListLoggerProvider sink) = Services(Settings.Enabled()
            .Set("ValidateClientsOnStart", "false")
            .ForClient("orders", "Retry:RetryableMethods:0", "POST"));
        services.AddHttpClient("orders").AddHttpResilienceHandler();

        using ServiceProvider provider = services.BuildServiceProvider();
        HostStartup.Run(provider);

        Assert.Single(Matching(sink, Unsafe));
    }

    /// <summary>
    /// Event 15 states a standard client's effective settings in one line, after its own overrides.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public void AStandardClient_GetsASummaryOfItsEffectiveSettings()
    {
        (ServiceCollection services, ListLoggerProvider sink) = Services(Settings.Enabled()
            .ForClient("orders", "Timeout:Total", "00:00:10")
            .ForClient("orders", "Timeout:Attempt", "00:00:03")
            .ForClient("orders", "Retry:RetryableMethods:0", "GET")
            .ForClient("orders", "Retry:RetryableMethods:1", "post"));
        services.AddHttpClient("orders").AddHttpResilienceHandler();

        using ServiceProvider provider = services.BuildServiceProvider();
        HostStartup.Run(provider);

        string summary = Assert.Single(Matching(sink, Summary));
        Assert.StartsWith("[Information] HttpResilience.NET.Configuration:", summary, StringComparison.Ordinal);
        Assert.Contains("client 'orders' uses the standard pipeline", summary, StringComparison.Ordinal);
        Assert.Contains("timeout total 00:00:10, attempt 00:00:03, client 00:00:40", summary, StringComparison.Ordinal);
        Assert.Contains("retry 2 (Constant from 00:00:00, at most 00:00:05) for GET, POST", summary, StringComparison.Ordinal);
        Assert.Contains("breaker opens at 50% failures of at least 1000 attempts", summary, StringComparison.Ordinal);
        Assert.Contains("one pipeline", summary, StringComparison.Ordinal);
        Assert.Contains("concurrency backstop 1000", summary, StringComparison.Ordinal);
        Assert.Contains("rate limit off", summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// A hedged client's summary names hedging and its allowed authorities, not retries.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public void AHedgedClient_SummaryNamesHedging()
    {
        (ServiceCollection services, ListLoggerProvider sink) = Services(Settings.Hedged());
        services.AddHttpClient("search").AddHttpResilienceHedgingHandler();

        using ServiceProvider provider = services.BuildServiceProvider();
        HostStartup.Run(provider);

        string summary = Assert.Single(Matching(sink, Summary));
        Assert.Contains("uses the hedging pipeline", summary, StringComparison.Ordinal);
        Assert.Contains("for GET, HEAD, OPTIONS, TRACE", summary, StringComparison.Ordinal);
        Assert.Contains("1 allowed authority;", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("retry", summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// Clients are reported in registration order, a disabled one with event 6 and no summary.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public void Clients_AreReportedInRegistrationOrder()
    {
        (ServiceCollection services, ListLoggerProvider sink) = Services(
            Settings.Enabled().ForClient("b", "Enabled", "false"));
        services.AddHttpClient("c").AddHttpResilienceHandler();
        services.AddHttpClient("a").AddHttpResilienceHandler();
        services.AddHttpClient("b").AddHttpResilienceHandler();

        using ServiceProvider provider = services.BuildServiceProvider();
        HostStartup.Run(provider);

        string[] lines = [.. sink.Records.Where(r => r.Contains(Summary, StringComparison.Ordinal) ||
                                                    r.Contains(Disabled, StringComparison.Ordinal))];
        Assert.Equal(3, lines.Length);
        Assert.Contains("'c'", lines[0], StringComparison.Ordinal);
        Assert.Contains("'a'", lines[1], StringComparison.Ordinal);
        Assert.Contains("'b'", lines[2], StringComparison.Ordinal);
        Assert.Contains(Disabled, lines[2], StringComparison.Ordinal);
    }
}
