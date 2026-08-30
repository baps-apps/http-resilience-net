namespace HttpResilience.NET.Options;

/// <summary>
/// Circuit breaker thresholds: when to stop calling a dependency that is failing, and when to try again.
/// </summary>
/// <remarks>
/// The breaker is <b>process-local</b>. Every replica keeps its own state and must independently observe
/// <see cref="MinimumThroughput"/> before it can open, so a 20-replica deployment sends at least
/// <c>20 x MinimumThroughput</c> failing attempts before the fleet stops calling a dead dependency. Caller
/// cancellation is never counted as a failure.
/// <para>
/// The defaults suit a busy client. A low-traffic client needs a lower
/// <see cref="MinimumThroughput"/> or its breaker never engages at all.
/// </para>
/// </remarks>
/// <example>
/// A client that handles a few requests a minute, where the default threshold of 100 would never be reached:
/// <code language="json">
/// {
///   "HttpResilience": {
///     "Clients": {
///       "Reports": {
///         "CircuitBreaker": { "MinimumThroughput": 5, "SamplingDuration": "00:02:00" }
///       }
///     }
///   }
/// }
/// </code>
/// </example>
public sealed class CircuitBreakerOptions
{
    /// <summary>
    /// Gets or sets the proportion of failures within <see cref="SamplingDuration"/> that opens the circuit.
    /// Must be greater than 0 and at most 1. Defaults to 0.1, i.e. 10%.
    /// </summary>
    /// <remarks>
    /// Low on purpose. A dependency failing one call in ten is already degraded, and shedding load early is
    /// what gives it room to recover.
    /// </remarks>
    public double FailureRatio { get; set; } = 0.1;

    /// <summary>
    /// Gets or sets the number of <b>attempts</b> that must be observed within <see cref="SamplingDuration"/>
    /// before <see cref="FailureRatio"/> is evaluated. Must be at least 2. Defaults to 100.
    /// </summary>
    /// <remarks>
    /// <b>Attempts, not caller requests.</b> The breaker sits inside the retry loop, so every retry is its own
    /// observation and one failing request contributes <c>1 + <see cref="RetryOptions.MaxRetries"/></c> of
    /// them. At the defaults, 100 attempts over 30 seconds is 3.3 attempts per second per replica -- roughly
    /// 1.1 failing caller requests per second. Below that rate the breaker can never open, and the client
    /// effectively has only timeouts protecting it.
    /// </remarks>
    public int MinimumThroughput { get; set; } = 100;

    /// <summary>
    /// Gets or sets the rolling window over which failures are counted. Must be at least 500 milliseconds and
    /// at least double <see cref="TimeoutOptions.Attempt"/>. Defaults to 30 seconds.
    /// </summary>
    public TimeSpan SamplingDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets how long the circuit stays open before a trial request is allowed through. Must be at
    /// least 500 milliseconds. Defaults to 5 seconds.
    /// </summary>
    /// <remarks>
    /// While the circuit is open, calls fail immediately with <c>BrokenCircuitException</c> instead of
    /// reaching the network -- which is the point, but it means callers see a different exception during an
    /// outage. Longer gives the dependency more room; shorter recovers faster once it is healthy.
    /// </remarks>
    public TimeSpan BreakDuration { get; set; } = TimeSpan.FromSeconds(5);
}
