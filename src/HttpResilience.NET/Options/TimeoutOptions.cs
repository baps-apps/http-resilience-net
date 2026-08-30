namespace HttpResilience.NET.Options;

/// <summary>
/// Timeout budgets for a logical HTTP request.
/// </summary>
/// <remarks>
/// Four bounds, outermost first. Startup validation enforces the ordering, so a nonsensical combination fails
/// the deployment rather than the first request.
/// <code>
/// CancellationToken (caller / request abort)  -- always wins, never a circuit-breaker failure
///   +- Client   (HttpClient.Timeout: queue wait + all attempts + RESPONSE BODY transfer)
///        +- [ rate limiter / concurrency queue wait -- OUTSIDE the total budget ]
///             +- Total    (all attempts plus backoff, from admission onwards)
///                  +- Attempt  (one HTTP attempt, up to response HEADERS)
///                       +- Connection:ConnectTimeout  (TCP + TLS only)
/// </code>
/// <para>
/// <b><see cref="Total"/> stops applying when response headers arrive.</b> Every resilience strategy lives in
/// the handler chain, and the chain returns as soon as the headers are in. Under the default
/// <c>HttpCompletionOption.ResponseContentRead</c> the body is buffered by
/// <see cref="System.Net.Http.HttpClient"/> afterwards, where no strategy can see it -- so
/// <see cref="Client"/> is what stops an origin holding a connection open by trickling a body. If you stream
/// large responses, request <c>HttpCompletionOption.ResponseHeadersRead</c> and impose your own deadline on
/// reading the stream rather than raising <see cref="Client"/> until it stops meaning anything.
/// </para>
/// </remarks>
/// <example>
/// A dependency that should answer in a second, with three attempts allowed:
/// <code language="json">
/// { "HttpResilience": { "Timeout": { "Total": "00:00:10", "Attempt": "00:00:03" } } }
/// </code>
/// </example>
public sealed class TimeoutOptions
{
    /// <summary>
    /// Gets or sets the budget for one logical request from the moment it is admitted, covering every attempt
    /// and all backoff delays. Must be strictly greater than <see cref="Attempt"/>. Defaults to 20 seconds.
    /// </summary>
    /// <remarks>
    /// This is the number to write an SLO against, with one caveat: time spent queued for a rate-limit permit
    /// or a concurrency slot happens <i>before</i> admission and is not included. Keep the queue limits small
    /// if the SLO has to cover queueing, or measure from the caller.
    /// </remarks>
    public TimeSpan Total { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Gets or sets the budget for a single HTTP attempt, up to response headers. Must be strictly less than
    /// <see cref="Total"/>. Defaults to 5 seconds.
    /// </summary>
    /// <remarks>
    /// An attempt that exceeds this is treated as a transient failure, so it is retried and it counts towards
    /// the circuit breaker. Set it to what a healthy response actually takes plus headroom -- too low and you
    /// manufacture failures, too high and a hung dependency holds your capacity for the full total budget.
    /// </remarks>
    public TimeSpan Attempt { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or sets <see cref="System.Net.Http.HttpClient.Timeout"/>: the outer backstop covering limiter
    /// queue wait, every attempt, and the response-body transfer that <see cref="Total"/> cannot reach. Must
    /// be strictly greater than <see cref="Total"/>. Defaults to <see langword="null"/>, meaning
    /// <see cref="Total"/> plus <see cref="DefaultClientAllowance"/>.
    /// </summary>
    /// <remarks>
    /// This exists to be generous and still finite, so leave it alone unless a client has a deep limiter queue
    /// or genuinely large downloads. It is not a request SLO -- <see cref="Total"/> is. A request that hits
    /// this one has either queued for a long time or stopped receiving body bytes, and both surface as a bare
    /// <see cref="TaskCanceledException"/> carrying none of the pipeline's context.
    /// </remarks>
    public TimeSpan? Client { get; set; }

    /// <summary>
    /// The allowance added to <see cref="Total"/> when <see cref="Client"/> is not set: 30 seconds.
    /// </summary>
    /// <remarks>
    /// The allowance covers limiter queue wait and the response-body transfer, and nothing else --
    /// <see cref="Total"/> already covers every attempt up to response headers. It was one minute, which is
    /// three times the whole default attempt budget for body bytes alone, inherited unstated by every client
    /// in every service that adopts this package. A default that loose is a poor fit for the only bound
    /// standing between a trickling origin and a connection, a buffer and an inbound request held open, so it
    /// is halved. It remains a backstop and not an SLO: a client that genuinely downloads large bodies should
    /// state <see cref="Client"/> itself, or -- better, and what the checklist asks for -- stream with
    /// <c>HttpCompletionOption.ResponseHeadersRead</c> and impose its own deadline on the read.
    /// </remarks>
    internal static readonly TimeSpan DefaultClientAllowance = TimeSpan.FromSeconds(30);

    /// <summary>The effective <see cref="System.Net.Http.HttpClient.Timeout"/> for this configuration.</summary>
    internal TimeSpan EffectiveClientTimeout => Client ?? Total + DefaultClientAllowance;
}
