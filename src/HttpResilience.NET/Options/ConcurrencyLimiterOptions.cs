namespace HttpResilience.NET.Options;

/// <summary>
/// A cap on how many requests this client may have in flight at once, plus the backstop the platform always
/// applies.
/// </summary>
/// <remarks>
/// Answers a different question from <see cref="RateLimiterOptions"/>: this bounds how much of <i>your</i>
/// capacity may be spent waiting on one dependency, rather than how fast that dependency may be called. Reach
/// for it when a slow dependency could otherwise consume every thread and connection you have and take the
/// whole service down with it -- the bulkhead pattern.
/// <para>
/// One slot covers a whole logical request including its retries, so a retrying request can never be rejected
/// by its own cap.
/// </para>
/// </remarks>
/// <example>
/// Allowing 20 concurrent calls to a slow dependency, with a short queue:
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
public sealed class ConcurrencyLimiterOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether the concurrency cap is applied. Defaults to
    /// <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// Cannot be switched on after the client is registered -- it decides whether a handler is added at all,
    /// so a later change fails startup. Put it in configuration or in the <c>configure</c> parameter.
    /// </remarks>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of logical requests in flight at once. Required when
    /// <see cref="Enabled"/> is <see langword="true"/>, and must be at most <see cref="Backstop"/>.
    /// </summary>
    /// <remarks>
    /// No default: how much of your own capacity may wait on one dependency is a decision about your service,
    /// which no shared package can guess. Requests over the cap are queued up to <see cref="QueueLimit"/> and
    /// then rejected with <c>RateLimiterRejectedException</c>.
    /// </remarks>
    public int? Limit { get; set; }

    /// <summary>
    /// Gets or sets how many requests may wait for a slot. Defaults to 0, which fails fast. Capped at 1,000.
    /// </summary>
    /// <remarks>
    /// The wait happens outside <see cref="TimeoutOptions.Total"/>, so a queued request can take longer than
    /// the total budget and only <see cref="TimeoutOptions.Client"/> and the caller's
    /// <see cref="System.Threading.CancellationToken"/> bound it. Each queued request also holds its content
    /// buffer in memory.
    /// </remarks>
    public int QueueLimit { get; set; }

    /// <summary>
    /// Gets or sets the concurrency cap the platform's resilience handler always applies, whether or not
    /// <see cref="Enabled"/> is <see langword="true"/>. Must be at least 1. Defaults to 1,000.
    /// </summary>
    /// <remarks>
    /// You are not adding this control -- it is already there. The standard resilience handler carries one
    /// limiter slot that is never empty, and left implicit its 1,000-concurrent cap is a scaling cliff nobody
    /// can see: above it, requests fail with <c>RateLimiterRejectedException</c> naming a rate limiter you
    /// never enabled, and there is no queue. It is surfaced here so the number can be read, alerted on and
    /// raised.
    /// <para>
    /// It bounds <see cref="Limit"/> rather than adding to it, and startup validation rejects a
    /// <see cref="Limit"/> above it.
    /// </para>
    /// <para>
    /// <b>This is one limiter per pipeline, not per client.</b> A hedged client gets this cap per authority,
    /// and so does a standard client under <see cref="PipelineSelectionMode.ByAuthority"/> -- so a client with
    /// N listed authorities is bounded at <c>(N + 1) x Backstop</c> in-flight requests, counting the shared
    /// pipeline. <see cref="RateLimiterOptions"/> does not multiply this way; its limiter is one instance per
    /// client.
    /// </para>
    /// </remarks>
    public int Backstop { get; set; } = 1000;
}
