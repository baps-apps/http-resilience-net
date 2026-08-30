using System.Net;
using HttpResilience.NET.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Polly.Timeout;

namespace HttpResilience.NET.Tests.Behavior;

public class TimeoutAndCancellationTests
{
    private static RecordingHandler SlowOrigin(TimeSpan delay) =>
        new(async (request, _, cancellationToken) =>
        {
            await Task.Delay(delay, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };
        });

    [Fact]
    public async Task AttemptTimeout_CutsOffASlowAttempt_AndTheAttemptIsRetried()
    {
        RecordingHandler origin = SlowOrigin(TimeSpan.FromSeconds(30));

        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled()
                .Set("Timeout:Attempt", "00:00:00.200")
                .Set("Timeout:Total", "00:00:10"),
            origin);

        await Assert.ThrowsAsync<TimeoutRejectedException>(() => harness.GetAsync());

        // The attempt timeout is transient, so every configured attempt was made.
        Assert.Equal(3, origin.Count);
    }

    [Fact]
    public async Task TotalTimeout_BoundsTheWholeLogicalRequest()
    {
        RecordingHandler origin = SlowOrigin(TimeSpan.FromSeconds(30));

        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled()
                .Set("Retry:MaxRetries", "2")
                .Set("Timeout:Attempt", "00:00:00.200")
                .Set("Timeout:Total", "00:00:00.700"),
            origin);

        long start = Environment.TickCount64;
        await Assert.ThrowsAnyAsync<Exception>(() => harness.GetAsync());
        long elapsed = Environment.TickCount64 - start;

        Assert.True(elapsed < 5_000, $"The total timeout should have bounded the request, took {elapsed}ms.");
        Assert.True(origin.Count <= 3, $"No more attempts than configured should be made, saw {origin.Count}.");
    }

    /// <summary>
    /// HttpClient.Timeout sits outside the resilience pipeline. Left at its 100-second default it would
    /// silently truncate any longer total budget; set to infinite it would leave the response body read with
    /// no bound at all. It is therefore derived from the total budget, comfortably above it.
    /// </summary>
    [Fact]
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
    [Fact]
    public void HttpClientTimeout_UsesTheConfiguredValue_WhenOneIsGiven()
    {
        using ServiceProvider provider = ResilienceHarness.BuildProvider(
            Settings.Enabled().Set("Timeout:Client", "00:10:00"));

        using HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("test");

        Assert.Equal(TimeSpan.FromMinutes(10), client.Timeout);
    }

    /// <summary>
    /// Timeout:Total stops applying when response headers arrive: content is buffered by HttpClient after the
    /// resilience handler has already returned. HttpClient.Timeout is the only thing that bounds that, which
    /// is why it is not infinite.
    /// </summary>
    /// <remarks>
    /// Fails if HttpClient.Timeout goes back to <see cref="Timeout.InfiniteTimeSpan"/>: the request then runs
    /// for as long as the origin cares to trickle the body, holding a connection and an inbound request, and
    /// the pipeline's own telemetry reports a fast successful attempt.
    /// </remarks>
    [Fact]
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
        await Assert.ThrowsAsync<TaskCanceledException>(() => harness.GetAsync());
    }

    /// <summary>Sends response headers immediately and then never finishes the body.</summary>
    /// <remarks>
    /// Observes the cancellation token, as a real response stream read does -- the point of the test is which
    /// deadline cancels it, not whether the transport is interruptible.
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
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            await stream.WriteAsync(new byte[] { 1 }, cancellationToken);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    [Fact]
    public async Task CallerCancellation_StopsImmediately_AndDoesNotRetry()
    {
        RecordingHandler origin = SlowOrigin(TimeSpan.FromSeconds(30));

        await using ResilienceHarness harness = ResilienceHarness.Create(Settings.Enabled(), origin);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.GetAsync(cancellationToken: cts.Token));

        Assert.Equal(1, origin.Count);
    }

    [Fact]
    public async Task CallerCancellation_IsNotCountedAsACircuitBreakerFailure()
    {
        RecordingHandler origin = SlowOrigin(TimeSpan.FromSeconds(30));

        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled()
                .Set("Retry:Enabled", "false")
                .Set("CircuitBreaker:MinimumThroughput", "2")
                .Set("CircuitBreaker:FailureRatio", "0.1")
                .Set("CircuitBreaker:SamplingDuration", "00:00:30"),
            origin);

        for (int i = 0; i < 6; i++)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => harness.GetAsync(cancellationToken: cts.Token));
        }

        // A caller giving up says nothing about the dependency's health, so the circuit must still be closed.
        Assert.Empty(HealthState.NotClosed(harness.Services));
    }

    /// <summary>
    /// A caller that gives up while queued for a concurrency slot must stop waiting, and must never reach
    /// the origin.
    /// </summary>
    /// <remarks>
    /// Named <c>CallerCancellation_CancelsAQueuedRateLimiterWait</c> until a review noticed it configures a
    /// <c>ConcurrencyLimiter</c> and never touches the rate limiter. The two are different strategy
    /// configurations -- this one is Polly's <c>DefaultRateLimiterOptions</c>, the other is a
    /// <c>RateLimiter</c> instance this package supplies -- so the misnamed test left the rate-limiter queue
    /// looking covered when nothing exercised it. See
    /// <see cref="CallerCancellation_CancelsAQueuedRateLimiterPermitWait"/>.
    /// </remarks>
    [Fact]
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

        Task<HttpResponseMessage> holder = harness.GetAsync();
        while (origin.Count < 1)
        {
            await Task.Yield();
        }

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
    /// A different code path from the concurrency slot above. The concurrency limiter is Polly's
    /// <c>DefaultRateLimiterOptions</c>, built by the strategy; the rate limiter is a
    /// <see cref="System.Threading.RateLimiting.RateLimiter"/> instance this package constructs and hands to
    /// the strategy as <c>args => limiter.AcquireAsync(1, args.Context.CancellationToken)</c>. Whether that
    /// token is the caller's is the thing being asserted, and passing the wrong one is a one-word change
    /// nothing else would catch.
    /// <para>
    /// Production change that would make this fail: passing <see cref="CancellationToken.None"/> to
    /// <c>AcquireAsync</c> in <c>StandardPipelineConfigurator</c>. The queued request would then wait for
    /// the window to roll -- an hour here -- with the caller already gone.
    /// </para>
    /// </remarks>
    [Fact]
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

        Task<HttpResponseMessage> holder = harness.GetAsync();
        while (origin.Count < 1)
        {
            await Task.Yield();
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        Task<HttpResponseMessage> queued = harness.GetAsync(cancellationToken: cts.Token);

        // Bounded, because the regression this guards does not fail -- it hangs. Passing
        // CancellationToken.None to AcquireAsync leaves the queued request waiting for the window to roll,
        // an hour away, and a test run that never finishes is a worse CI signal than one that fails.
        // Measured: 278 ms with the caller's token, no result at all without it.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => queued.WaitAsync(TimeSpan.FromSeconds(10)));

        gate.SetResult();
        await holder;

        // The cancelled request never took a permit and never reached the origin.
        Assert.Equal(1, origin.Count);
        Assert.Empty(HealthState.NotClosed(harness.Services));
    }
}
