using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace HttpResilience.NET.Options;

/// <summary>
/// Retry strategy options. Config key: "HttpResilienceOptions:Retry".
/// </summary>
public class RetryOptions
{
    /// <summary>
    /// Number of retries after the first attempt fails (transient failures only). Maps to HttpRetryStrategyOptions.MaxRetryAttempts.
    /// <para><b>Use case:</b> 2–5 for flaky or rate-limited APIs; 0 to disable retries. Default: 3.</para>
    /// </summary>
    [Range(0, 10, ErrorMessage = "MaxRetryAttempts must be between 0 and 10.")]
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>
    /// Base delay (seconds, supports sub-second values like 0.5) between retries; actual delay depends on <see cref="BackoffType"/>. Maps to HttpRetryStrategyOptions.Delay.
    /// <para><b>Use case:</b> Start with 1–3 seconds; increase if the backend needs cooldown. Default: 2.</para>
    /// </summary>
    [Range(0.0, 60.0, ErrorMessage = "BaseDelaySeconds must be between 0 and 60 seconds.")]
    public double BaseDelaySeconds { get; set; } = 2.0;

    /// <summary>
    /// How delay grows between retries. Maps to Polly DelayBackoffType.
    /// <para><b>Options:</b> <see cref="RetryBackoffType.Constant"/>, <see cref="RetryBackoffType.Linear"/>, <see cref="RetryBackoffType.Exponential"/> (default). Config key: "BackoffType".</para>
    /// <para><b>Effect:</b> Constant = same delay every time; Linear = delay × attempt; Exponential = delay × 2^attempt. Use Exponential for backing off under load; Constant for predictable spacing.</para>
    /// </summary>
    [JsonPropertyName("BackoffType")]
    public RetryBackoffType BackoffType { get; set; } = RetryBackoffType.Exponential;

    /// <summary>
    /// When true, adds random jitter to retry delays to avoid thundering herd. Maps to UseJitter.
    /// <para><b>Use case:</b> Keep true in multi-instance or high-concurrency scenarios. Default: true.</para>
    /// </summary>
    public bool UseJitter { get; set; } = true;

    /// <summary>
    /// When true, uses the HTTP Retry-After response header (if present) to decide when to retry. Maps to ShouldRetryAfterHeader.
    /// <para><b>Use case:</b> Enable when calling APIs that return 429/503 with Retry-After. Default: true.</para>
    /// </summary>
    public bool UseRetryAfterHeader { get; set; } = true;
}
