namespace HttpResilience.NET;

/// <summary>
/// Circuit breaker thresholds: when to stop calling a failing dependency, and when to try again.
/// </summary>
/// <remarks>
/// State is per process: each replica must see <see cref="MinimumThroughput"/> attempts on its own before it
/// can open. Caller cancellation is not counted as a failure. Low-traffic clients need a lower
/// <see cref="MinimumThroughput"/>, or the breaker never opens; a startup log line (event 11) states the rate
/// each client needs.
/// </remarks>
/// <example>
/// A client handling a few requests a minute:
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
public sealed class HttpResilienceCircuitBreakerOptions
{
    /// <summary>
    /// Gets or sets the failure proportion within <see cref="SamplingDuration"/> that opens the circuit. Must
    /// be greater than 0 and at most 1. Defaults to 0.5 (50%).
    /// </summary>
    /// <remarks>
    /// Opens on a dependency that is down, not on a steady low error rate that retries absorb: opening turns
    /// every request into a failure. The platform's default is 0.1.
    /// </remarks>
    public double FailureRatio { get; set; } = 0.5;

    /// <summary>
    /// Gets or sets the attempts that must be observed within <see cref="SamplingDuration"/> before
    /// <see cref="FailureRatio"/> is evaluated. Must be at least 2. Defaults to 20.
    /// </summary>
    /// <remarks>
    /// Counts attempts, not caller requests: each retry is a separate observation. At the defaults, 20 attempts
    /// in 30 seconds is 0.67 per second in one replica, about seven fully failing requests with two retries. The
    /// platform's default of 100 needs 3.3 per second, which most internal clients never reach.
    /// </remarks>
    public int MinimumThroughput { get; set; } = 20;

    /// <summary>
    /// Gets or sets the window over which failures are counted. Must be at least 500 milliseconds and at least
    /// twice <see cref="HttpResilienceTimeoutOptions.Attempt"/>. Defaults to 30 seconds.
    /// </summary>
    public TimeSpan SamplingDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets how long the circuit stays open before a trial request. Must be at least 500 milliseconds.
    /// Defaults to 5 seconds.
    /// </summary>
    /// <remarks>
    /// While open, calls fail immediately with <c>BrokenCircuitException</c>.
    /// </remarks>
    public TimeSpan BreakDuration { get; set; } = TimeSpan.FromSeconds(5);
}
