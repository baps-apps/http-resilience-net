namespace HttpResilience.NET;

/// <summary>
/// Outbound rate limiting: how fast this client may call a downstream. Off by default.
/// </summary>
/// <remarks>
/// Use it when a downstream publishes a quota. To limit how many requests wait on a slow dependency, use
/// <see cref="HttpResilienceConcurrencyLimiterOptions"/> instead.
/// <list type="bullet">
/// <item><description><b>Per process, per client.</b> The effective rate is replicas × clients calling the host × <see cref="PermitLimit"/>. Enforce a true global quota at a gateway.</description></item>
/// <item><description>One permit covers a logical request including its retries or hedged attempts.</description></item>
/// <item><description>The concurrency backstop stays in force when this is enabled.</description></item>
/// </list>
/// </remarks>
/// <example>
/// A downstream quota of 600 requests per minute across 6 replicas:
/// <code language="json">
/// {
///   "HttpResilience": {
///     "Clients": {
///       "Partner": {
///         "RateLimiter": {
///           "Enabled": true,
///           "Algorithm": "SlidingWindow",
///           "PermitLimit": 100,
///           "Window": "00:01:00",
///           "QueueLimit": 0
///         }
///       }
///     }
///   }
/// }
/// </code>
/// </example>
public sealed class HttpResilienceRateLimiterOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether outbound rate limiting is applied. Defaults to
    /// <see langword="false"/>.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the algorithm. Defaults to <see cref="HttpResilienceRateLimitAlgorithm.FixedWindow"/>.
    /// </summary>
    public HttpResilienceRateLimitAlgorithm Algorithm { get; set; } = HttpResilienceRateLimitAlgorithm.FixedWindow;

    /// <summary>
    /// Gets or sets the permits per <see cref="Window"/> for <see cref="HttpResilienceRateLimitAlgorithm.FixedWindow"/> and
    /// <see cref="HttpResilienceRateLimitAlgorithm.SlidingWindow"/>. Required when <see cref="Enabled"/> is
    /// <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// Requests over the limit fail with <c>RateLimiterRejectedException</c>.
    /// </remarks>
    public int? PermitLimit { get; set; }

    /// <summary>
    /// Gets or sets the window length for <see cref="HttpResilienceRateLimitAlgorithm.FixedWindow"/> and
    /// <see cref="HttpResilienceRateLimitAlgorithm.SlidingWindow"/>. Defaults to 1 second.
    /// </summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets or sets the number of segments in a sliding window. Defaults to 8.
    /// </summary>
    public int SegmentsPerWindow { get; set; } = 8;

    /// <summary>
    /// Gets or sets the bucket capacity (largest burst) for <see cref="HttpResilienceRateLimitAlgorithm.TokenBucket"/>.
    /// Required for that algorithm.
    /// </summary>
    public int? TokenLimit { get; set; }

    /// <summary>
    /// Gets or sets the tokens added per <see cref="ReplenishmentPeriod"/> (sustained rate) for
    /// <see cref="HttpResilienceRateLimitAlgorithm.TokenBucket"/>. Required for that algorithm.
    /// </summary>
    public int? TokensPerPeriod { get; set; }

    /// <summary>
    /// Gets or sets how often tokens are added for <see cref="HttpResilienceRateLimitAlgorithm.TokenBucket"/>. Defaults to
    /// 1 second.
    /// </summary>
    public TimeSpan ReplenishmentPeriod { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets or sets how many requests may wait for a permit. Defaults to 0 (fail fast). Maximum 1,000.
    /// </summary>
    /// <remarks>
    /// Queue wait is outside <see cref="HttpResilienceTimeoutOptions.Total"/>, and each queued request holds its content in
    /// memory. Keep it small.
    /// </remarks>
    public int QueueLimit { get; set; }
}
