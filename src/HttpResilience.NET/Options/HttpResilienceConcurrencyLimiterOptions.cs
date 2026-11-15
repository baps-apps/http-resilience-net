namespace HttpResilience.NET;

/// <summary>
/// A cap on how many requests this client may have in flight, plus the backstop that always applies.
/// </summary>
/// <remarks>
/// A bulkhead: it stops one slow dependency from consuming all of your service's capacity. One slot covers a
/// logical request including its retries.
/// </remarks>
/// <example>
/// <code language="json">
/// {
///   "HttpResilience": {
///     "Clients": {
///       "Reports": {
///         "ConcurrencyLimiter": { "Enabled": true, "Limit": 20, "QueueLimit": 50 }
///       }
///     }
///   }
/// }
/// </code>
/// </example>
public sealed class HttpResilienceConcurrencyLimiterOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether the concurrency cap is applied. Defaults to
    /// <see langword="false"/>.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the maximum logical requests in flight. Required when <see cref="Enabled"/> is
    /// <see langword="true"/>; must not exceed <see cref="Backstop"/>.
    /// </summary>
    /// <remarks>
    /// Requests over the cap wait in a queue of <see cref="QueueLimit"/>, then fail with
    /// <c>RateLimiterRejectedException</c>.
    /// </remarks>
    public int? Limit { get; set; }

    /// <summary>
    /// Gets or sets how many requests may wait for a slot. Defaults to 0 (fail fast). Maximum 1,000.
    /// </summary>
    /// <remarks>
    /// Queue wait is outside <see cref="HttpResilienceTimeoutOptions.Total"/>; only <see cref="HttpResilienceTimeoutOptions.Client"/> and
    /// the caller's cancellation token bound it.
    /// </remarks>
    public int QueueLimit { get; set; }

    /// <summary>
    /// Gets or sets the concurrency cap that always applies, even when <see cref="Enabled"/> is
    /// <see langword="false"/>. Must be between 1 and 10,000. Defaults to 1,000.
    /// </summary>
    /// <remarks>
    /// The platform's resilience handler always carries this limiter, with no queue. Beyond it, requests fail
    /// with <c>RateLimiterRejectedException</c> and log event 7.
    /// <para>
    /// It applies per pipeline: per authority for hedged clients and for
    /// <see cref="HttpResiliencePipelineSelectionMode.ByAuthority"/>, otherwise per client.
    /// </para>
    /// </remarks>
    public int Backstop { get; set; } = 1000;
}
