using System.Net;
using HttpResilience.NET.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly.RateLimiting;

namespace HttpResilience.NET.Tests.Behavior;

/// <summary>
/// Every limiter this package configures reports its own rejections.
/// </summary>
/// <remarks>
/// All limiters surface the same <see cref="RateLimiterRejectedException"/> on the same instrument, so the log
/// line naming the control and key is what tells them apart. Each test fails if its <c>OnRejected</c>
/// assignment in <c>HttpClientBuilderExtensions</c> or <c>StandardPipelineConfigurator</c> is removed.
/// </remarks>
public class LimiterRejectionTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task RateLimiterRejection_NamesTheControlAndTheKeyToChange()
    {
        IReadOnlyList<string> records = await SaturateAsync(
            Settings.Enabled()
                .Set("RateLimiter:Enabled", "true")
                .Set("RateLimiter:PermitLimit", "1")
                .Set("RateLimiter:Window", "01:00:00"));

        string notice = Single(records, "rate limiter rejected");

        Assert.Contains("orders", notice, StringComparison.Ordinal);
        Assert.Contains("RateLimiter:PermitLimit", notice, StringComparison.Ordinal);
        Assert.Contains("[Warning]", notice, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ConcurrencyLimiterRejection_NamesTheControlAndTheKeyToChange()
    {
        IReadOnlyList<string> records = await SaturateAsync(
            Settings.Enabled()
                .Set("ConcurrencyLimiter:Enabled", "true")
                .Set("ConcurrencyLimiter:Limit", "1")
                .Set("ConcurrencyLimiter:QueueLimit", "0"));

        string notice = Single(records, "concurrency limiter rejected");

        Assert.Contains("orders", notice, StringComparison.Ordinal);
        Assert.Contains("ConcurrencyLimiter:Limit", notice, StringComparison.Ordinal);
        Assert.Contains("[Warning]", notice, StringComparison.Ordinal);
    }

    /// <summary>
    /// The hedging pipeline carries its rate limiter as a separate outer handler, so it is a second
    /// assignment that can be forgotten independently of the standard one.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task HedgedRateLimiterRejection_NamesTheControlAndTheKeyToChange()
    {
        IReadOnlyList<string> records = await SaturateAsync(
            Settings.Hedged()
                .Set("Hedging:Delay", "00:00:10")
                .Set("RateLimiter:Enabled", "true")
                .Set("RateLimiter:PermitLimit", "1")
                .Set("RateLimiter:Window", "01:00:00")
                .ForClient("orders", "PipelineSelection:Authorities:0", "http://origin.test"),
            hedged: true);

        string notice = Single(records, "rate limiter rejected");

        Assert.Contains("orders", notice, StringComparison.Ordinal);
        Assert.Contains("RateLimiter:PermitLimit", notice, StringComparison.Ordinal);
    }

    /// <summary>
    /// A hedged client's backstop is the platform's per-authority endpoint limiter, a third place a rejection
    /// callback can be missing.
    /// </summary>
    /// <remarks>
    /// Fails if <c>HedgingPipelineConfigurator</c> stops assigning the endpoint limiter's <c>OnRejected</c>.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task HedgedBackstopRejection_NamesTheControlAndTheKeyToChange()
    {
        IReadOnlyList<string> records = await SaturateAsync(
            Settings.Hedged()
                .Set("Hedging:Delay", "00:00:10")
                .Set("ConcurrencyLimiter:Backstop", "1")
                .ForClient("orders", "PipelineSelection:Authorities:0", "http://origin.test"),
            hedged: true);

        string notice = Single(records, "concurrency backstop rejected");

        Assert.Contains("orders", notice, StringComparison.Ordinal);
        Assert.Contains("HttpResilience:Clients:orders:ConcurrencyLimiter:Backstop", notice, StringComparison.Ordinal);
        Assert.Contains("[Warning]", notice, StringComparison.Ordinal);
    }

    /// <summary>
    /// A burst of rejections is the overload case; one log entry per rejection would add to it.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ABurstOfRejections_LogsOneEntry()
    {
        IReadOnlyList<string> records = await SaturateAsync(
            Settings.Enabled()
                .Set("ConcurrencyLimiter:Enabled", "true")
                .Set("ConcurrencyLimiter:Limit", "1")
                .Set("ConcurrencyLimiter:QueueLimit", "0"),
            rejections: 50);

        string notice = Single(records, "concurrency limiter rejected");
        Assert.Contains("0 more since the last entry", notice, StringComparison.Ordinal);
        Assert.Single(records, r => r.Contains("concurrency limiter rejected", StringComparison.OrdinalIgnoreCase));
    }

    private static string Single(IReadOnlyList<string> records, string fragment)
    {
        string[] matches = [.. records.Where(r => r.Contains(fragment, StringComparison.OrdinalIgnoreCase))];
        Assert.NotEmpty(matches);
        return matches[0];
    }

    /// <summary>
    /// Holds one request at the origin, then sends a second that the limiter must refuse, and returns what
    /// an operator would have seen in the log.
    /// </summary>
    private static async Task<IReadOnlyList<string>> SaturateAsync(
        Settings settings,
        bool hedged = false,
        int rejections = 1)
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

        var gate = new TaskCompletionSource();
        var origin = new RecordingHandler(async (request, _, cancellationToken) =>
        {
            await gate.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };
        });

        IHttpClientBuilder builder = services.AddHttpClient("orders");
        _ = hedged ? builder.AddHttpResilienceHedgingHandler() : builder.AddHttpResilienceHandler();
        builder.ConfigurePrimaryHttpMessageHandler(() => origin);

        await using ServiceProvider provider = services.BuildServiceProvider();
        HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("orders");

        Task<HttpResponseMessage> held = client.GetAsync("http://origin.test/x");
        await origin.WaitForCountAsync(1);

        for (int i = 0; i < rejections; i++)
        {
            await Assert.ThrowsAsync<RateLimiterRejectedException>(() => client.GetAsync("http://origin.test/x"));
        }

        gate.SetResult();
        (await held).Dispose();

        return sink.Records;
    }
}
