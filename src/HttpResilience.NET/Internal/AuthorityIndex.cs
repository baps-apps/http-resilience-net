using System.Collections.Frozen;
using HttpResilience.NET.Options;

namespace HttpResilience.NET.Internal;

/// <summary>
/// A deploy-time-fixed set of authorities, matched against a request without allocating.
/// </summary>
/// <remarks>
/// The obvious implementation builds <c>scheme://host:port</c> from the request and probes a set with it.
/// That puts a string allocation on the request path of every client using per-authority pipelines or a
/// hedged client's allow-list, purely to perform a lookup -- in a package whose claim is that it costs
/// nothing over the handler it configures. Indexing by host and comparing the scheme and port as they are
/// removes the allocation, and returns the allow-list's own string so the pipeline key is a shared instance
/// rather than a fresh one per request.
/// </remarks>
internal sealed class AuthorityIndex
{
    /// <summary>
    /// The authorities indexed by host, probed with a <see cref="ReadOnlySpan{T}"/> so the request host can
    /// be trimmed of its root label without allocating a substring on the request path.
    /// </summary>
    private readonly FrozenDictionary<string, Entry[]>.AlternateLookup<ReadOnlySpan<char>> _byHost;

    private AuthorityIndex(FrozenDictionary<string, Entry[]> byHost) =>
        _byHost = byHost.GetAlternateLookup<ReadOnlySpan<char>>();

    /// <summary>The number of distinct authorities, which bounds the number of pipelines that can exist.</summary>
    public int Count { get; private init; }

    public static AuthorityIndex Create(PipelineSelectionOptions selection)
    {
        List<Uri> parsed = [];
        foreach (string authority in selection.Authorities ?? [])
        {
            if (TryParse(authority, out Uri? uri))
            {
                parsed.Add(uri);
            }
        }

        FrozenDictionary<string, Entry[]> byHost = parsed
            .GroupBy(uri => NormalizeHost(uri).ToString(), StringComparer.OrdinalIgnoreCase)
            .ToFrozenDictionary(
                group => group.Key,
                group => group
                    .Select(uri => new Entry(
                        uri.Scheme,
                        uri.Port,
                        PipelineKeySelector.BuildAuthority(uri.Scheme, uri.Host, uri.Port, uri.IsDefaultPort)))
                    .Distinct()
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);

        return new AuthorityIndex(byHost) { Count = byHost.Sum(pair => pair.Value.Length) };
    }

    /// <summary>
    /// Returns the allow-list entry matching this request, or <see langword="false"/> if there is none.
    /// </summary>
    public bool TryGetKey(Uri? uri, out string key)
    {
        key = string.Empty;
        if (uri is not { IsAbsoluteUri: true } ||
            !_byHost.TryGetValue(NormalizeHost(uri), out Entry[]? entries))
        {
            return false;
        }

        foreach (Entry entry in entries)
        {
            if (entry.Port == uri.Port &&
                string.Equals(entry.Scheme, uri.Scheme, StringComparison.OrdinalIgnoreCase))
            {
                key = entry.Key;
                return true;
            }
        }

        return false;
    }

    public bool Contains(Uri? uri) => TryGetKey(uri, out _);

    /// <summary>
    /// The form of a host that two spellings of the same authority agree on.
    /// </summary>
    /// <remarks>
    /// <see cref="Uri.Host"/> is not a normal form. It returns the Unicode label for an internationalized
    /// host written in Unicode and the punycode label for the same host written in punycode, so an allow-list
    /// written one way rejects a request written the other -- and it keeps the trailing dot of a
    /// fully-qualified name, so <c>orders.internal.</c> and <c>orders.internal</c> are different hosts.
    /// Both fail closed, which makes them an availability edge rather than a bypass, but an allow-list that
    /// rejects a host that is on it is still wrong.
    /// <para>
    /// <see cref="Uri.IdnHost"/> is stable across both spellings and is cached on the <see cref="Uri"/> after
    /// first access. The root label is removed by slicing rather than by <c>TrimEnd</c> so that nothing is
    /// allocated on the request path; <see cref="_byHost"/> is a span alternate lookup for exactly that reason.
    /// Pinned by <c>AuthorityNormalisationTests</c> and <c>PipelineKeySelectorAllocationTests</c>.
    /// </para>
    /// </remarks>
    private static ReadOnlySpan<char> NormalizeHost(Uri uri)
    {
        ReadOnlySpan<char> host = uri.IdnHost;
        return host.Length > 1 && host[^1] == '.' ? host[..^1] : host;
    }

    private static bool TryParse(string? value, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out Uri? parsed) ||
            string.IsNullOrEmpty(parsed.Host))
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    private readonly record struct Entry(string Scheme, int Port, string Key);
}
