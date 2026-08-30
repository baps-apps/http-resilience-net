using System.Net;
using HttpResilience.NET.Tests.Infrastructure;
using Polly.RateLimiting;

namespace HttpResilience.NET.Tests.Behavior;

/// <summary>
/// The pipeline shape is fixed, and these tests pin what "fixed" means by observing behavior rather than
/// inspecting registration. Ordering is only visible from the outside as a difference in how many times the
/// origin is called and which strategy sees which outcome.
/// </summary>
public class PipelineOrderingTests
{
    /// <summary>
    /// A rate-limit permit must cover a logical request including its retries. If the limiter were inside the
    /// retry loop, a single request with a permit limit of one would exhaust its own budget and surface a
    /// <see cref="RateLimiterRejectedException"/> instead of the origin's response.
    /// </summary>
    [Fact]
    public async Task RateLimiter_ChargesOnePermitPerLogicalRequest_NotPerAttempt()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled()
                .Set("RateLimiter:Enabled", "true")
                .Set("RateLimiter:PermitLimit", "1")
                .Set("RateLimiter:Window", "00:01:00")
                .Set("RateLimiter:QueueLimit", "0"));

        HttpResponseMessage response = await harness.GetAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(3, harness.Origin.Count);
    }

    [Fact]
    public async Task RateLimiter_StillRejects_WhenTheBudgetIsGenuinelyExhausted()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled()
                .Set("Retry:Enabled", "false")
                .Set("RateLimiter:Enabled", "true")
                .Set("RateLimiter:PermitLimit", "1")
                .Set("RateLimiter:Window", "00:01:00")
                .Set("RateLimiter:QueueLimit", "0"));

        await harness.GetAsync();

        await Assert.ThrowsAsync<RateLimiterRejectedException>(() => harness.GetAsync());
        Assert.Equal(1, harness.Origin.Count);
    }

    /// <summary>
    /// A concurrency slot must also cover the whole logical request. If the limiter were inside the retry loop
    /// it would release and re-acquire between attempts, so the cap would apply to attempts rather than to the
    /// callers it is meant to bound.
    /// </summary>
    [Fact]
    public async Task ConcurrencyLimiter_CapsConcurrentLogicalRequests()
    {
        var gate = new TaskCompletionSource();
        var origin = new RecordingHandler(async (request, _, cancellationToken) =>
        {
            await gate.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };
        });

        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled()
                .Set("ConcurrencyLimiter:Enabled", "true")
                .Set("ConcurrencyLimiter:Limit", "2")
                .Set("ConcurrencyLimiter:QueueLimit", "20"),
            origin);

        Task<HttpResponseMessage>[] inFlight = [.. Enumerable.Range(0, 12).Select(_ => harness.GetAsync())];

        // Give the limiter time to admit everything it is going to admit before releasing the origin.
        while (harness.Origin.Count < 2)
        {
            await Task.Yield();
        }

        Assert.Equal(2, harness.Origin.Count);
        gate.SetResult();
        await Task.WhenAll(inFlight);

        Assert.Equal(12, harness.Origin.Count);
        Assert.Equal(2, harness.Origin.MaxConcurrent);
    }

    [Fact]
    public async Task ConcurrencyLimiter_HoldsOneSlotAcrossRetries()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled()
                .Set("ConcurrencyLimiter:Enabled", "true")
                .Set("ConcurrencyLimiter:Limit", "1")
                .Set("ConcurrencyLimiter:QueueLimit", "0"));

        // A single caller retrying must never be rejected by its own concurrency cap.
        HttpResponseMessage response = await harness.GetAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(3, harness.Origin.Count);
    }

    /// <summary>
    /// A permit must mean the same thing on both pipelines: one logical request. A supplementary hedged
    /// attempt must never be the thing that gets rejected, because the caller would then see a
    /// <see cref="RateLimiterRejectedException"/> in place of the real outcome.
    /// </summary>
    [Fact]
    public async Task RateLimiter_ChargesOnePermitPerLogicalRequest_OnTheHedgingPipelineToo()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Hedged()
                .Set("Hedging:Delay", "00:00:00")
                .Set("Hedging:MaxHedgedAttempts", "2")
                .Set("RateLimiter:Enabled", "true")
                .Set("RateLimiter:PermitLimit", "1")
                .Set("RateLimiter:Window", "00:01:00")
                .Set("RateLimiter:QueueLimit", "0"),
            hedged: true);

        HttpResponseMessage response = await harness.GetAsync();
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(3, harness.Origin.Count);

        // The budget is spent, so the next logical request is refused before it reaches the wire at all.
        await Assert.ThrowsAsync<RateLimiterRejectedException>(() => harness.GetAsync());
        Assert.Equal(3, harness.Origin.Count);
    }

    [Fact]
    public async Task RateLimiter_IsActuallyWired_OnTheHedgingPipeline()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Hedged()
                .Set("Retry:Enabled", "false")
                .Set("Hedging:Delay", "00:00:00")
                .Set("Hedging:MaxHedgedAttempts", "1")
                .Set("RateLimiter:Enabled", "true")
                .Set("RateLimiter:PermitLimit", "2")
                .Set("RateLimiter:Window", "00:01:00")
                .Set("RateLimiter:QueueLimit", "0"),
            hedged: true);

        await harness.GetAsync();
        await harness.GetAsync();

        await Assert.ThrowsAsync<RateLimiterRejectedException>(() => harness.GetAsync());
    }
}
