namespace HttpResilience.NET;

/// <summary>
/// Hedging: start a second copy of a slow request and use whichever answers first. Used only by clients
/// registered with <c>AddHttpResilienceHedgingHandler</c>.
/// </summary>
/// <remarks>
/// Use it for tail latency on read-heavy calls to a dependency with spare capacity. Every hedged attempt is a
/// real request, so do not use it on a struggling dependency.
/// <para>
/// The hedging pipeline is: total timeout, hedging, then per-authority concurrency limiter, circuit breaker
/// and attempt timeout. It has no retry strategy, so <c>Retry</c> keys on a hedged client fail startup. A
/// hedged client must list its <see cref="HttpResiliencePipelineSelectionOptions.Authorities"/>.
/// </para>
/// </remarks>
/// <example>
/// <code language="csharp">
/// builder.Services.AddHttpClient("Search").AddHttpResilienceHedgingHandler();
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
public sealed class HttpResilienceHedgingOptions
{
    /// <summary>
    /// Gets or sets how long to wait for an attempt before starting the next one. Defaults to 2 seconds.
    /// </summary>
    /// <remarks>
    /// Around the dependency's p95 is a good starting point. <see cref="TimeSpan.Zero"/> sends every attempt at
    /// once and multiplies traffic for every request.
    /// </remarks>
    public TimeSpan Delay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Gets or sets the hedged attempts beyond the first, so the origin sees at most
    /// <c>1 + MaxHedgedAttempts</c> requests. Must be between 1 and 10. Defaults to 1.
    /// </summary>
    public int MaxHedgedAttempts { get; set; } = 1;

    /// <summary>
    /// Gets or sets a value indicating whether hedging is limited to the RFC 9110 safe methods: GET, HEAD,
    /// OPTIONS and TRACE. Defaults to <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// An allow-list: any other method, recognised or not, is never hedged. Hedged attempts run at the same
    /// time, so an idempotency key on the origin may not prevent duplicates.
    /// <list type="bullet">
    /// <item><description>It cannot be <see langword="false"/> in the root section.</description></item>
    /// <item><description>A client with it <see langword="false"/> logs a startup warning (event 10).</description></item>
    /// </list>
    /// </remarks>
    public bool DisableForUnsafeHttpMethods { get; set; } = true;
}
