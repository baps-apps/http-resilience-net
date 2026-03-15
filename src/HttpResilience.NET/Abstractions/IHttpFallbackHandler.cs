namespace HttpResilience.NET.Abstractions;

/// <summary>
/// Optional custom fallback handler for HTTP resilience. When registered and fallback is enabled,
/// this handler is invoked first on failure; if it returns a response, that is used; otherwise the synthetic response from options is used.
/// </summary>
/// <remarks>
/// <para>Register in DI (e.g. per named client) and pass to <c>AddHttpClientWithResilience</c> (the overload that accepts <c>IHttpFallbackHandler</c>).
/// Use for custom logic such as calling another URL, returning a cached value, or logging.</para>
/// <para><b>Disposal:</b> Do not dispose <see cref="HttpFallbackContext.Result"/> (the failed response). Ownership remains with the pipeline.
/// If you return a new <see cref="HttpResponseMessage"/> from <see cref="TryHandleAsync"/>, the caller (the pipeline) assumes ownership and will dispose it when appropriate.
/// The synthetic fallback response produced by the pipeline when no custom handler returns a value is also owned by the caller.</para>
/// </remarks>
public interface IHttpFallbackHandler
{
    /// <summary>
    /// Attempts to handle the failure and produce a fallback response.
    /// </summary>
    /// <param name="context">The fallback context (failed outcome: result or exception). Do not dispose <paramref name="context"/>.Result.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A fallback response, or null to fall through to the next handler (e.g. synthetic response from options). The pipeline owns and disposes any non-null response you return.</returns>
    ValueTask<HttpResponseMessage?> TryHandleAsync(HttpFallbackContext context, CancellationToken cancellationToken);
}
