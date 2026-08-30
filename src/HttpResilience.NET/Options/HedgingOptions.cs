namespace HttpResilience.NET.Options;

/// <summary>
/// Hedging: race a second copy of a slow request and take whichever answers first. Used only by clients
/// registered with <c>AddHedgedHttpResilience</c>.
/// </summary>
/// <remarks>
/// Reach for this when tail latency matters more than outbound load -- a read-heavy search or lookup where
/// p99 is the problem and the dependency has spare capacity. Do not reach for it to make a struggling
/// dependency faster: every hedged attempt is a real request, so a hedged client multiplies load on exactly
/// the dependency least able to take it.
/// <para>
/// Hedging is never selected by configuration alone. It is chosen in code, on the line that registers the
/// client, so the decision is visible in review.
/// </para>
/// <para>
/// The hedging pipeline's shape differs from the standard one: total timeout, hedging, then a
/// <b>per-authority</b> concurrency limiter, circuit breaker and attempt timeout. It has <b>no retry
/// strategy</b>, so <c>Retry:*</c> keys on a hedged client fail startup rather than binding silently. Those
/// per-authority pipelines are never evicted, which is why a hedged client must list its
/// <see cref="PipelineSelectionOptions.Authorities"/>.
/// </para>
/// </remarks>
/// <example>
/// <code language="csharp">
/// builder.Services.AddHttpClient("Search").AddHedgedHttpResilience();
/// </code>
/// <code language="json">
/// {
///   "HttpResilience": {
///     "Clients": {
///       "Search": {
///         "Hedging": { "Delay": "00:00:00.300", "MaxHedgedAttempts": 1 },
///         "PipelineSelection": { "Authorities": [ "https://search.internal" ] }
///       }
///     }
///   }
/// }
/// </code>
/// </example>
public sealed class HedgingOptions
{
    /// <summary>
    /// Gets or sets how long to wait for the primary attempt before starting a hedged one. Defaults to
    /// 2 seconds.
    /// </summary>
    /// <remarks>
    /// Set it near the latency you are willing to accept -- typically around the dependency's p95, so only
    /// genuinely slow requests are hedged and the extra load stays small. <see cref="TimeSpan.Zero"/> issues
    /// every attempt at once, which doubles outbound traffic for every request and should be reserved for
    /// cases where that has been budgeted for.
    /// </remarks>
    public TimeSpan Delay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Gets or sets the number of hedged attempts beyond the primary one, so the origin sees at most
    /// <c>1 + MaxHedgedAttempts</c> requests. Must be between 1 and 10. Defaults to 1.
    /// </summary>
    /// <remarks>
    /// This directly multiplies outbound load. 1 is almost always the right answer.
    /// </remarks>
    public int MaxHedgedAttempts { get; set; } = 1;

    /// <summary>
    /// Gets or sets a value indicating whether hedging is restricted to the four methods RFC 9110 defines as
    /// safe -- GET, HEAD, OPTIONS and TRACE. Defaults to <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// <b>An allow-list, not a deny-list of the five familiar mutating verbs.</b> Anything outside the safe
    /// four is never hedged, recognised or not, so a WebDAV <c>MOVE</c> or a cache <c>PURGE</c> is excluded
    /// along with POST. There is no per-method opt-in for hedging, unlike retries.
    /// <para>
    /// Hedged attempts are <i>simultaneous</i>, so unlike retries they give an origin's idempotency key no
    /// serialization to rely on. Switching this off means accepting that two identical mutating requests may
    /// arrive at the same instant.
    /// </para>
    /// <para>
    /// It cannot be <see langword="false"/> in the <b>root</b> section -- startup fails. Every client inherits
    /// the root, so one key there would decide it for every hedged client in the process, and this is the more
    /// dangerous of the two guards rather than the less. State it under the one client that needs it.
    /// </para>
    /// <para>
    /// A hedged client with this <see langword="false"/> logs one <c>Warning</c> at host start naming the
    /// client and this key.
    /// </para>
    /// </remarks>
    public bool DisableForUnsafeHttpMethods { get; set; } = true;
}
