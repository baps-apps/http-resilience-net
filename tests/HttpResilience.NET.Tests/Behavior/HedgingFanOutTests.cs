using HttpResilience.NET.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HttpResilience.NET.Tests.Behavior;

/// <summary>
/// A hedging delay below 10 ms sends nearly every request <c>MaxHedgedAttempts</c> + 1 times, so startup logs
/// one warning (event 21). It is valid configuration, so the host still starts.
/// </summary>
public class HedgingFanOutTests
{
    private const string Fragment = "starts the next attempt after";

    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData("00:00:00", "00:00:00")]
    [InlineData("00:00:00.009", "00:00:00.0090000")]
    public void AHedgingDelayBelowTenMilliseconds_StartsAndWarnsOnce(string delay, string logged)
    {
        string[] lines = StartAndCollect(
            Settings.Hedged()
                .Set("Hedging:Delay", delay)
                .Set("Hedging:MaxHedgedAttempts", "10"),
            hedged: true);

        string line = Assert.Single(lines);
        Assert.StartsWith("[Warning]", line, StringComparison.Ordinal);
        Assert.Contains("'orders'", line, StringComparison.Ordinal);
        Assert.Contains($"after {logged}", line, StringComparison.Ordinal);
        Assert.Contains("sent 11 times", line, StringComparison.Ordinal);
        Assert.Contains("HttpResilience:Clients:orders:Hedging:Delay", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// The threshold is exclusive: fails if it moves, or if the default delay starts warning.
    /// </summary>
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData("00:00:00.010")]
    [InlineData("00:00:02")]
    public void AHedgingDelayOfTenMillisecondsOrMore_DoesNotWarn(string delay)
    {
        string[] lines = StartAndCollect(Settings.Hedged().Set("Hedging:Delay", delay), hedged: true);

        Assert.Empty(lines);
    }

    /// <summary>
    /// A standard client never hedges, so a root <c>Hedging:Delay</c> it does not read is not reported.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public void AStandardClient_DoesNotWarn()
    {
        string[] lines = StartAndCollect(Settings.Enabled().Set("Hedging:Delay", "00:00:00"), hedged: false);

        Assert.Empty(lines);
    }

    private static string[] StartAndCollect(Settings settings, bool hedged)
    {
        var sink = new ListLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddProvider(sink);
        });

        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Build())
            .Build();
        services.AddHttpResilience(configuration);

        IHttpClientBuilder builder = services.AddHttpClient("orders");
        _ = hedged ? builder.AddHttpResilienceHedgingHandler() : builder.AddHttpResilienceHandler();

        using ServiceProvider provider = services.BuildServiceProvider();
        HostStartup.Run(provider);

        return [.. sink.Records.Where(record => record.Contains(Fragment, StringComparison.Ordinal))];
    }
}
