namespace HttpResilience.NET.Options;

/// <summary>
/// Whether one client uses a single resilience pipeline or one per authority, and which authorities it may
/// reach.
/// </summary>
/// <remarks>
/// Useful when one client calls several hosts whose health is independent, so one sick host does not open the
/// circuit for the others. A client that talks to a single host needs none of this.
/// <para>
/// The <see cref="Authorities"/> allow-list is required rather than optional, because every distinct authority
/// permanently allocates a pipeline, a circuit breaker and a metric series, and nothing evicts them. Where a
/// target host can be influenced by request data -- a tenant-configured webhook, a stored callback URL -- an
/// unbounded set is a memory-exhaustion path. With the list, the number of pipelines is fixed at deploy time.
/// </para>
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
public sealed class PipelineSelectionOptions
{
    /// <summary>
    /// Gets or sets the selection mode. Defaults to <see cref="PipelineSelectionMode.None"/>.
    /// </summary>
    /// <remarks>
    /// Cannot be changed after the client is registered -- it decides whether pipeline selection is wired at
    /// all, so a later change fails startup.
    /// </remarks>
    public PipelineSelectionMode Mode { get; set; } = PipelineSelectionMode.None;

    /// <summary>
    /// Gets or sets the authorities that get their own pipeline, as <c>scheme://host</c> or
    /// <c>scheme://host:port</c>. Required when <see cref="Mode"/> is
    /// <see cref="PipelineSelectionMode.ByAuthority"/>, and for every client registered with
    /// <c>AddHedgedHttpResilience</c>.
    /// </summary>
    /// <remarks>
    /// On a <b>standard</b> client this bounds how many pipelines can exist; requests to an unlisted authority
    /// are allowed and share one pipeline. On a <b>hedged</b> client it also bounds destinations: a request to
    /// an unlisted authority is rejected with <see cref="System.Net.Http.HttpRequestException"/> before it
    /// reaches the wire.
    /// <para>
    /// <b>It does not bound where a redirect goes.</b> A 3xx is resolved inside
    /// <see cref="System.Net.Http.SocketsHttpHandler"/>, below every handler in the chain, so a redirect from
    /// a listed authority to an unlisted one is followed and never seen here. Use
    /// <see cref="ConnectionOptions.AllowAutoRedirect"/> for that -- a hedged client already resolves it to
    /// <see langword="false"/> for exactly this reason.
    /// </para>
    /// <para>
    /// Hosts are matched on <see cref="System.Uri.IdnHost"/> with any trailing root label removed, so an
    /// internationalised authority matches whether a request spells it in Unicode or punycode, and
    /// <c>orders.internal</c> matches <c>orders.internal.</c>. Scheme and port must match exactly.
    /// </para>
    /// <para>
    /// A client section <b>replaces</b> this list rather than adding to the root's. A client that states no
    /// list of its own inherits the root's.
    /// </para>
    /// </remarks>
    public List<string>? Authorities { get; set; }
}
