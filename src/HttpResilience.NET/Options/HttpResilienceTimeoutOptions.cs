namespace HttpResilience.NET;

/// <summary>
/// Timeout budgets for a logical HTTP request.
/// </summary>
/// <remarks>
/// Outermost first. Startup validation enforces the ordering.
/// <code>
/// CancellationToken (caller)            -- always wins; not counted as a circuit-breaker failure
///   +- Client   (HttpClient.Timeout: limiter queue wait + all attempts + response body)
///        +- [ rate limiter / concurrency limiter queue wait -- outside Total ]
///             +- Total    (all attempts and backoff, up to response headers)
///                  +- Attempt  (one attempt, up to response headers)
///                       +- Connection:ConnectTimeout  (TCP + TLS)
/// </code>
/// <para>
/// <see cref="Total"/> and <see cref="Attempt"/> end when response headers arrive. The body is read by
/// <see cref="System.Net.Http.HttpClient"/> afterwards and is bounded only by <see cref="Client"/>. For large
/// or streamed responses, use <c>HttpCompletionOption.ResponseHeadersRead</c> and apply your own read
/// deadline.
/// </para>
/// </remarks>
/// <example>
/// <code language="json">
/// { "HttpResilience": { "Timeout": { "Total": "00:00:10", "Attempt": "00:00:03" } } }
/// </code>
/// </example>
public sealed class HttpResilienceTimeoutOptions
{
    /// <summary>
    /// Gets or sets the budget for one logical request, covering every attempt and backoff delay. Must be
    /// greater than <see cref="Attempt"/>. Defaults to 20 seconds.
    /// </summary>
    /// <remarks>
    /// Time spent queued for a rate-limit permit or concurrency slot is not included.
    /// </remarks>
    public TimeSpan Total { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Gets or sets the budget for a single attempt, up to response headers. Must be less than
    /// <see cref="Total"/>. Defaults to 5 seconds.
    /// </summary>
    /// <remarks>
    /// An attempt that times out is retried and counts as a circuit-breaker failure. Set it to a healthy
    /// response time plus headroom.
    /// </remarks>
    public TimeSpan Attempt { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or sets <see cref="System.Net.Http.HttpClient.Timeout"/>, the outer bound covering limiter queue
    /// wait, every attempt and the response body. Must be greater than <see cref="Total"/>. Defaults to
    /// <see langword="null"/>, meaning <see cref="Total"/> plus 30 seconds.
    /// </summary>
    /// <remarks>
    /// Set it only for a client with a deep limiter queue or large downloads. When it expires the caller sees
    /// a <see cref="TaskCanceledException"/>. Do not also assign <c>HttpClient.Timeout</c> in code; a
    /// conflicting value fails client creation.
    /// </remarks>
    public TimeSpan? Client { get; set; }
}
