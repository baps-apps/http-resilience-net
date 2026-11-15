using System.Net;
using HttpResilience.NET.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Polly.Timeout;

namespace HttpResilience.NET.Tests.Behavior;

public class TimeoutAndCancellationTests
{
    /// <summary>An origin that answers only when the request is cancelled.</summary>
    private static RecordingHandler HangingOrigin() =>
        new(async (request, _, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };
        });

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task AttemptTimeout_CutsOffASlowAttempt_AndTheAttemptIsRetried()
    {
        var time = new FakeTimeProvider();
        RecordingHandler origin = HangingOrigin();

        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled()
                .Set("Timeout:Attempt", "00:00:00.200")
                .Set("Timeout:Total", "00:00:10"),
            origin,
            timeProvider: time);

        Task<HttpResponseMessage> request = harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken);

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            await origin.WaitForCountAsync(attempt);
            time.Advance(TimeSpan.FromMilliseconds(200));
        }

        await Assert.ThrowsAsync<TimeoutRejectedException>(() => request);

        // The attempt timeout is transient, so every configured attempt was made.
        Assert.Equal(3, origin.Count);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task TotalTimeout_EndsTheLogicalRequest_MidRetry()
    {
        var time = new FakeTimeProvider();
        RecordingHandler origin = HangingOrigin();

        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled()
                .Set("Retry:MaxRetries", "2")
                .Set("Timeout:Attempt", "00:00:01")
                .Set("Timeout:Total", "00:00:01.500"),
            origin,
            timeProvider: time);

        Task<HttpResponseMessage> request = harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken);

        await origin.WaitForCountAsync(1);
        time.Advance(TimeSpan.FromSeconds(1));
        await origin.WaitForCountAsync(2);
        time.Advance(TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAsync<TimeoutRejectedException>(() => request);

        // Three attempts were configured; the total budget ended the request during the second.
        Assert.Equal(2, origin.Count);
    }

    /// <summary>
    /// HttpClient.Timeout sits outside the pipeline: at 100 seconds it would truncate a longer total budget,
    /// and infinite would leave the body unbounded. It is derived from the total budget.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public void HttpClientTimeout_DefaultsToTheTotalBudgetPlusABodyAllowance()
    {
        using ServiceProvider provider = ResilienceHarness.BuildProvider(
            Settings.Enabled()
                .Set("Timeout:Total", "00:03:00")
                .Set("Timeout:Attempt", "00:01:00")
                .Set("CircuitBreaker:SamplingDuration", "00:02:00"));

        using HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("test");

        // 3 minutes of total budget plus the 30-second body allowance.
        Assert.Equal(TimeSpan.FromSeconds(210), client.Timeout);
    }

    /// <summary>
    /// An explicit client budget is used verbatim, for a client whose queue wait or download is longer than
    /// the derived allowance.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public void HttpClientTimeout_UsesTheConfiguredValue_WhenOneIsGiven()
    {
        using ServiceProvider provider = ResilienceHarness.BuildProvider(
            Settings.Enabled().Set("Timeout:Client", "00:10:00"));

        using HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("test");

        Assert.Equal(TimeSpan.FromMinutes(10), client.Timeout);
    }

    /// <summary>
    /// Timeout:Total stops at response headers; HttpClient buffers the body afterwards, bounded only by
    /// HttpClient.Timeout.
    /// </summary>
    /// <remarks>
    /// Fails if HttpClient.Timeout becomes <see cref="Timeout.InfiniteTimeSpan"/>.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task StalledResponseBody_IsBoundedByTheClientTimeout_NotByTotal()
    {
        var origin = new RecordingHandler((request, _, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StalledContent()
            }));

        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled()
                .Set("Timeout:Total", "00:00:02")
                .Set("Timeout:Attempt", "00:00:01")
                .Set("Timeout:Client", "00:00:03")
                .Set("Retry:Enabled", "false")
                .Set("CircuitBreaker:SamplingDuration", "00:00:02"),
            origin);

        // Default HttpCompletionOption.ResponseContentRead, so HttpClient buffers the body.
        await Assert.ThrowsAsync<TaskCanceledException>(() => harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    /// <summary>Sends response headers immediately and then never finishes the body.</summary>
    /// <remarks>
    /// Observes the cancellation token, as a real stream read does; the test is about which deadline fires.
    /// </remarks>
    private sealed class StalledContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            await stream.WriteAsync(new byte[] { 1 }, cancellationToken);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task CallerCancellation_StopsImmediately_AndDoesNotRetry()
    {
        RecordingHandler origin = HangingOrigin();

        await using ResilienceHarness harness = ResilienceHarness.Create(Settings.Enabled(), origin);
        using var cts = new CancellationTokenSource();

        Task<HttpResponseMessage> request = harness.GetAsync(cancellationToken: cts.Token);
        await origin.WaitForCountAsync(1);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);

        Assert.Equal(1, origin.Count);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task CallerCancellation_IsNotCountedAsACircuitBreakerFailure()
    {
        RecordingHandler origin = HangingOrigin();

        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled()
                .Set("Retry:Enabled", "false")
                .Set("CircuitBreaker:MinimumThroughput", "2")
                .Set("CircuitBreaker:FailureRatio", "0.1")
                .Set("CircuitBreaker:SamplingDuration", "00:00:30"),
            origin);

        for (int i = 1; i <= 6; i++)
        {
            using var cts = new CancellationTokenSource();
            Task<HttpResponseMessage> request = harness.GetAsync(cancellationToken: cts.Token);
            await origin.WaitForCountAsync(i);
            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        }

        // A caller giving up says nothing about the dependency's health, so the circuit must still be closed.
        Assert.Empty(BreakerState.NotClosed(harness.Services));
    }

    /// <summary>
    /// A caller that gives up while queued for a concurrency slot must stop waiting, and must never reach
    /// the origin.
    /// </summary>
    /// <remarks>
    /// The concurrency limiter path. The rate-limiter queue is covered by
    /// <see cref="CallerCancellation_CancelsAQueuedRateLimiterPermitWait"/>.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task CallerCancellation_CancelsAQueuedConcurrencySlotWait()
    {
        var gate = new TaskCompletionSource();
        var origin = new RecordingHandler(async (request, _, cancellationToken) =>
        {
            await gate.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };
        });

        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled()
                .Set("Retry:Enabled", "false")
                .Set("ConcurrencyLimiter:Enabled", "true")
                .Set("ConcurrencyLimiter:Limit", "1")
                .Set("ConcurrencyLimiter:QueueLimit", "10"),
            origin);

        Task<HttpResponseMessage> holder = harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken);
        await origin.WaitForCountAsync(1);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        Task<HttpResponseMessage> queued = harness.GetAsync(cancellationToken: cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);

        gate.SetResult();
        await holder;
        Assert.Equal(1, origin.Count);
    }

    /// <summary>
    /// The same guarantee on the other queue: a caller waiting for a rate-limit permit must be released by
    /// its own cancellation token.
    /// </summary>
    /// <remarks>
    /// The rate limiter is an instance this package passes to the strategy as
    /// <c>args => limiter.AcquireAsync(1, args.Context.CancellationToken)</c>. Fails if
    /// <see cref="CancellationToken.None"/> is passed to <c>AcquireAsync</c> in
    /// <c>StandardPipelineConfigurator</c>: the queued request would wait an hour for the window.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task CallerCancellation_CancelsAQueuedRateLimiterPermitWait()
    {
        var gate = new TaskCompletionSource();
        var origin = new RecordingHandler(async (request, _, cancellationToken) =>
        {
            await gate.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };
        });

        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled()
                .Set("Retry:Enabled", "false")
                .Set("RateLimiter:Enabled", "true")
                .Set("RateLimiter:PermitLimit", "1")
                // An hour, so the only thing that can end the queued wait is cancellation.
                .Set("RateLimiter:Window", "01:00:00")
                .Set("RateLimiter:QueueLimit", "10"),
            origin);

        Task<HttpResponseMessage> holder = harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken);
        await origin.WaitForCountAsync(1);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        Task<HttpResponseMessage> queued = harness.GetAsync(cancellationToken: cts.Token);

        // Bounded, because the regression hangs rather than fails: with CancellationToken.None the queued
        // request waits for the window to roll, an hour away.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => queued.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        gate.SetResult();
        await holder;

        // The cancelled request never took a permit and never reached the origin.
        Assert.Equal(1, origin.Count);
        Assert.Empty(BreakerState.NotClosed(harness.Services));
    }
}
