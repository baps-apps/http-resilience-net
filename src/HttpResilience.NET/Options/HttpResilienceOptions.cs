namespace HttpResilience.NET.Options;

/// <summary>
/// The complete resilience configuration for one <see cref="System.Net.Http.HttpClient"/>, bound from the
/// <c>HttpResilience</c> section.
/// </summary>
/// <remarks>
/// Per-client overrides live at <c>HttpResilience:Clients:{name}</c> and are layered on top of the root
/// values, so a client states only what it changes. Scalars override; the two lists in this schema
/// (<see cref="RetryOptions.RetryableMethods"/> and <see cref="PipelineSelectionOptions.Authorities"/>)
/// replace rather than accumulate, so a client can narrow an inherited allow-list.
/// <para>
/// The pipeline shape is fixed and is not configurable, because ordering is where resilience pipelines go
/// wrong. Outermost to innermost:
/// </para>
/// <code>
/// ConcurrencyLimiter   (optional) -- one slot per logical request
///   +- RateLimiter     (optional) -- one permit per logical request
///        +- Total timeout
///             +- Retry
///                  +- Circuit breaker
///                       +- Attempt timeout
///                            +- SocketsHttpHandler
/// </code>
/// <para>
/// You can read this back at run time to see what a client is actually running:
/// <c>IOptionsMonitor&lt;HttpResilienceOptions&gt;.Get("Orders")</c>. The pipeline is built from that same
/// instance, so the values you read are the values in effect. Configuration is read once at startup;
/// changing it needs a restart.
/// </para>
/// </remarks>
/// <example>
/// A minimal configuration, plus one client that needs a tighter budget:
/// <code language="json">
/// {
///   "HttpResilience": {
///     "Enabled": true,
///     "Timeout": { "Total": "00:00:20", "Attempt": "00:00:05" },
///     "Clients": {
///       "Orders": { "Timeout": { "Total": "00:00:10", "Attempt": "00:00:03" } }
///     }
///   }
/// }
/// </code>
/// <code language="csharp">
/// builder.Services.AddHttpResilience(builder.Configuration);
/// builder.Services.AddHttpClient("Orders").AddHttpResilience();
/// </code>
/// </example>
public sealed class HttpResilienceOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether the resilience pipeline is applied. Defaults to
    /// <see langword="false"/>, so a client opts in.
    /// </summary>
    /// <remarks>
    /// Off by default so that adding this package to a service never changes how its clients behave until
    /// someone says so. The cost is that a forgotten key produces exactly the same run-time state as a
    /// deliberate opt-out -- so a client registered with this <see langword="false"/> logs a <b>Warning</b>
    /// naming the key at startup, before the service accepts traffic, where a deployment check will see it.
    /// <para>
    /// This governs the resilience pipeline only. <see cref="Connection"/> is applied independently, so
    /// switching resilience off during an incident does not also discard connection-pool tuning.
    /// </para>
    /// </remarks>
    public bool Enabled { get; set; }

    /// <summary>Gets the timeout budgets: total, per attempt, and the outer <c>HttpClient</c> backstop.</summary>
    public TimeoutOptions Timeout { get; } = new();

    /// <summary>Gets the retry behavior. Only RFC 9110 safe methods are retried unless you say otherwise.</summary>
    public RetryOptions Retry { get; } = new();

    /// <summary>Gets the circuit breaker thresholds. Process-local: every replica keeps its own state.</summary>
    public CircuitBreakerOptions CircuitBreaker { get; } = new();

    /// <summary>
    /// Gets the connection-pool settings for the primary handler. Applied whether or not
    /// <see cref="Enabled"/> is set.
    /// </summary>
    public ConnectionOptions Connection { get; } = new();

    /// <summary>
    /// Gets outbound rate limiting -- how fast this client may call a downstream. Process-local, so the
    /// fleet-wide rate is <c>replicas x clients x PermitLimit</c>.
    /// </summary>
    public RateLimiterOptions RateLimiter { get; } = new();

    /// <summary>
    /// Gets the cap on concurrent in-flight requests, plus the backstop the platform always applies.
    /// </summary>
    public ConcurrencyLimiterOptions ConcurrencyLimiter { get; } = new();

    /// <summary>
    /// Gets hedging behavior. Applies only to clients registered with <c>AddHedgedHttpResilience</c>;
    /// ignored otherwise.
    /// </summary>
    public HedgingOptions Hedging { get; } = new();

    /// <summary>
    /// Gets whether the client uses one pipeline or one per authority, and which authorities it may reach.
    /// </summary>
    public PipelineSelectionOptions PipelineSelection { get; } = new();
}
