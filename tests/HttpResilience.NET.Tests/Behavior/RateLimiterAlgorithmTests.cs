using HttpResilience.NET.Tests.Infrastructure;
using Polly.RateLimiting;

namespace HttpResilience.NET.Tests.Behavior;

/// <summary>
/// Every algorithm the schema offers is driven to genuine exhaustion.
/// </summary>
/// <remarks>
/// <c>RateLimiterFactory</c> maps the schema onto three BCL limiter types. <c>TokenLimit</c> (capacity) and
/// <c>TokensPerPeriod</c> (refill rate) are adjacent <c>int</c> properties; transposing them would compile.
/// Each budget is spent within a window too long to replenish, so assertions are counts, not timings.
/// </remarks>
public class RateLimiterAlgorithmTests
{
    /// <summary>Configures every algorithm's keys, so the theory below only varies the algorithm name.</summary>
    private static Settings Budget(string algorithm, int permits) => Settings.Enabled()
        .Set("Retry:Enabled", "false")
        .Set("RateLimiter:Enabled", "true")
        .Set("RateLimiter:Algorithm", algorithm)
        .Set("RateLimiter:QueueLimit", "0")
        .Set("RateLimiter:PermitLimit", permits.ToString(System.Globalization.CultureInfo.InvariantCulture))
        .Set("RateLimiter:Window", "01:00:00")
        .Set("RateLimiter:SegmentsPerWindow", "4")
        .Set("RateLimiter:TokenLimit", permits.ToString(System.Globalization.CultureInfo.InvariantCulture))
        .Set("RateLimiter:TokensPerPeriod", "1")
        .Set("RateLimiter:ReplenishmentPeriod", "01:00:00");

    /// <summary>
    /// Fails for any algorithm whose budget key is mapped to the wrong property: the request count admitted
    /// before the first rejection is the configured capacity, whichever algorithm is selected.
    /// </summary>
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData("FixedWindow")]
    [InlineData("SlidingWindow")]
    [InlineData("TokenBucket")]
    public async Task EveryAlgorithm_AdmitsItsBudget_AndThenRejects(string algorithm)
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(Budget(algorithm, permits: 3));

        for (int i = 0; i < 3; i++)
        {
            (await harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken)).Dispose();
        }

        await Assert.ThrowsAsync<RateLimiterRejectedException>(() => harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(3, harness.Origin.Count);
    }

    /// <summary>
    /// A token bucket's capacity is <c>TokenLimit</c> and its refill rate is <c>TokensPerPeriod</c>. This is
    /// the assertion that separates them: with a capacity of 3 and a refill of 1 per hour, three requests are
    /// admitted immediately. Transposed, the bucket would hold one token and only one would get through.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task TokenBucket_TakesItsCapacityFromTokenLimit_NotFromTokensPerPeriod()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled()
                .Set("Retry:Enabled", "false")
                .Set("RateLimiter:Enabled", "true")
                .Set("RateLimiter:Algorithm", "TokenBucket")
                .Set("RateLimiter:TokenLimit", "3")
                .Set("RateLimiter:TokensPerPeriod", "1")
                .Set("RateLimiter:ReplenishmentPeriod", "01:00:00")
                .Set("RateLimiter:QueueLimit", "0"));

        for (int i = 0; i < 3; i++)
        {
            (await harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken)).Dispose();
        }

        await Assert.ThrowsAsync<RateLimiterRejectedException>(() => harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(3, harness.Origin.Count);
    }

    /// <summary>
    /// A queue is honoured on every algorithm: a request arriving with the budget spent waits for a permit.
    /// </summary>
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData("SlidingWindow")]
    [InlineData("TokenBucket")]
    public async Task EveryAlgorithm_QueuesUpToItsQueueLimit_BeforeRejecting(string algorithm)
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Budget(algorithm, permits: 1).Set("RateLimiter:QueueLimit", "1"));

        (await harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken)).Dispose();

        // The budget is spent and the replenishment period is an hour away, so the queued request waits
        // until the caller gives up rather than being rejected. The one after it has nowhere to wait.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        Task<HttpResponseMessage> queued = harness.GetAsync(cancellationToken: cts.Token);

        await Assert.ThrowsAsync<RateLimiterRejectedException>(() => harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);

        Assert.Equal(1, harness.Origin.Count);
    }
}
