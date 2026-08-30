using System.Globalization;
using HttpResilience.NET.Options;

namespace HttpResilience.NET.Internal;

/// <summary>
/// Maps a request to the key of the resilience pipeline that should handle it.
/// </summary>
/// <remarks>
/// The set of keys is fixed by configuration, so no amount of request traffic can create additional
/// pipelines. Anything outside the configured allow-list shares <see cref="SharedKey"/>.
/// </remarks>
internal static class PipelineKeySelector
{
    /// <summary>The key shared by every authority that is not individually allow-listed.</summary>
    public const string SharedKey = "shared";

    /// <summary>
    /// Builds the selector delegate for <c>SelectPipelineBy</c>. Returns a bounded set of keys, and
    /// allocates nothing per request.
    /// </summary>
    public static Func<HttpRequestMessage, string> Create(PipelineSelectionOptions selection)
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
    /// <remarks>
    /// The key must identify one live circuit breaker, because the tracker is a dictionary and a coarser key
    /// lets the last transition to fire overwrite the others -- one host recovering would mask another still
    /// open. How many breakers exist differs by pipeline:
    /// <list type="bullet">
    /// <item><description>
    /// The standard handler runs one breaker per pipeline, so the key is per authority only under
    /// <see cref="PipelineSelectionMode.ByAuthority"/> and shared otherwise.
    /// </description></item>
    /// <item><description>
    /// The hedging handler keeps a breaker per endpoint whatever the selection mode, so the key is always per
    /// authority. The allow-list <c>AddHedgedHttpResilience</c> requires is what keeps that set bounded.
    /// </description></item>
    /// </list>
    /// </remarks>
    public static Func<HttpRequestMessage?, string> CreateForTracking(HttpResilienceOptions options, PipelineKind kind)
    {
        bool perAuthority = kind is PipelineKind.Hedging ||
            options.PipelineSelection.Mode is PipelineSelectionMode.ByAuthority;

        if (!perAuthority)
        {
            return static _ => SharedKey;
        }

        Func<HttpRequestMessage, string> selector = Create(options.PipelineSelection);
        return request => request is null ? SharedKey : selector(request);
    }

    public static string BuildAuthority(string scheme, string host, int port, bool isDefaultPort) =>
        isDefaultPort
            ? string.Concat(scheme, "://", host)
            : string.Create(CultureInfo.InvariantCulture, $"{scheme}://{host}:{port}");
}
