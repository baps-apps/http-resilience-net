namespace HttpResilience.NET.Internal;

/// <summary>
/// Rejects a request whose authority is not on the configured allow-list, before it reaches the hedging handler.
/// </summary>
internal sealed class AuthorityAllowListHandler : DelegatingHandler
{
    private const string _advice =
        "that authority is not listed in PipelineSelection:Authorities. Hedging allocates a circuit " +
        "breaker, a limiter and a metric series per authority and never evicts them, so the set has to be " +
        "fixed at deploy time. Add the authority to the list, or use AddHttpResilienceHandler instead of " +
        "AddHttpResilienceHedgingHandler.";

    private readonly AuthorityIndex _allowed;

    public AuthorityAllowListHandler(AuthorityIndex allowed) => _allowed = allowed;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        EnsureAllowed(request);
        return base.SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// The path <c>HttpClient.Send</c> takes. The base implementation forwards without calling
    /// <see cref="SendAsync"/>, so the check has to be made here too.
    /// </summary>
    protected override HttpResponseMessage Send(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        EnsureAllowed(request);
        return base.Send(request, cancellationToken);
    }

    private void EnsureAllowed(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Not HttpRequestException: retry predicates, the platform's default included, treat every one as
        // transient, so a consumer's own retry would repeat a refusal no retry can fix. HttpClient throws
        // InvalidOperationException for a request it cannot send.
        if (!_allowed.Contains(request.RequestUri))
        {
            throw new InvalidOperationException(
                $"A hedged client cannot send a request to '{Describe(request.RequestUri)}': {_advice}");
        }
    }

    private static string Describe(Uri? uri) =>
        uri is { IsAbsoluteUri: true }
            ? PipelineKeySelector.BuildAuthority(uri.Scheme, uri.Host, uri.Port, uri.IsDefaultPort)
            : "(relative or missing request URI)";
}
