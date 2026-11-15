namespace HttpResilience.NET;

/// <summary>
/// Retry behavior for transient failures. Only GET, HEAD, OPTIONS and TRACE are retried unless you name
/// other methods in <see cref="RetryableMethods"/>.
/// </summary>
/// <remarks>
/// Retries multiply outbound traffic in every replica: at 100 requests per second and the default
/// <see cref="MaxRetries"/> of 2, a failing dependency receives up to 300 per second from each pod. See
/// <c>docs/OPERATIONS.md</c> before raising it.
/// </remarks>
/// <example>
/// The defaults, stated explicitly:
/// <code language="json">
/// {
///   "HttpResilience": {
///     "Retry": {
///       "Enabled": true,
///       "MaxRetries": 2,
///       "BaseDelay": "00:00:00.500",
///       "BackoffType": "Exponential",
///       "MaxDelay": "00:00:05",
///       "UseJitter": true
///     }
///   }
/// }
/// </code>
/// </example>
public sealed class HttpResilienceRetryOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether failed attempts are retried. Defaults to
    /// <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// Set to <see langword="false"/> to keep timeouts and the circuit breaker without retries, for example
    /// while a dependency is overloaded. <see cref="MaxRetries"/> cannot be 0; use this instead.
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the number of retries after the first attempt, so the origin sees at most
    /// <c>1 + MaxRetries</c> requests. Must be between 1 and 10. Defaults to 2.
    /// </summary>
    /// <remarks>
    /// A startup warning names the client when the worst-case schedule does not fit in
    /// <see cref="HttpResilienceTimeoutOptions.Total"/>, because later retries would then be cut short.
    /// </remarks>
    public int MaxRetries { get; set; } = 2;

    /// <summary>
    /// Gets or sets the base delay that <see cref="BackoffType"/> scales. Defaults to 500 milliseconds.
    /// </summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Gets or sets how the delay grows between attempts. Defaults to
    /// <see cref="HttpResilienceRetryBackoffType.Exponential"/>.
    /// </summary>
    public HttpResilienceRetryBackoffType BackoffType { get; set; } = HttpResilienceRetryBackoffType.Exponential;

    /// <summary>
    /// Gets or sets a value indicating whether the delay is randomised. Defaults to <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// Keep this on. Without jitter, every replica retries on the same schedule and the retries reach the
    /// dependency as one synchronised wave.
    /// </remarks>
    public bool UseJitter { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether a <c>Retry-After</c> response header overrides the computed
    /// delay. Defaults to <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// A value up to <see cref="MaxDelay"/> is waited for. A longer one ends the retries and returns the
    /// origin's response to the caller at once: retrying sooner than the origin asked would most likely be
    /// refused again, and waiting longer would hold the caller and its limiter permit.
    /// </remarks>
    public bool UseRetryAfterHeader { get; set; } = true;

    /// <summary>
    /// Gets or sets the longest wait before a retry. Must be at least <see cref="BaseDelay"/>, greater than zero
    /// and at most one day. Defaults to 5 seconds.
    /// </summary>
    /// <remarks>
    /// A computed backoff longer than this is cut to it. A <c>Retry-After</c> longer than this ends the retries
    /// (see <see cref="UseRetryAfterHeader"/>). Raise it for a dependency that asks for longer waits, together
    /// with <see cref="HttpResilienceTimeoutOptions.Total"/>, which still bounds the whole request.
    /// </remarks>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or sets a value indicating whether retries are limited to the RFC 9110 safe methods: GET, HEAD,
    /// OPTIONS and TRACE. Defaults to <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// This is an allow-list. Unlike the platform setting of the same name, any method outside the four safe
    /// ones is never retried, including unfamiliar ones such as <c>PURGE</c> or <c>MOVE</c>.
    /// <para>
    /// Prefer <see cref="RetryableMethods"/> to switching this off. Rules enforced at startup:
    /// </para>
    /// <list type="bullet">
    /// <item><description>It cannot be <see langword="false"/> in the root section, because every client inherits the root.</description></item>
    /// <item><description>A client cannot state it beside a non-empty <see cref="RetryableMethods"/>, which replaces it.</description></item>
    /// <item><description>A client with it <see langword="false"/> logs a startup warning (event 10).</description></item>
    /// </list>
    /// </remarks>
    public bool DisableForUnsafeHttpMethods { get; set; } = true;

    /// <summary>
    /// Gets the HTTP methods that may be retried. When non-empty, only these are retried and
    /// <see cref="DisableForUnsafeHttpMethods"/> is ignored. Empty by default.
    /// </summary>
    /// <remarks>
    /// Use this to retry a non-idempotent method on an endpoint that deduplicates on an idempotency key.
    /// <list type="bullet">
    /// <item><description>A client's list replaces the root's; a client with no list inherits it.</description></item>
    /// <item><description>Empty means the default safe-method guard. A client states an empty list to drop an inherited one. It does not disable retries; use <see cref="Enabled"/>.</description></item>
    /// <item><description>The root list may name only safe methods. Unsafe methods belong under <c>HttpResilience:Clients:{name}</c>.</description></item>
    /// <item><description>Entries are upper-cased, so <c>post</c> means <c>POST</c>. Request methods are matched exactly, as RFC 9110 makes them case-sensitive: a request sent as <c>post</c> is not opted in.</description></item>
    /// <item><description>Each client that may retry an unsafe method logs a startup warning (event 10).</description></item>
    /// </list>
    /// <para>
    /// A retry re-sends the same request, so its content must be replayable. Buffered content
    /// (<c>StringContent</c>, <c>ByteArrayContent</c>, <c>JsonContent</c>) is fine. A <c>StreamContent</c>
    /// over a non-seekable stream is delivered once and the retry throws <see cref="HttpRequestException"/>,
    /// although the origin already received the request. Call <c>LoadIntoBufferAsync()</c> first.
    /// </para>
    /// </remarks>
    /// <example>
    /// Retrying POST for one client whose endpoint honours an idempotency key:
    /// <code language="json">
    /// {
    ///   "HttpResilience": {
    ///     "Clients": {
    ///       "Payments": { "Retry": { "RetryableMethods": [ "GET", "POST" ] } }
    ///     }
    ///   }
    /// }
    /// </code>
    /// </example>
    public IList<string> RetryableMethods { get; } = new List<string>();
}
