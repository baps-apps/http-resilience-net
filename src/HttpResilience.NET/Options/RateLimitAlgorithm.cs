namespace HttpResilience.NET.Options;

/// <summary>
/// The rate-limiting algorithm, backed by the <c>System.Threading.RateLimiting</c> types in the BCL.
/// </summary>
/// <remarks>
/// Pick by the shape of the quota you are respecting. A downstream that says "1,000 requests per minute" is a
/// window; one that says "sustained 100/s, bursts to 500" is a bucket.
/// </remarks>
/// <example>
/// A downstream quota of 1,000 requests per minute, shared by 10 replicas, so 100 per replica:
/// <code language="json">
/// {
///   "HttpResilience": {
///     "RateLimiter": {
///       "Enabled": true,
///       "Algorithm": "SlidingWindow",
///       "PermitLimit": 100,
///       "Window": "00:01:00",
///       "SegmentsPerWindow": 6
///     }
///   }
/// }
/// </code>
/// </example>
public enum RateLimitAlgorithm
{
    /// <summary>
    /// A fixed number of permits per window, reset all at once. Cheapest and easiest to reason about, but a
    /// client can spend a full window's budget at the end of one window and again at the start of the next --
    /// so the instantaneous rate can be double what you configured. Fine when the downstream quota has
    /// headroom.
    /// </summary>
    FixedWindow = 0,

    /// <summary>
    /// A fixed window divided into <see cref="RateLimiterOptions.SegmentsPerWindow"/> segments, which expire
    /// individually. Removes the boundary burst above at a small memory cost. Use this when the downstream
    /// enforces its quota strictly.
    /// </summary>
    SlidingWindow = 1,

    /// <summary>
    /// Tokens replenish at a steady rate up to a capacity, so a client that has been idle may burst up to
    /// <see cref="RateLimiterOptions.TokenLimit"/> and then settles to
    /// <see cref="RateLimiterOptions.TokensPerPeriod"/>. Use this for bursty callers -- a batch job, a
    /// cache-fill on startup -- against a downstream that tolerates a burst.
    /// </summary>
    TokenBucket = 2
}
