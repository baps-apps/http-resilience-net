using System.Collections.Frozen;

namespace HttpResilience.NET.Internal;

/// <summary>
/// Shared HTTP method classification for retry and hedging guards.
/// </summary>
internal static class HttpMethodPredicates
{
    /// <summary>
    /// The methods RFC 9110 section 9.2.1 defines as safe, and therefore the only ones this package repeats
    /// without an explicit opt-in. Matched exactly: RFC 9110 section 9.1 makes method names case-sensitive.
    /// </summary>
    private static readonly FrozenSet<string> _safeMethods = new[]
    {
        "GET", "HEAD", "OPTIONS", "TRACE"
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Whether a request's method may be repeated without an explicit opt-in. Exact match, so <c>get</c> is
    /// an unknown method and not safe. A missing method is not safe.
    /// </summary>
    public static bool IsSafe(HttpMethod? method) =>
        method is not null && _safeMethods.Contains(method.Method);

    /// <summary>
    /// The same question asked of a configured string. Configuration is written by hand, so its case is
    /// ignored: <c>get</c> there means <c>GET</c>.
    /// </summary>
    public static bool IsSafe(string method) => _safeMethods.Contains(Normalize(method));

    /// <summary>
    /// Whether a configured value is a syntactically valid HTTP method.
    /// </summary>
    public static bool IsValidMethodToken(string method)
    {
        if (string.IsNullOrEmpty(method))
        {
            return false;
        }

        foreach (char c in method)
        {
            if (!IsTokenChar(c))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsTokenChar(char c) =>
        char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c, StringComparison.Ordinal);

    /// <summary>
    /// Builds the lookup of methods a caller opted in to. Configured entries are upper-cased; request methods
    /// are then matched against them exactly.
    /// </summary>
    public static FrozenSet<string> ToMethodSet(IEnumerable<string> methods) =>
        methods.Select(Normalize).ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// A configured method in its canonical upper-case form.
    /// </summary>
    public static string Normalize(string method) => method.ToUpperInvariant();
}
