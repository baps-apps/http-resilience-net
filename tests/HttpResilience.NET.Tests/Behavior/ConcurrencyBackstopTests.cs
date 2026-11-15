using System.Net;
using HttpResilience.NET.Tests.Infrastructure;
using HttpResilience.NET.Tests.Internal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly.RateLimiting;

namespace HttpResilience.NET.Tests.Behavior;

/// <summary>
/// The standard resilience handler always carries a concurrency limiter. Above its permit count requests fail
/// with <see cref="RateLimiterRejectedException"/>, even with no rate limiter enabled.
/// </summary>
/// <remarks>
/// These tests drive the limiter to genuine saturation rather than asserting the configured value.
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
        using var gauges = new GaugeCollector(harness.Services);
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

        // While the gate is closed, every request ends up in exactly one place and stays there: at the origin,
        // rejected (completed), or waiting in a limiter's queue. Wait for that state, not for a quiet period.
        await AllAccountedForAsync(harness, gauges, inFlight);

        gate.SetResult();
        string?[] outcomes = await Task.WhenAll(inFlight);
        return (harness.Origin.MaxConcurrent, outcomes.Count(o => o == nameof(RateLimiterRejectedException)));
    }

    /// <summary>
    /// Completes once every request is at the origin, completed, or counted by a limiter's
    /// <c>queued_requests</c> gauge. Fails after ten seconds rather than hanging the run.
    /// </summary>
    /// <remarks>
    /// A fixed condition, so a slow machine only makes it take longer; it never decides too early. Missing
    /// limiters do not stall it: every request then reaches the origin, and the assertions fail instead.
    /// </remarks>
    private static async Task AllAccountedForAsync(
        ResilienceHarness harness, GaugeCollector gauges, Task<string?>[] inFlight)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            int arrived = harness.Origin.Count;
            int completed = inFlight.Count(task => task.IsCompleted);
            long queued = gauges.Collect("http_resilience.limiter.queued_requests").Sum(m => m.Value);

            if (arrived + completed + queued == inFlight.Length)
            {
                return;
            }

            try
            {
                await Task.Delay(10, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                Assert.Fail(
                    $"Expected all {inFlight.Length} requests at the origin, completed or queued within 10 s; " +
                    $"saw {arrived} at the origin, {completed} completed and {queued} queued.");
            }
        }
    }

    [Fact(Timeout = TestTimeouts.Default)]
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
    /// A rate limiter replaces the handler's default limiter, so the concurrency cap must be re-added rather
    /// than disappear.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
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
    /// With both limiters enabled the backstop handler is skipped, and the bound still holds because the
    /// client's own cap is validated at or below the backstop.
    /// </summary>
    /// <remarks>
    /// Fails if the <c>ConcurrencyLimiter:Limit</c> at-most-<c>Backstop</c> rule is dropped from
    /// <c>HttpResilienceOptionsValidator</c>, or if the skip in <c>AddConcurrencyBackstopIfDisplaced</c> is
    /// widened to a case with no other cap.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
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
    /// The rate limiter is a keyed singleton shared by every per-authority pipeline. A client with N listed
    /// authorities is bounded at <c>(N + 1) x Backstop</c>, counting the shared pipeline; the documentation
    /// quotes this number.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
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

        Task<HttpResponseMessage> held = harness.GetAsync("http://a.test/x", TestContext.Current.CancellationToken);
        await harness.Origin.WaitForCountAsync(1);

        // The same authority is bounded at the configured permit count.
        await Assert.ThrowsAsync<RateLimiterRejectedException>(() => harness.GetAsync("http://a.test/y", TestContext.Current.CancellationToken));

        // A different authority has a pipeline of its own, and therefore a backstop of its own.
        Task<HttpResponseMessage> other = harness.GetAsync("http://b.test/x", TestContext.Current.CancellationToken);
        await harness.Origin.WaitForCountAsync(2);

        gate.SetResult();
        (await held).Dispose();
        (await other).Dispose();

        Assert.Equal(2, harness.Origin.MaxConcurrent);
    }

    /// <summary>
    /// The rate limiter, unlike the backstop above, is one instance per client and is shared by every
    /// per-authority pipeline. A permit budget is a statement about a downstream quota, not about one host.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
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

        (await harness.GetAsync("http://a.test/x", TestContext.Current.CancellationToken)).Dispose();

        await Assert.ThrowsAsync<RateLimiterRejectedException>(() => harness.GetAsync("http://b.test/x", TestContext.Current.CancellationToken));
        Assert.Equal(1, harness.Origin.Count);
    }

    [Fact(Timeout = TestTimeouts.Default)]
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
    /// A hedged client's backstop bounds each authority on its own: one saturated host does not refuse
    /// requests to another.
    /// </summary>
    /// <remarks>
    /// Fails if the endpoint limiters are shared across authorities, which would turn the per-authority bound
    /// into a per-client one.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task HedgedBackstop_IsPerAuthority()
    {
        var gate = new TaskCompletionSource();
        var origin = new RecordingHandler(async (request, _, cancellationToken) =>
        {
            if (request.RequestUri!.Host == "origin.test")
            {
                await gate.Task.WaitAsync(cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };
        });

        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Hedged()
                .Set("ConcurrencyLimiter:Backstop", "1")
                .Set("Hedging:Delay", "00:00:10")
                .Set("PipelineSelection:Authorities:1", "http://other.test"),
            origin, hedged: true);

        Task<HttpResponseMessage> held = harness.GetAsync("http://origin.test/x", TestContext.Current.CancellationToken);
        await origin.WaitForCountAsync(1);

        // The held host is at its bound; the other host is not.
        await Assert.ThrowsAsync<RateLimiterRejectedException>(() => harness.GetAsync("http://origin.test/x", TestContext.Current.CancellationToken));
        using HttpResponseMessage other = await harness.GetAsync("http://other.test/x", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);

        gate.SetResult();
        (await held).Dispose();
    }

    /// <summary>
    /// The client's own cap is applied outside the handler, so a value above the backstop is never reached:
    /// the excess is rejected by the inner limiter rather than queued by the outer one.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
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
    [Fact(Timeout = TestTimeouts.Default)]
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
    /// Backstop and rate-limiter rejections are the same exception type, so the log must say which control
    /// fired and what to change.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
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
        services.AddHttpClient("orders").AddHttpResilienceHandler().ConfigurePrimaryHttpMessageHandler(() => origin);

        await using ServiceProvider provider = services.BuildServiceProvider();
        HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("orders");

        Task<HttpResponseMessage> held = client.GetAsync("http://origin.test/x", TestContext.Current.CancellationToken);
        await origin.WaitForCountAsync(1);

        await Assert.ThrowsAsync<RateLimiterRejectedException>(() => client.GetAsync("http://origin.test/x", TestContext.Current.CancellationToken));
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
