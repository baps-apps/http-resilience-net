using System.Net;
using HttpResilience.NET.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly.RateLimiting;

namespace HttpResilience.NET.Tests.Behavior;

/// <summary>
/// The standard resilience handler always carries a limiter, whether or not this package configures one.
/// Left implicit it is a scaling cliff nobody can see: above its permit count requests are rejected with
/// <see cref="RateLimiterRejectedException"/>, naming a feature the operator never enabled.
/// </summary>
/// <remarks>
/// These tests drive the limiter to genuine saturation. A test that merely asserts the configured value
/// would not have caught the two defects that motivated the setting: a cap the schema never mentioned, and
/// a <c>ConcurrencyLimiter:Limit</c> above it being silently clamped with the excess rejected rather than
/// queued.
/// </remarks>
public class ConcurrencyBackstopTests
{
    /// <summary>Holds every request at the origin until released, so concurrency is observable.</summary>
    private static RecordingHandler GatedOrigin(TaskCompletionSource gate) =>
        new(async (request, _, cancellationToken) =>
        {
            await gate.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };
        });

    private static async Task<(int MaxConcurrent, int Rejected)> SaturateAsync(
        ResilienceHarness harness, TaskCompletionSource gate, int requests)
    {
        Task<string?>[] inFlight = [.. Enumerable.Range(0, requests).Select(_ => Task.Run(async () =>
        {
            try
            {
                (await harness.GetAsync()).Dispose();
                return (string?)null;
            }
            catch (Exception exception)
            {
                return exception.GetType().Name;
            }
        }))];

        // Wait for the system to settle rather than for a specific count: with a queue configured, some
        // requests wait outside the origin and never arrive, so "all requests accounted for" never holds.
        await SettleAsync(harness, inFlight);

        gate.SetResult();
        string?[] outcomes = await Task.WhenAll(inFlight);
        return (harness.Origin.MaxConcurrent, outcomes.Count(o => o == nameof(RateLimiterRejectedException)));
    }


    /// <summary>
    /// Polls until neither the origin's arrival count nor the number of completed requests has changed for
    /// a short window, so the assertions run against a steady state rather than a race.
    /// </summary>
    private static async Task SettleAsync(ResilienceHarness harness, Task<string?>[] inFlight)
    {
        (int Arrived, int Done) previous = (-1, -1);
        for (int stable = 0; stable < 5;)
        {
            await Task.Delay(50);
            (int Arrived, int Done) current = (harness.Origin.Count, inFlight.Count(t => t.IsCompleted));
            stable = current == previous ? stable + 1 : 0;
            previous = current;
        }
    }

    [Fact]
    public async Task Backstop_CapsConcurrency_EvenWhenTheRateLimiterIsDisabled()
    {
        var gate = new TaskCompletionSource();
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled().Set("ConcurrencyLimiter:Backstop", "4"), GatedOrigin(gate));

        (int maxConcurrent, int rejected) = await SaturateAsync(harness, gate, requests: 10);

        Assert.Equal(4, maxConcurrent);
        Assert.Equal(6, rejected);
    }

    /// <summary>
    /// Assigning a rate limiter replaces the handler's default limiter outright, so the concurrency cap the
    /// client had a moment ago would disappear the moment an operator enabled rate limiting. It must not.
    /// </summary>
    [Fact]
    public async Task Backstop_StillApplies_WhenTheRateLimiterIsEnabled()
    {
        var gate = new TaskCompletionSource();
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled()
                .Set("ConcurrencyLimiter:Backstop", "4")
                .Set("RateLimiter:Enabled", "true")
                .Set("RateLimiter:PermitLimit", "1000")
                .Set("RateLimiter:Window", "01:00:00"),
            GatedOrigin(gate));

        (int maxConcurrent, int rejected) = await SaturateAsync(harness, gate, requests: 10);

        Assert.Equal(4, maxConcurrent);
        Assert.Equal(6, rejected);
    }

    /// <summary>
    /// With both limiters enabled the backstop handler is skipped -- the standard handler's limiter slot
    /// carries the rate limiter, and no separate backstop is added. The concurrency bound must still hold,
    /// because the client's own cap is validated at or below the backstop and is therefore the tighter of
    /// the two.
    /// </summary>
    /// <remarks>
    /// The documentation said "the backstop is always applied, including when a rate limiter is configured",
    /// three times, absolutely -- and it is not applied in this one combination. The behavior was safe, but
    /// only because of a validation rule written in a different file, and nothing exercised the combination.
    /// <para>
    /// Production change that would make this fail: dropping the <c>ConcurrencyLimiter:Limit</c> at-most-
    /// <c>Backstop</c> rule from <c>HttpResilienceOptionsValidator</c>, or widening the skip in
    /// <c>AddConcurrencyBackstopIfDisplaced</c> to a case where no other cap exists. Either would leave this
    /// configuration with no concurrency bound at all.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ConcurrencyBound_StillHolds_WhenBothLimitersAreEnabled()
    {
        var gate = new TaskCompletionSource();
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled()
                .Set("ConcurrencyLimiter:Backstop", "1000")
                .Set("ConcurrencyLimiter:Enabled", "true")
                .Set("ConcurrencyLimiter:Limit", "4")
                .Set("ConcurrencyLimiter:QueueLimit", "0")
                .Set("RateLimiter:Enabled", "true")
                .Set("RateLimiter:PermitLimit", "1000")
                .Set("RateLimiter:Window", "01:00:00"),
            GatedOrigin(gate));

        (int maxConcurrent, int rejected) = await SaturateAsync(harness, gate, requests: 10);

        Assert.Equal(4, maxConcurrent);
        Assert.Equal(6, rejected);
    }

    /// <summary>
    /// Under per-authority pipeline selection the backstop is <b>per authority</b>, not per client, because
    /// the platform instantiates the whole standard pipeline per key and the backstop is a limiter built from
    /// that pipeline's own options.
    /// </summary>
    /// <remarks>
    /// The rate limiter does not behave this way: it is a keyed singleton resolved from the container, so
    /// every per-authority pipeline shares one budget. The difference matters for capacity planning -- a
    /// client with N allow-listed authorities is bounded at <c>(N + 1) x Backstop</c> concurrent requests,
    /// counting the shared pipeline, not at <c>Backstop</c>. Pinned here so the number in the documentation
    /// and the number in force cannot drift apart again.
    /// </remarks>
    [Fact]
    public async Task Backstop_IsPerAuthority_UnderByAuthoritySelection()
    {
        var gate = new TaskCompletionSource();
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled()
                .Set("Retry:Enabled", "false")
                .Set("ConcurrencyLimiter:Backstop", "1")
                .Set("PipelineSelection:Mode", "ByAuthority")
                .Set("PipelineSelection:Authorities:0", "http://a.test")
                .Set("PipelineSelection:Authorities:1", "http://b.test"),
            GatedOrigin(gate));

        Task<HttpResponseMessage> held = harness.GetAsync("http://a.test/x");
        while (harness.Origin.Count < 1)
        {
            await Task.Yield();
        }

        // The same authority is bounded at the configured permit count.
        await Assert.ThrowsAsync<RateLimiterRejectedException>(() => harness.GetAsync("http://a.test/y"));

        // A different authority has a pipeline of its own, and therefore a backstop of its own.
        Task<HttpResponseMessage> other = harness.GetAsync("http://b.test/x");
        while (harness.Origin.Count < 2)
        {
            await Task.Yield();
        }

        gate.SetResult();
        (await held).Dispose();
        (await other).Dispose();

        Assert.Equal(2, harness.Origin.MaxConcurrent);
    }

    /// <summary>
    /// The rate limiter, unlike the backstop above, is one instance per client and is shared by every
    /// per-authority pipeline. A permit budget is a statement about a downstream quota, not about one host.
    /// </summary>
    [Fact]
    public async Task RateLimiter_IsSharedAcrossAuthorities_UnderByAuthoritySelection()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled()
                .Set("Retry:Enabled", "false")
                .Set("RateLimiter:Enabled", "true")
                .Set("RateLimiter:PermitLimit", "1")
                .Set("RateLimiter:Window", "01:00:00")
                .Set("PipelineSelection:Mode", "ByAuthority")
                .Set("PipelineSelection:Authorities:0", "http://a.test")
                .Set("PipelineSelection:Authorities:1", "http://b.test"));

        (await harness.GetAsync("http://a.test/x")).Dispose();

        await Assert.ThrowsAsync<RateLimiterRejectedException>(() => harness.GetAsync("http://b.test/x"));
        Assert.Equal(1, harness.Origin.Count);
    }

    [Fact]
    public async Task Backstop_AppliesToTheHedgingEndpointPipeline()
    {
        var gate = new TaskCompletionSource();
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Hedged()
                .Set("ConcurrencyLimiter:Backstop", "4")
                .Set("Hedging:Delay", "00:00:10"),
            GatedOrigin(gate), hedged: true);

        (int maxConcurrent, _) = await SaturateAsync(harness, gate, requests: 10);

        Assert.Equal(4, maxConcurrent);
    }

    /// <summary>
    /// The client's own cap is applied outside the handler, so a value above the backstop is never reached:
    /// the excess is rejected by the inner limiter rather than queued by the outer one.
    /// </summary>
    [Fact]
    public async Task ClientCapBelowTheBackstop_IsTheOneThatBinds()
    {
        var gate = new TaskCompletionSource();
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled()
                .Set("ConcurrencyLimiter:Backstop", "8")
                .Set("ConcurrencyLimiter:Enabled", "true")
                .Set("ConcurrencyLimiter:Limit", "3")
                .Set("ConcurrencyLimiter:QueueLimit", "20"),
            GatedOrigin(gate));

        (int maxConcurrent, int rejected) = await SaturateAsync(harness, gate, requests: 10);

        Assert.Equal(3, maxConcurrent);
        Assert.Equal(0, rejected);
    }

    /// <summary>
    /// The invariant is that in-flight requests never exceed the smaller of the two caps, however the
    /// pipeline chooses to enforce it.
    /// </summary>
    [Fact]
    public async Task ClientCapAndRateLimiterTogether_StillBoundConcurrency()
    {
        var gate = new TaskCompletionSource();
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled()
                .Set("ConcurrencyLimiter:Backstop", "8")
                .Set("ConcurrencyLimiter:Enabled", "true")
                .Set("ConcurrencyLimiter:Limit", "3")
                .Set("ConcurrencyLimiter:QueueLimit", "20")
                .Set("RateLimiter:Enabled", "true")
                .Set("RateLimiter:PermitLimit", "1000")
                .Set("RateLimiter:Window", "01:00:00"),
            GatedOrigin(gate));

        (int maxConcurrent, int rejected) = await SaturateAsync(harness, gate, requests: 10);

        Assert.Equal(3, maxConcurrent);
        Assert.Equal(0, rejected);
    }

    /// <summary>
    /// A rejection from the backstop and a rejection from a configured rate limiter are the same exception
    /// type on the same instrument, so an operator seeing <c>RateLimiterRejectedException</c> on a client
    /// with no rate limiter has nothing to tell them which control fired or what number to change.
    /// </summary>
    [Fact]
    public async Task BackstopRejection_SaysWhichControlFiredAndWhatToChange()
    {
        var sink = new ListLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddProvider(sink);
        });

        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(Settings.Enabled().Set("ConcurrencyLimiter:Backstop", "1").Build())
            .Build();
        services.AddHttpResilience(configuration);

        var gate = new TaskCompletionSource();
        RecordingHandler origin = GatedOrigin(gate);
        services.AddHttpClient("orders").AddHttpResilience().ConfigurePrimaryHttpMessageHandler(() => origin);

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

        string[] notices = [.. sink.Records
            .Where(record => record.Contains("concurrency backstop", StringComparison.OrdinalIgnoreCase))];

        Assert.NotEmpty(notices);
        Assert.Contains("orders", notices[0], StringComparison.Ordinal);
        Assert.Contains("ConcurrencyLimiter:Backstop", notices[0], StringComparison.Ordinal);
        Assert.Contains("1", notices[0], StringComparison.Ordinal);
    }
}
