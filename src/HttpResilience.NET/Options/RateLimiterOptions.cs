using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace HttpResilience.NET.Options;

/// <summary>
/// Rate limiter options (standard pipeline only). Config key: "HttpResilienceOptions:RateLimiter".
/// </summary>
public class RateLimiterOptions
{
    /// <summary>
    /// When true, limits how many requests can be sent per time window (standard pipeline only).
    /// <para><b>Use case:</b> Enable when you must stay under a backend or quota limit. Default: false.</para>
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Maximum number of requests allowed per window when <see cref="Enabled"/> is true.
    /// <para><b>Use case:</b> Set to your allowed throughput (e.g. 1000/sec depending on window). Default: 1000.</para>
    /// </summary>
    [Range(1, 100_000, ErrorMessage = "PermitLimit must be between 1 and 100000.")]
    public int PermitLimit { get; set; } = 1000;

    /// <summary>
    /// Length of the rate-limit window in seconds. For SlidingWindow, the window is split into <see cref="SegmentsPerWindow"/> segments.
    /// <para><b>Use case:</b> 1 for per-second, 60 for per-minute. Default: 1.</para>
    /// </summary>
    [Range(1, 3600, ErrorMessage = "WindowSeconds must be between 1 and 3600.")]
    public int WindowSeconds { get; set; } = 1;

    /// <summary>
    /// When rate limit is exceeded, how many requests can wait in queue (0 = no queue; requests fail immediately).
    /// <para><b>Use case:</b> 0 for strict limiting; small value to smooth bursts. Default: 0.</para>
    /// </summary>
    [Range(0, 10_000, ErrorMessage = "QueueLimit must be between 0 and 10000.")]
    public int QueueLimit { get; set; }

    /// <summary>
    /// Rate limit algorithm. Config key: "Algorithm".
    /// <para><b>Options:</b> <see cref="RateLimitAlgorithm.FixedWindow"/> (default), <see cref="RateLimitAlgorithm.SlidingWindow"/>, <see cref="RateLimitAlgorithm.TokenBucket"/>.</para>
    /// <para><b>Effect:</b> FixedWindow = X permits per window; SlidingWindow = smoother, avoids boundary spikes; TokenBucket = sustained average rate with burst capacity.</para>
    /// </summary>
    [JsonPropertyName("Algorithm")]
    public RateLimitAlgorithm Algorithm { get; set; } = RateLimitAlgorithm.FixedWindow;

    /// <summary>
    /// Number of segments the window is split into for the SlidingWindow rate limiter only. Default: 2.
    /// </summary>
    [Range(1, 100, ErrorMessage = "SegmentsPerWindow must be between 1 and 100.")]
    public int SegmentsPerWindow { get; set; } = 2;

    /// <summary>
    /// Maximum tokens in the bucket for the TokenBucket rate limiter only. Default: 1000.
    /// </summary>
    [Range(1, 100_000, ErrorMessage = "TokenBucketCapacity must be between 1 and 100000.")]
    public int TokenBucketCapacity { get; set; } = 1000;

    /// <summary>
    /// Tokens added each replenishment period for the TokenBucket rate limiter only. Default: 1000.
    /// </summary>
    [Range(1, 100_000, ErrorMessage = "TokensPerPeriod must be between 1 and 100000.")]
    public int TokensPerPeriod { get; set; } = 1000;

    /// <summary>
    /// Interval (seconds) between token replenishments for the TokenBucket rate limiter only. Default: 1.
    /// </summary>
    [Range(1, 3600, ErrorMessage = "ReplenishmentPeriodSeconds must be between 1 and 3600.")]
    public int ReplenishmentPeriodSeconds { get; set; } = 1;
}
