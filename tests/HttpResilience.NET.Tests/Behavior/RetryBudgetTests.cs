using HttpResilience.NET.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HttpResilience.NET.Tests.Behavior;

/// <summary>
/// A retry schedule longer than <c>Timeout:Total</c> starts and logs one warning (event 14). A total timeout
/// used as a hard cap is valid configuration.
/// </summary>
public class RetryBudgetTests
{
    private const string Fragment = "retry schedule for client";

    [Fact(Timeout = TestTimeouts.Default)]
    public void AScheduleLongerThanTheTotal_StartsAndWarnsOnce()
    {
        // The platform defaults: 4 attempts x 10s + (1 + 2 + 4) x 2s backoff = 54s, against a 30s total. The
        // platform has no delay cap, so MaxDelay is raised past the longest backoff.
        string[] lines = StartAndCollect(Settings.Enabled()
            .Set("Retry:MaxDelay", "00:01:00")
            .Set("Retry:MaxRetries", "3")
            .Set("Retry:BaseDelay", "00:00:02")
            .Set("Retry:BackoffType", "Exponential")
            .Set("Timeout:Attempt", "00:00:10")
            .Set("Timeout:Total", "00:00:30"));

        string line = Assert.Single(lines);
        Assert.StartsWith("[Warning]", line, StringComparison.Ordinal);
        Assert.Contains("'orders'", line, StringComparison.Ordinal);
        Assert.Contains("HttpResilience:Clients:orders:Timeout:Total", line, StringComparison.Ordinal);
        Assert.Contains("00:00:54", line, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void AScheduleThatFits_DoesNotWarn_WhateverTheJitterSetting()
    {
        // 3 attempts x 1s + (1 + 2) x 2s = 9s, against a 10s total.
        Settings fits = Settings.Enabled()
            .Set("Timeout:Attempt", "00:00:01")
            .Set("Timeout:Total", "00:00:10")
            .Set("CircuitBreaker:SamplingDuration", "00:00:30")
            .Set("Retry:MaxRetries", "2")
            .Set("Retry:BaseDelay", "00:00:02")
            .Set("Retry:BackoffType", "Exponential");

        Assert.Empty(StartAndCollect(fits.Set("Retry:UseJitter", "true")));
        Assert.Empty(StartAndCollect(fits.Set("Retry:UseJitter", "false")));
    }

    /// <summary>
    /// The estimate cuts each backoff to <c>Retry:MaxDelay</c>, as the pipeline does.
    /// </summary>
    /// <remarks>
    /// 4 attempts x 1s + (2 + 4 + 8)s = 18s uncapped; with a 2s cap, 4s + (2 + 2 + 2)s = 10s, which fits. Fails if
    /// the estimate ignores the cap.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public void TheEstimate_HonoursMaxDelay()
    {
        Settings schedule = Settings.Enabled()
            .Set("Timeout:Attempt", "00:00:01")
            .Set("Timeout:Total", "00:00:10")
            .Set("CircuitBreaker:SamplingDuration", "00:00:30")
            .Set("Retry:MaxRetries", "3")
            .Set("Retry:BaseDelay", "00:00:02")
            .Set("Retry:BackoffType", "Exponential");

        Assert.Single(StartAndCollect(schedule.Set("Retry:MaxDelay", "00:01:00")));
        Assert.Empty(StartAndCollect(schedule.Set("Retry:MaxDelay", "00:00:02")));
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void AClientWithRetriesDisabled_DoesNotWarn()
    {
        string[] lines = StartAndCollect(Settings.Enabled()
            .Set("Retry:Enabled", "false")
            .Set("Timeout:Attempt", "00:00:10")
            .Set("Timeout:Total", "00:00:11"));

        Assert.Empty(lines);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void AHedgedClient_DoesNotWarn_BecauseItHasNoRetries()
    {
        string[] lines = StartAndCollect(
            Settings.Hedged()
                .Set("Retry:MaxRetries", "5")
                .Set("Timeout:Attempt", "00:00:05")
                .Set("Timeout:Total", "00:00:06")
                .Set("Hedging:Delay", "00:00:00.500"),
            hedged: true);

        Assert.Empty(lines);
    }

    private static string[] StartAndCollect(Settings settings, bool hedged = false)
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
