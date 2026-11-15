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
/// Every client that may send an unsafe method more than once logs a startup warning naming the key responsible.
/// </summary>
public class UnsafeMethodNoticeTests
{
    private static IConfigurationRoot Configuration(Settings settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings.Build()).Build();

    private static string[] NoticesFor(
        Settings settings,
        Action<HttpResilience.NET.HttpResilienceOptions>? configure = null)
    {
        var sink = new ListLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddProvider(sink);
        });
        services.AddHttpResilience(Configuration(settings));
        IHttpClientBuilder client = services.AddHttpClient("orders");
        _ = configure is null ? client.AddHttpResilienceHandler() : client.AddHttpResilienceHandler(configure);

        using ServiceProvider provider = services.BuildServiceProvider();
        HostStartup.Run(provider);

        return Repeatable(sink);
    }

    private static string[] Repeatable(ListLoggerProvider sink) =>
        [.. sink.Records.Where(record =>
            record.Contains("may send unsafe HTTP methods", StringComparison.Ordinal))];

    /// <summary>
    /// The startup warning names the key an operator can edit: an inherited list lives at the root, not in
    /// the client's section.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public void AnInheritedAllowList_IsReportedAgainstTheRootSection()
    {
        string[] notices = NoticesFor(Settings.Enabled()
            .Set("Retry:RetryableMethods:0", "GET")
            .ForClient("orders", "Timeout:Total", "00:00:30"));

        // A safe-only root list warns about nothing, so widen it in code the way a consumer would have to.
        Assert.Empty(notices);

        notices = NoticesFor(
            Settings.Enabled().ForClient("orders", "Timeout:Total", "00:00:30"),
            configure: options =>
            {
                options.Retry.RetryableMethods.Clear();
                options.Retry.RetryableMethods.Add("GET");
                options.Retry.RetryableMethods.Add("POST");
            });

        Assert.Single(notices);
        Assert.Contains("POST", notices[0], StringComparison.Ordinal);
        Assert.Contains("set in code rather than in configuration", notices[0], StringComparison.Ordinal);
        Assert.DoesNotContain(
            "HttpResilience:Clients:orders:Retry:RetryableMethods", notices[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// Every client that can repeat a mutating request says so once, at startup, before traffic.
    /// </summary>
    /// <remarks>
    /// Like <c>DisabledClient_WarnsAtStartup_BeforeAnyClientIsCreated</c>: nothing in the pipeline's telemetry
    /// distinguishes a retried POST from a retried GET. Fails if <c>UnsafeMethodNotice</c> is not registered
    /// or does not cover both mechanisms.
    /// </remarks>
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData("Retry:DisableForUnsafeHttpMethods", "false", false, "Retry:DisableForUnsafeHttpMethods")]
    [InlineData("Retry:RetryableMethods:0", "POST", false, "Retry:RetryableMethods")]
    [InlineData("Hedging:DisableForUnsafeHttpMethods", "false", true, "Hedging:DisableForUnsafeHttpMethods")]
    public void AClientThatCanRepeatAMutatingRequest_WarnsAtStartup(
        string key, string value, bool hedged, string expectedPath)
    {
        var sink = new ListLoggerProvider();
        Settings settings = (hedged ? Settings.Hedged() : Settings.Enabled()).ForClient("orders", key, value);

        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddProvider(sink);
        });
        services.AddHttpResilience(Configuration(settings));

        IHttpClientBuilder builder = services.AddHttpClient("orders");
        _ = hedged ? builder.AddHttpResilienceHedgingHandler() : builder.AddHttpResilienceHandler();

        using ServiceProvider provider = services.BuildServiceProvider();

        // Nothing yet: the notice is logged when the host starts.
        Assert.Empty(Repeatable(sink));

        HostStartup.Run(provider);

        string[] notices = Repeatable(sink);
        Assert.Single(notices);
        Assert.StartsWith("[Warning]", notices[0], StringComparison.Ordinal);
        Assert.Contains("orders", notices[0], StringComparison.Ordinal);
        Assert.Contains($"HttpResilience:Clients:orders:{expectedPath}", notices[0], StringComparison.Ordinal);
        Assert.Contains("idempotency key", notices[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// An allow-list of safe methods changes nothing, so it must not produce a line -- a warning on the
    /// harmless case is how a warning on the dangerous one stops being read.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public void AnAllowListOfOnlySafeMethods_WarnsAboutNothing()
    {
        string[] notices = NoticesFor(Settings.Enabled()
            .ForClient("orders", "Retry:RetryableMethods:0", "GET")
            .ForClient("orders", "Retry:RetryableMethods:1", "HEAD"));

        Assert.Empty(notices);
    }

    /// <summary>A client with no pipeline repeats nothing, whatever its flags say.</summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public void ADisabledClient_WarnsAboutNothing()
    {
        string[] notices = NoticesFor(Settings.Empty()
            .Set("Enabled", "false")
            .ForClient("orders", "Retry:DisableForUnsafeHttpMethods", "false"));

        Assert.Empty(notices);
    }

    /// <summary>And the default configuration is silent, which is what makes the warning mean something.</summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public void TheDefaults_WarnAboutNothing()
    {
        Assert.Empty(NoticesFor(Settings.Enabled()));
    }

    /// <summary>
    /// Turning the guard off <i>after</i> registration is still reported, because the notice reads the
    /// options rather than the registration that created it.
    /// </summary>
    /// <remarks>
    /// Fails if <c>UnsafeMethodNotice</c> goes back to being registered conditionally on the flag, which is
    /// the mistake the hedging guard already made once: a safety notice a later configuration change can
    /// delete by deleting its registration is not a notice.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public void DisablingTheGuardAfterRegistration_IsStillReported()
    {
        var sink = new ListLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddProvider(sink);
        });
        services.AddHttpResilience(Configuration(Settings.Enabled()));
        services.AddHttpClient("orders").AddHttpResilienceHandler();
        services.Configure<HttpResilience.NET.HttpResilienceOptions>(
            "orders", options => options.Retry.DisableForUnsafeHttpMethods = false);

        using ServiceProvider provider = services.BuildServiceProvider();
        HostStartup.Run(provider);

        Assert.Single(Repeatable(sink));
    }
}
