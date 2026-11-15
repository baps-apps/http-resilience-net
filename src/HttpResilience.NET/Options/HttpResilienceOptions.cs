namespace HttpResilience.NET;

/// <summary>
/// The resilience configuration for one <see cref="System.Net.Http.HttpClient"/>, bound from the
/// <c>HttpResilience</c> section.
/// </summary>
/// <remarks>
/// Per-client overrides live at <c>HttpResilience:Clients:{name}</c> and are layered on top of the root, so a
/// client states only what it changes. Lists (<see cref="HttpResilienceRetryOptions.RetryableMethods"/>,
/// <see cref="HttpResiliencePipelineSelectionOptions.Authorities"/>) are replaced, not merged.
/// <para>
/// The pipeline order is fixed. Outermost to innermost, for a standard client:
/// </para>
/// <code>
/// ConcurrencyLimiter   (optional)   one slot per logical request
///   +- ConcurrencyBackstop          only with a rate limiter and no ConcurrencyLimiter
///        +- Limiter                 ALWAYS PRESENT: the rate limiter if enabled, else the backstop
///             +- Total timeout
///                  +- Retry
///                       +- Circuit breaker
///                            +- Attempt timeout
///                                 +- SocketsHttpHandler
/// </code>
/// <para>
/// A hedged client has a different shape: the authority allow-list outermost, and hedging instead of retry.
/// </para>
/// <para>
/// Read a client's effective values with <c>IOptionsMonitor&lt;HttpResilienceOptions&gt;.Get("Orders")</c>;
/// the pipeline is built from the same instance. Configuration is read once; changes need a restart.
/// </para>
/// </remarks>
/// <example>
/// <code language="json">
/// {
///   "HttpResilience": {
///     "Timeout": { "Total": "00:00:20", "Attempt": "00:00:05" },
///     "Clients": {
///       "Orders": { "Timeout": { "Total": "00:00:10", "Attempt": "00:00:03" } }
///     }
///   }
/// }
/// </code>
/// <code language="csharp">
/// builder.Services.AddHttpResilience(builder.Configuration);
/// builder.Services.AddHttpClient("Orders").AddHttpResilienceHandler();
/// </code>
/// </example>
public sealed class HttpResilienceOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether the resilience pipeline is applied. Defaults to
    /// <see langword="true"/>: registering the client is the opt-in.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description><c>HttpResilience:Clients:{name}:Enabled: false</c> turns one client off.</description></item>
    /// <item><description><c>HttpResilience:Enabled: false</c> at the root is a kill switch: it turns every client off, whatever the client sections or the <c>configure</c> delegate say.</description></item>
    /// <item><description>A disabled client logs a startup warning (event 6) naming the key that disabled it.</description></item>
    /// <item><description><see cref="Connection"/> settings apply either way.</description></item>
    /// </list>
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets the timeout budgets.</summary>
    public HttpResilienceTimeoutOptions Timeout { get; } = new();

    /// <summary>Gets the retry settings. Used by the standard pipeline only.</summary>
    public HttpResilienceRetryOptions Retry { get; } = new();

    /// <summary>Gets the circuit breaker thresholds. State is per process, not shared across replicas.</summary>
    public HttpResilienceCircuitBreakerOptions CircuitBreaker { get; } = new();

    /// <summary>Gets the connection-pool settings. Applied whether or not <see cref="Enabled"/> is set.</summary>
    public HttpResilienceConnectionOptions Connection { get; } = new();

    /// <summary>
    /// Gets the outbound rate limit. Per process: the fleet-wide rate is replicas × <c>PermitLimit</c>.
    /// </summary>
    public HttpResilienceRateLimiterOptions RateLimiter { get; } = new();

    /// <summary>Gets the concurrent-request cap and the always-present backstop.</summary>
    public HttpResilienceConcurrencyLimiterOptions ConcurrencyLimiter { get; } = new();

    /// <summary>Gets the hedging settings. Used only by <c>AddHttpResilienceHedgingHandler</c> clients.</summary>
    public HttpResilienceHedgingOptions Hedging { get; } = new();

    /// <summary>Gets the pipeline-per-authority settings and the authority allow-list.</summary>
    public HttpResiliencePipelineSelectionOptions PipelineSelection { get; } = new();
}
