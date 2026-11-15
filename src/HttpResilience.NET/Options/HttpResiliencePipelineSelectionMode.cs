namespace HttpResilience.NET;

/// <summary>
/// How many resilience pipeline instances a single client uses.
/// </summary>
/// <remarks>
/// Change it only when one client calls several hosts with independent health.
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
public enum HttpResiliencePipelineSelectionMode
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
    /// Requires <see cref="HttpResiliencePipelineSelectionOptions.Authorities"/>; unlisted authorities share one pipeline.
    /// The rate limiter stays per client. The concurrency backstop applies per pipeline, so N listed
    /// authorities allow up to <c>(N + 1) x ConcurrencyLimiter:Backstop</c> requests in flight.
    /// </remarks>
    ByAuthority = 1
}
