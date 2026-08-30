using HttpResilience.NET.Tests.Infrastructure;

namespace HttpResilience.NET.Tests.Behavior;

/// <summary>
/// What <c>CircuitBreaker:MinimumThroughput</c> counts.
/// </summary>
/// <remarks>
/// The breaker sits <b>inside</b> the retry loop -- total timeout, retry, circuit breaker, attempt timeout --
/// so every retry is a separate observation. The threshold is therefore in <i>attempts</i>, not in logical
/// requests, and with the default <c>Retry:MaxRetries</c> of 2 the documented figure is three times the
/// number of caller requests it actually takes.
/// <para>
/// That is the safe direction -- the breaker is more sensitive than the number suggests, not less -- but the
/// arithmetic in <c>docs/OPERATIONS.md</c> is what operators size against, so the unit is pinned here rather
/// than left to be re-derived from the pipeline diagram.
/// </para>
/// </remarks>
public class BreakerThroughputScopeTests
{
    /// <summary>
    /// One caller request, three attempts, a throughput threshold of three: the breaker opens. It could only
    /// do that by counting attempts.
    /// </summary>
    /// <remarks>
    /// Fails if the breaker ever moves outside the retry loop, which would make one logical request one
    /// observation and leave the circuit closed.
    /// </remarks>
    [Fact]
    public async Task MinimumThroughput_CountsAttempts_NotLogicalRequests()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled()
                .Set("Retry:MaxRetries", "2")
                .Set("CircuitBreaker:MinimumThroughput", "3")
                .Set("CircuitBreaker:FailureRatio", "0.1")
                .Set("CircuitBreaker:SamplingDuration", "00:00:30"));

        // One caller request. Three attempts reach the origin, and all three fail.
        (await harness.GetAsync()).Dispose();

        Assert.Equal(3, harness.Origin.Count);
        Assert.NotEmpty(HealthState.NotClosed(harness.Services));
    }

    /// <summary>
    /// The control: the same single request with retries off is one observation, which is below the same
    /// threshold, so the circuit stays closed.
    /// </summary>
    [Fact]
    public async Task OneAttempt_DoesNotReachAThreeAttemptThreshold()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled()
                .Set("Retry:Enabled", "false")
                .Set("CircuitBreaker:MinimumThroughput", "3")
                .Set("CircuitBreaker:FailureRatio", "0.1")
                .Set("CircuitBreaker:SamplingDuration", "00:00:30"));

        (await harness.GetAsync()).Dispose();

        Assert.Equal(1, harness.Origin.Count);
        Assert.Empty(HealthState.NotClosed(harness.Services));
    }
}
