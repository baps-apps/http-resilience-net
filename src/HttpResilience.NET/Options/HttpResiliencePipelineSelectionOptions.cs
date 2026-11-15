namespace HttpResilience.NET;

/// <summary>
/// Whether a client uses one resilience pipeline or one per authority, and which authorities it may reach.
/// </summary>
/// <remarks>
/// Use per-authority pipelines when one client calls several hosts with independent health, so one failing
/// host does not open the circuit for the others. Each listed authority holds a pipeline, a circuit breaker
/// and a metric series for the life of the process, which is why the list is required and fixed at deploy
/// time.
/// </remarks>
/// <example>
/// <code language="json">
/// {
///   "HttpResilience": {
///     "Clients": {
///       "Partner": {
///         "PipelineSelection": {
///           "Mode": "ByAuthority",
///           "Authorities": [ "https://a.partner.example", "https://b.partner.example:8443" ]
///         }
///       }
///     }
///   }
/// }
/// </code>
/// </example>
public sealed class HttpResiliencePipelineSelectionOptions
{
    /// <summary>
    /// Gets or sets the selection mode. Defaults to <see cref="HttpResiliencePipelineSelectionMode.None"/>.
    /// </summary>
    public HttpResiliencePipelineSelectionMode Mode { get; set; } = HttpResiliencePipelineSelectionMode.None;

    /// <summary>
    /// Gets the authorities, as <c>scheme://host</c> or <c>scheme://host:port</c>. Required for
    /// <see cref="HttpResiliencePipelineSelectionMode.ByAuthority"/> and for every <c>AddHttpResilienceHedgingHandler</c> client.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description>Standard client: listed authorities get their own pipeline; others share one.</description></item>
    /// <item><description>Hedged client: requests to an unlisted authority fail with <see cref="InvalidOperationException"/> before reaching the network, so no retry policy treats them as transient.</description></item>
    /// <item><description>Redirects are not checked against the list; see <see cref="HttpResilienceConnectionOptions.AllowAutoRedirect"/>.</description></item>
    /// <item><description>Hosts match on <see cref="System.Uri.IdnHost"/>, ignoring a trailing dot. Scheme and port must match exactly.</description></item>
    /// <item><description>A client's list replaces the root's; a client with no list inherits it.</description></item>
    /// </list>
    /// </remarks>
    public IList<string> Authorities { get; } = new List<string>();
}
