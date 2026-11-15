using System.Diagnostics.Metrics;
using System.Net.Http.Headers;
using System.Net;
using System.Threading.RateLimiting;
using BclConcurrencyLimiterOptions = System.Threading.RateLimiting.ConcurrencyLimiterOptions;
using HttpResilience.NET.Internal;
using HttpResilience.NET.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.RateLimiting;
using Polly.Timeout;

namespace HttpResilience.NET.Tests.Behavior;

/// <summary>
/// A Retry-After header cannot extend a request past Timeout:Total.
/// </summary>
public class RetryAfterTests
{
    /// <summary>
    /// <c>Retry-After</c> replaces the computed delay, so the origin can stretch a schedule; <c>Timeout:Total</c>
    /// still stops it.
    /// </summary>
    /// <remarks>
    /// Fails if <c>Timeout:Total</c> stops wrapping the retry loop: an origin naming an hour would hold the
    /// caller for an hour.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ARetryAfterLongerThanTheTotalBudget_IsBoundedByTheTotalTimeout()
    {
        var origin = new RecordingHandler((request, _, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { RequestMessage = request };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(1));
            return Task.FromResult(response);
        });

        // MaxDelay raised past the header, so the header is honoured and only Timeout:Total can stop the wait.
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled()
                .Set("Retry:MaxDelay", "1.00:00:00")
                .Set("Timeout:Attempt", "00:00:00.500")
                .Set("Timeout:Total", "00:00:02")
                .Set("CircuitBreaker:SamplingDuration", "00:00:30"),
            origin);

        long start = Environment.TickCount64;
        await Assert.ThrowsAsync<TimeoutRejectedException>(() => harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken));
        long elapsed = Environment.TickCount64 - start;

        // Bounded by Timeout:Total, not by the hour the origin asked for.
        Assert.True(elapsed < 10_000, $"The total timeout should have bounded the wait, took {elapsed}ms.");

        // And the configured retries never ran: the first wait consumed the whole budget.
        Assert.Equal(1, origin.Count);
    }
}
