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
/// Only the concurrency backstop used to. That is the inverted priority: the backstop is the control nobody
/// configured, while the rate limiter and the client's concurrency cap are the ones an operator chose,
/// alerts on, and has to re-size during an incident. All of them surface as the same
/// <see cref="RateLimiterRejectedException"/> on the same instrument, so without a line naming the control
/// and the key there is nothing to tell them apart.
/// <para>
/// Production change that would make each of these fail: removing the corresponding <c>OnRejected</c>
/// assignment in <c>HttpClientBuilderExtensions</c> or <c>StandardPipelineConfigurator</c>.
/// </para>
/// </remarks>
public class LimiterRejectionTests
{
    [Fact]
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

    [Fact]
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
    [Fact]
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
    private static async Task<IReadOnlyList<string>> SaturateAsync(Settings settings, bool hedged = false)
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
        _ = hedged ? builder.AddHedgedHttpResilience() : builder.AddHttpResilience();
        builder.ConfigurePrimaryHttpMessageHandler(() => origin);

        await using ServiceProvider provider = services.BuildServiceProvider();
        HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("orders");

        Task<HttpResponseMessage> held = client.GetAsync("http://origin.test/x");
        while (origin.Count == 0)
        {
            await Task.Delay(10);
        }

        await Assert.ThrowsAsync<RateLimiterRejectedException>(() => client.GetAsync("http://origin.test/x"));
        gate.SetResult();
        (await held).Dispose();

        return sink.Records;
    }
}
