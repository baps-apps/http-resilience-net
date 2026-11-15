using System.Net;
using HttpResilience.NET.Tests.Infrastructure;
using Polly.CircuitBreaker;

namespace HttpResilience.NET.Tests.Behavior;

/// <summary>
/// The breaker's defaults (20 attempts, 50% failures, 30 s) must open for a dependency that is down at the traffic
/// of a typical internal API, and stay closed for a steady low error rate that retries absorb.
/// </summary>
/// <remarks>
/// The platform's defaults (100 attempts, 10%) need 3.3 attempts per second in one replica before the breaker can
/// open at all, so most internal clients never had one. No <c>CircuitBreaker</c> key is set here: these tests run on
/// the package's defaults.
/// </remarks>
public class CircuitBreakerDefaultsTests
{
    private static Settings DefaultBreaker() => Settings.Empty()
        .Set("Retry:BaseDelay", "00:00:00")
        .Set("Retry:BackoffType", "Constant");

    /// <summary>
    /// The 20th attempt opens it: six failing requests (three attempts each, with the default two retries) return
    /// the 503, the seventh is cut off by the opening breaker after its second attempt, and the next fails fast.
    /// </summary>
    /// <remarks>Fails under the platform's <c>MinimumThroughput</c> of 100: the breaker never opens.</remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ADependencyThatIsDown_OpensTheBreaker_AtTheTwentiethAttempt()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            DefaultBreaker(), new RecordingHandler(HttpStatusCode.ServiceUnavailable));

        for (int i = 0; i < 6; i++)
        {
            using HttpResponseMessage response =
                await harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }

        await Assert.ThrowsAsync<BrokenCircuitException>(
            () => harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(20, harness.Origin.Count);

        await Assert.ThrowsAsync<BrokenCircuitException>(
            () => harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(20, harness.Origin.Count);
    }

    /// <summary>
    /// One attempt in ten failing, over well past the minimum throughput, leaves the breaker closed: retries absorb
    /// it, and opening would turn a 10% error rate into a 100% one.
    /// </summary>
    /// <remarks>Fails under a <c>FailureRatio</c> of 0.1 with the lower throughput.</remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ASteadyTenPercentErrorRate_LeavesTheBreakerClosed()
    {
        var origin = new RecordingHandler((_, attempt, _) => Task.FromResult(new HttpResponseMessage(
            attempt % 10 == 0 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)));
        await using ResilienceHarness harness = ResilienceHarness.Create(DefaultBreaker(), origin);

        for (int i = 0; i < 60; i++)
        {
            using HttpResponseMessage response =
                await harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Empty(BreakerState.NotClosed(harness.Services));
    }
}
