namespace HttpResilience.NET.Options;

/// <summary>
/// How many resilience pipeline instances a single client uses.
/// </summary>
/// <remarks>
/// Only worth changing when one client calls several hosts and you do not want one sick host to open the
/// circuit for the others -- a partner API with regional endpoints, say. A client that talks to one host
/// gains nothing from <see cref="ByAuthority"/>.
/// </remarks>
/// <example>
/// <code language="json">
/// {
///   "HttpResilience": {
///     "Clients": {
///       "Partner": {
///         "PipelineSelection": {
///           "Mode": "ByAuthority",
///           "Authorities": [ "https://eu.partner.example", "https://us.partner.example" ]
///         }
///       }
///     }
///   }
/// }
/// </code>
/// </example>
public enum PipelineSelectionMode
{
    /// <summary>
    /// One pipeline for the client, shared by every request it makes. The default, and correct unless the
    /// client's hosts have independent health.
    /// </summary>
    None = 0,

    /// <summary>
    /// A separate pipeline per authority, so <b>circuit breakers</b> are isolated per host: one failing host
    /// stops being called while the others keep working.
    /// </summary>
    /// <remarks>
    /// Requires <see cref="PipelineSelectionOptions.Authorities"/>, which fixes the number of pipelines at
    /// deploy time. Anything not listed shares one pipeline.
    /// <para>
    /// Two things do not partition the way you might expect. The <b>rate limiter</b> stays per client and is
    /// shared by every authority, because a permit budget is a statement about a downstream quota rather than
    /// about one host's health. The <b>concurrency backstop</b> does partition, because it lives inside each
    /// pipeline -- so a client with N listed authorities is bounded at
    /// <c>(N + 1) x ConcurrencyLimiter:Backstop</c> in-flight requests, counting the shared pipeline. Size it
    /// as a per-authority cap under this mode.
    /// </para>
    /// </remarks>
    ByAuthority = 1
}
