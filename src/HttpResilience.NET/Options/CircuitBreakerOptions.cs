using System.ComponentModel.DataAnnotations;

namespace HttpResilience.NET.Options;

/// <summary>
/// Circuit breaker strategy options. Config key: "HttpResilienceOptions:CircuitBreaker".
/// Property names match configuration keys (e.g. MinimumThroughput, FailureRatio) for consistent binding.
/// </summary>
public class CircuitBreakerOptions
{
    /// <summary>
    /// Minimum number of requests in the sampling window before the circuit can open (1–100).
    /// <para><b>Use case:</b> Prevents opening on low traffic; e.g. 10–100. Default: 100 (Microsoft standard).</para>
    /// </summary>
    [Range(1, 100, ErrorMessage = "MinimumThroughput must be between 1 and 100.")]
    public int MinimumThroughput { get; set; } = 100;

    /// <summary>
    /// Fraction of requests that may fail (0.01–1.0) before the circuit opens; e.g. 0.1 = 10%.
    /// <para><b>Use case:</b> Stricter (e.g. 0.05) for critical backends; looser (e.g. 0.2) for best-effort. Default: 0.1.</para>
    /// </summary>
    [Range(0.01, 1.0, ErrorMessage = "FailureRatio must be between 0.01 and 1.0.")]
    public double FailureRatio { get; set; } = 0.1;

    /// <summary>
    /// Time window (seconds) over which the failure ratio is computed (1–600).
    /// <para><b>Use case:</b> Shorter (e.g. 10–30) for faster reaction; longer to avoid flapping. Default: 30.</para>
    /// </summary>
    [Range(1, 600, ErrorMessage = "SamplingDurationSeconds must be between 1 and 600.")]
    public int SamplingDurationSeconds { get; set; } = 30;

    /// <summary>
    /// How long (seconds) the circuit stays open before a half-open trial (1–300).
    /// <para><b>Use case:</b> Give the downstream time to recover; 5–30 typical. Default: 5 (Microsoft standard).</para>
    /// </summary>
    [Range(1, 300, ErrorMessage = "BreakDurationSeconds must be between 1 and 300 seconds.")]
    public int BreakDurationSeconds { get; set; } = 5;
}
