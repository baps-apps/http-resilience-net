using System.Net;
using System.Text;
using HttpResilience.NET.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;

namespace HttpResilience.NET.Tests.Options;

/// <summary>
/// CONFIGURATION.md compares this package's defaults with <c>AddStandardResilienceHandler</c> and
/// <c>AddStandardHedgingHandler</c>, for teams moving from them. Every number in that table is pinned here, on both
/// sides, so the table cannot drift from either.
/// </summary>
/// <remarks>
/// The platform side is measured, not remembered. The weekly platform-drift run executes these against the newest
/// platform release, so a change in Microsoft's defaults fails there before a consumer resolves it. Update the
/// table in docs/CONFIGURATION.md with any change here.
/// </remarks>
public class PlatformDefaultsComparisonTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public void ThePlatformsStandardDefaults_AreTheOnesTheTableQuotes()
    {
        var platform = new HttpStandardResilienceOptions();

        Assert.Equal(TimeSpan.FromSeconds(30), platform.TotalRequestTimeout.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(10), platform.AttemptTimeout.Timeout);
        Assert.Equal(3, platform.Retry.MaxRetryAttempts);
        Assert.Equal(TimeSpan.FromSeconds(2), platform.Retry.Delay);
        Assert.Equal(DelayBackoffType.Exponential, platform.Retry.BackoffType);
        Assert.True(platform.Retry.UseJitter);
        Assert.Null(platform.Retry.MaxDelay);
        Assert.True(platform.Retry.ShouldRetryAfterHeader);
        AssertBreaker(platform.CircuitBreaker);
        Assert.Equal(1000, platform.RateLimiter.DefaultRateLimiterOptions.PermitLimit);
        Assert.Equal(0, platform.RateLimiter.DefaultRateLimiterOptions.QueueLimit);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void ThePlatformsHedgingDefaults_AreTheOnesTheTableQuotes()
    {
        var platform = new HttpStandardHedgingResilienceOptions();

        Assert.Equal(TimeSpan.FromSeconds(30), platform.TotalRequestTimeout.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(10), platform.Endpoint.Timeout.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(2), platform.Hedging.Delay);
        Assert.Equal(1, platform.Hedging.MaxHedgedAttempts);
        AssertBreaker(platform.Endpoint.CircuitBreaker);
        Assert.Equal(1000, platform.Endpoint.RateLimiter.DefaultRateLimiterOptions.PermitLimit);
        Assert.Equal(0, platform.Endpoint.RateLimiter.DefaultRateLimiterOptions.QueueLimit);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void ThisPackagesDefaults_AreTheOnesTheTableQuotes()
    {
        var options = new HttpResilienceOptions();

        Assert.Equal(TimeSpan.FromSeconds(20), options.Timeout.Total);
        Assert.Equal(TimeSpan.FromSeconds(5), options.Timeout.Attempt);
        Assert.Null(options.Timeout.Client);
        Assert.Equal(2, options.Retry.MaxRetries);
        Assert.Equal(TimeSpan.FromMilliseconds(500), options.Retry.BaseDelay);
        Assert.Equal(HttpResilienceRetryBackoffType.Exponential, options.Retry.BackoffType);
        Assert.True(options.Retry.UseJitter);
        Assert.Equal(TimeSpan.FromSeconds(5), options.Retry.MaxDelay);
        Assert.True(options.Retry.UseRetryAfterHeader);
        Assert.True(options.Retry.DisableForUnsafeHttpMethods);
        Assert.Equal(0.5, options.CircuitBreaker.FailureRatio);
        Assert.Equal(20, options.CircuitBreaker.MinimumThroughput);
        Assert.Equal(TimeSpan.FromSeconds(30), options.CircuitBreaker.SamplingDuration);
        Assert.Equal(TimeSpan.FromSeconds(5), options.CircuitBreaker.BreakDuration);
        Assert.Equal(1000, options.ConcurrencyLimiter.Backstop);
        Assert.Equal(TimeSpan.FromSeconds(2), options.Hedging.Delay);
        Assert.Equal(1, options.Hedging.MaxHedgedAttempts);
        Assert.True(options.Hedging.DisableForUnsafeHttpMethods);

        // The table's reason for the shorter budget: every retry the defaults allow fits inside Timeout:Total
        // (3 × 5 s + 0.5 s + 1 s = 16.5 s), where the platform's (4 × 10 s + 2 s + 4 s + 8 s = 54 s) does not fit
        // in its 30 s.
        Assert.Equal(
            TimeSpan.FromSeconds(16.5),
            HttpResilience.NET.Internal.HttpResilienceOptionsValidator.EstimateRetrySchedule(options).WorstCase);
    }

    /// <summary>
    /// The snippet the table offers for matching the platform's numbers must reproduce them on the options the
    /// platform's handler actually runs with.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task TheMatchThePlatformSnippet_ReproducesThePlatformsNumbers()
    {
        Settings snippet = Settings.Empty()
            .Set("Timeout:Total", "00:00:30")
            .Set("Timeout:Attempt", "00:00:10")
            .Set("Retry:MaxRetries", "3")
            .Set("Retry:BaseDelay", "00:00:02")
            .Set("Retry:MaxDelay", "00:00:30")
            .Set("CircuitBreaker:FailureRatio", "0.1")
            .Set("CircuitBreaker:MinimumThroughput", "100");
        await using ResilienceHarness harness = ResilienceHarness.Create(snippet);

        var platform = new HttpStandardResilienceOptions();
        HttpStandardResilienceOptions effective = harness.Services
            .GetRequiredService<IOptionsMonitor<HttpStandardResilienceOptions>>().Get("test-standard");

        Assert.Equal(platform.TotalRequestTimeout.Timeout, effective.TotalRequestTimeout.Timeout);
        Assert.Equal(platform.AttemptTimeout.Timeout, effective.AttemptTimeout.Timeout);
        Assert.Equal(platform.Retry.MaxRetryAttempts, effective.Retry.MaxRetryAttempts);
        Assert.Equal(platform.Retry.Delay, effective.Retry.Delay);
        Assert.Equal(platform.Retry.BackoffType, effective.Retry.BackoffType);
        Assert.Equal(platform.Retry.UseJitter, effective.Retry.UseJitter);
        Assert.Equal(platform.Retry.ShouldRetryAfterHeader, effective.Retry.ShouldRetryAfterHeader);
        Assert.Equal(platform.CircuitBreaker.FailureRatio, effective.CircuitBreaker.FailureRatio);
        Assert.Equal(platform.CircuitBreaker.MinimumThroughput, effective.CircuitBreaker.MinimumThroughput);

        // The platform has no cap; Timeout:Total bounds any wait anyway, so the cap is set to it.
        Assert.Equal(effective.TotalRequestTimeout.Timeout, effective.Retry.MaxDelay);
    }

    /// <summary>
    /// The table says the platform retries every method and leaves <c>HttpClient.Timeout</c> infinite. Both are
    /// behavior, so both are measured.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ThePlatformsHandler_RetriesAPost_AndLeavesTheClientTimeoutInfinite()
    {
        var origin = new RecordingHandler(HttpStatusCode.InternalServerError);
        var services = new ServiceCollection();
        services.AddHttpClient("platform")
            .ConfigurePrimaryHttpMessageHandler(() => origin)
            .AddStandardResilienceHandler(options => options.Retry.Delay = TimeSpan.Zero);
        await using ServiceProvider provider = services.BuildServiceProvider();
        using HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("platform");

        using var content = new StringContent("x", Encoding.UTF8, "text/plain");
        using HttpResponseMessage response = await client.PostAsync(
            "http://origin.test/x", content, TestContext.Current.CancellationToken);

        Assert.Equal(["POST", "POST", "POST", "POST"], origin.Methods);
        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
    }

    private static void AssertBreaker(HttpCircuitBreakerStrategyOptions breaker)
    {
        Assert.Equal(0.1, breaker.FailureRatio);
        Assert.Equal(100, breaker.MinimumThroughput);
        Assert.Equal(TimeSpan.FromSeconds(30), breaker.SamplingDuration);
        Assert.Equal(TimeSpan.FromSeconds(5), breaker.BreakDuration);
    }
}
