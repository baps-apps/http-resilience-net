using System.Globalization;

namespace HttpResilience.NET.Internal;

/// <summary>
/// Maps a request to the key of the resilience pipeline that should handle it.
/// </summary>
internal static class PipelineKeySelector
{
    /// <summary>The key shared by every authority that is not individually allow-listed.</summary>
    public const string SharedKey = "shared";

    /// <summary>
    /// Builds the selector delegate for <c>SelectPipelineBy</c>. Returns a bounded set of keys, and
    /// allocates nothing per request.
    /// </summary>
    public static Func<HttpRequestMessage, string> Create(HttpResiliencePipelineSelectionOptions selection)
    {
        AuthorityIndex index = AuthorityIndex.Create(selection);
        if (index.Count == 0)
        {
            return static _ => SharedKey;
        }

        return request => index.TryGetKey(request.RequestUri, out string key) ? key : SharedKey;
    }

    /// <summary>
    /// Normalises a configured authority to the same shape a request is matched against, for validation.
    /// </summary>
    public static bool TryNormalizeAuthority(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out Uri? uri) ||
            string.IsNullOrEmpty(uri.Host))
        {
            return false;
        }

        normalized = BuildAuthority(uri.Scheme, uri.Host, uri.Port, uri.IsDefaultPort);
        return true;
    }

    /// <summary>
    /// Builds the key function used to label circuit breaker state, for the pipeline this client actually uses.
    /// </summary>
    public static Func<HttpRequestMessage?, string> CreateForTracking(HttpResilienceOptions options, PipelineKind kind)
    {
        bool perAuthority = kind is PipelineKind.Hedging ||
            options.PipelineSelection.Mode is HttpResiliencePipelineSelectionMode.ByAuthority;

        if (!perAuthority)
        {
            return static _ => SharedKey;
        }

        Func<HttpRequestMessage, string> selector = Create(options.PipelineSelection);
        return request => request is null ? SharedKey : selector(request);
    }

    /// <summary>
    /// Every circuit breaker key a client can have: one per listed authority for a hedged client, plus the
    /// shared one for a standard <c>ByAuthority</c> client (unlisted authorities share it), or just the shared
    /// one. Bounded by the allow-list, like the pipelines themselves.
    /// </summary>
    public static IEnumerable<string> BreakerKeys(HttpResilienceOptions options, PipelineKind kind)
    {
        bool perAuthority = kind is PipelineKind.Hedging ||
            options.PipelineSelection.Mode is HttpResiliencePipelineSelectionMode.ByAuthority;

        if (!perAuthority)
        {
            return [SharedKey];
        }

        IEnumerable<string> listed = AuthorityIndex.Create(options.PipelineSelection).Keys;
        return kind is PipelineKind.Hedging ? listed : listed.Append(SharedKey);
    }

    public static string BuildAuthority(string scheme, string host, int port, bool isDefaultPort) =>
        isDefaultPort
            ? string.Concat(scheme, "://", host)
            : string.Create(CultureInfo.InvariantCulture, $"{scheme}://{host}:{port}");
}
