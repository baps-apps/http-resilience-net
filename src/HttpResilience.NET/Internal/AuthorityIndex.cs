using System.Collections.Frozen;

namespace HttpResilience.NET.Internal;

/// <summary>
/// A deploy-time-fixed set of authorities, matched against a request without allocating.
/// </summary>
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

    public static AuthorityIndex Create(HttpResiliencePipelineSelectionOptions selection)
    {
        List<Uri> parsed = [];
        foreach (string authority in selection.Authorities)
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

    /// <summary>Every pipeline key the index can return, one per distinct authority.</summary>
    public IEnumerable<string> Keys => _byHost.Dictionary.Values.SelectMany(entries => entries.Select(entry => entry.Key));

    /// <summary>
    /// The form of a host that two spellings of the same authority agree on.
    /// </summary>
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
