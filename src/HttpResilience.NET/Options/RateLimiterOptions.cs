namespace HttpResilience.NET.Options;

/// <summary>
/// Outbound rate limiting: how fast this client may call a downstream. Off by default.
/// </summary>
/// <remarks>
/// Reach for this when a downstream publishes a quota you must not exceed, or when you are the noisy neighbour
/// on a shared dependency. It answers a different question from <see cref="ConcurrencyLimiterOptions"/>, which
/// bounds how much of <i>your</i> capacity may wait on one dependency.
/// <para>
/// <b>This limiter is process-local and cannot enforce a cluster-wide quota.</b> Each replica gets its own,
/// so the fleet-wide rate is <c>replicas x PermitLimit</c> per window: 10 pods at 100/s permit 1,000/s in
/// aggregate. There is a second multiplier -- the budget belongs to a <i>named client</i>, not to a
/// downstream, so two clients calling the same host hold two independent budgets. Size
/// <see cref="PermitLimit"/> as <c>quota / (replicas x clients that reach the host)</c>, or enforce the real
/// quota at a gateway where it can actually be global.
/// </para>
/// <para>
/// One permit covers one <b>logical request</b> including its retries or hedged attempts, so a retrying client
/// puts up to <c>PermitLimit x (1 + Retry:MaxRetries)</c> requests on the wire per window. Size the budget
/// with that multiplier in mind.
/// </para>
/// <para>
/// Enabling this takes the platform handler's one limiter slot, which otherwise holds the concurrency
/// backstop. The backstop is not lost: it is re-applied outside the rate limiter.
/// </para>
/// </remarks>
/// <example>
/// A downstream quota of 600 requests per minute across 6 replicas, one client:
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
public sealed class RateLimiterOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether outbound rate limiting is applied. Defaults to
    /// <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// Cannot be switched on after the client is registered -- it decides whether a limiter is built at all,
    /// so a later change fails startup. Put it in configuration or in the <c>configure</c> parameter.
    /// </remarks>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the algorithm. Defaults to <see cref="RateLimitAlgorithm.FixedWindow"/>.
    /// </summary>
    public RateLimitAlgorithm Algorithm { get; set; } = RateLimitAlgorithm.FixedWindow;

    /// <summary>
    /// Gets or sets the permits allowed per <see cref="Window"/>, for
    /// <see cref="RateLimitAlgorithm.FixedWindow"/> and <see cref="RateLimitAlgorithm.SlidingWindow"/>.
    /// Required when <see cref="Enabled"/> is <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// No default, deliberately: this is a capacity contract with a specific downstream and no shared package
    /// can guess it. Exceeding it throws <c>RateLimiterRejectedException</c> to the caller.
    /// </remarks>
    public int? PermitLimit { get; set; }

    /// <summary>
    /// Gets or sets the window length for <see cref="RateLimitAlgorithm.FixedWindow"/> and
    /// <see cref="RateLimitAlgorithm.SlidingWindow"/>. Defaults to 1 second.
    /// </summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets or sets the number of segments a sliding window is divided into. Defaults to 8. Higher is
    /// smoother and costs a little more memory.
    /// </summary>
    public int SegmentsPerWindow { get; set; } = 8;

    /// <summary>
    /// Gets or sets the bucket capacity for <see cref="RateLimitAlgorithm.TokenBucket"/> -- the largest burst
    /// an idle client may make. Required when that algorithm is selected.
    /// </summary>
    public int? TokenLimit { get; set; }

    /// <summary>
    /// Gets or sets the tokens added each <see cref="ReplenishmentPeriod"/> for
    /// <see cref="RateLimitAlgorithm.TokenBucket"/> -- the sustained rate, as opposed to the burst.
    /// Required when that algorithm is selected.
    /// </summary>
    public int? TokensPerPeriod { get; set; }

    /// <summary>
    /// Gets or sets how often tokens are replenished for <see cref="RateLimitAlgorithm.TokenBucket"/>.
    /// Defaults to 1 second.
    /// </summary>
    public TimeSpan ReplenishmentPeriod { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets or sets how many requests may wait for a permit. Defaults to 0, which fails fast. Capped at
    /// 1,000.
    /// </summary>
    /// <remarks>
    /// Keep this small, or leave it at 0. A queued request holds its
    /// <see cref="System.Net.Http.HttpRequestMessage"/> and content buffer in memory while it waits, so a deep
    /// queue of large uploads is a memory risk as well as a latency one -- and the wait happens outside
    /// <see cref="TimeoutOptions.Total"/>, where no pipeline timeout can bound it. A persistently full queue
    /// means the downstream needs more capacity or you need to shed load; it does not mean the queue should be
    /// longer.
    /// </remarks>
    public int QueueLimit { get; set; }

    /// <summary>
    /// The configuration key that sizes this limiter's budget, for a rejection message that names the number
    /// an operator would change rather than the exception type they already have.
    /// </summary>
    internal string PermitKey => Algorithm is RateLimitAlgorithm.TokenBucket
        ? "RateLimiter:TokensPerPeriod"
        : "RateLimiter:PermitLimit";
}
