namespace HttpResilience.NET.Internal;

/// <summary>
/// Rejects a request whose authority is not on the configured allow-list, before it reaches the hedging handler.
/// </summary>
/// <remarks>
/// The standard hedging handler keeps a separate inner pipeline -- circuit breaker, concurrency limiter and
/// attempt timeout -- per authority, cached for the life of the process with nothing to evict it. That is
/// sound for the fixed set of endpoints it is designed around, and a resource-exhaustion path for a client
/// whose destination can be influenced by request data: a tenant-configured webhook or a stored callback URL.
/// Each novel host permanently allocates a pipeline and a metric series.
/// <para>
/// The allow-list that bounds pipeline selection therefore also bounds which pipelines can exist.
/// </para>
/// <para>
/// <b>This handler cannot see a redirect.</b> A 3xx is resolved inside <see cref="SocketsHttpHandler"/>,
/// below every <see cref="DelegatingHandler"/>, so a redirect from a listed authority to an unlisted one
/// never reaches this code. It never reaches the hedging handler either, so it allocates no pipeline and the
/// cardinality bound still holds -- but the request does go. What bounds the destinations is therefore
/// <see cref="HttpResilience.NET.Options.ConnectionOptions.AllowAutoRedirect"/>, which resolves to
/// <see langword="false"/> for this pipeline precisely because this pipeline enforces a list. The two halves
/// are one control, and both are pinned by <c>RedirectTests</c>.
/// </para>
/// </remarks>
internal sealed class AuthorityAllowListHandler : DelegatingHandler
{
    // Hoisted out of the interpolation. The destination of a hedged client can come from request data --
    // a tenant-configured callback, a stored webhook URL -- so the rejection path is one an outside party
    // can drive at whatever rate it likes. Only the authority varies; the advice does not.
    private const string _advice =
        "that authority is not listed in PipelineSelection:Authorities. Hedging allocates a circuit " +
        "breaker, a limiter and a metric series per authority and never evicts them, so the set has to be " +
        "fixed at deploy time. Add the authority to the list, or use AddHttpResilience instead of " +
        "AddHedgedHttpResilience.";

    private readonly AuthorityIndex _allowed;

    public AuthorityAllowListHandler(AuthorityIndex allowed) => _allowed = allowed;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_allowed.Contains(request.RequestUri))
        {
            // HttpRequestException rather than InvalidOperationException: the condition is the request's own
            // URI, which is runtime data, not a mistake in the wiring. Callers that already wrap outbound
            // calls in `catch (HttpRequestException)` see one more failed request instead of an exception
            // type that escapes to the top of the process.
            throw new HttpRequestException(
                $"A hedged client cannot send a request to '{Describe(request.RequestUri)}': {_advice}");
        }

        return base.SendAsync(request, cancellationToken);
    }

    private static string Describe(Uri? uri) =>
        uri is { IsAbsoluteUri: true }
            ? PipelineKeySelector.BuildAuthority(uri.Scheme, uri.Host, uri.Port, uri.IsDefaultPort)
            : "(relative or missing request URI)";
}
