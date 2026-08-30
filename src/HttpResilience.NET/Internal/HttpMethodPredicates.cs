using System.Collections.Frozen;

namespace HttpResilience.NET.Internal;

/// <summary>
/// Shared HTTP method classification for retry and hedging guards.
/// </summary>
internal static class HttpMethodPredicates
{
    /// <summary>
    /// The methods RFC 9110 section 9.2.1 defines as safe, and therefore the only ones this package repeats
    /// without an explicit opt-in.
    /// </summary>
    /// <remarks>
    /// An allow-list, deliberately, rather than a deny-list of POST, PATCH, PUT, DELETE and CONNECT -- which
    /// is what <c>HttpRetryStrategyOptionsExtensions.DisableForUnsafeHttpMethods</c> applies and what this
    /// package used to delegate to. A deny-list answers "is this one of the five verbs I know are unsafe?",
    /// so a method it has never heard of is repeated by default: a WebDAV <c>MOVE</c>, <c>MKCOL</c> or
    /// <c>PROPPATCH</c>, a cache <c>PURGE</c>, a version-control <c>MERGE</c>, any
    /// <c>new HttpMethod("...")</c> an application passes. Every one of those mutates.
    /// <para>
    /// RFC 9110 defines safety for a closed set of methods and says nothing about extensions, so the only
    /// sound default for an unrecognized method is not to duplicate it. Naming it in
    /// <c>Retry:RetryableMethods</c> remains the supported way to opt one in.
    /// </para>
    /// </remarks>
    private static readonly FrozenSet<string> _safeMethods = new[]
    {
        "GET", "HEAD", "OPTIONS", "TRACE"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether this method may be repeated without an explicit opt-in. A missing method is not safe.
    /// </summary>
    public static bool IsSafe(HttpMethod? method) =>
        method is not null && _safeMethods.Contains(method.Method);

    /// <summary>
    /// The same question asked of a configured string rather than a request's method.
    /// </summary>
    /// <remarks>
    /// A string overload rather than <c>IsSafe(new HttpMethod(value))</c>: the constructor throws on a value
    /// that is not a token, and the one caller runs inside <c>PostConfigure</c> -- which the options factory
    /// invokes <i>before</i> the validators. Constructing the method there would replace this validator's
    /// message about a malformed entry with an <see cref="ArgumentException"/> from the BCL.
    /// </remarks>
    public static bool IsSafe(string method) => _safeMethods.Contains(method);

    /// <summary>
    /// Whether a configured value is a syntactically valid HTTP method.
    /// </summary>
    /// <remarks>
    /// A token per RFC 9110 section 5.6.2, not a member of a known set. The old check rejected anything outside
    /// nine standard verbs, which was defensible only while unrecognized methods were retried anyway: now
    /// that they are not, <c>RetryableMethods</c> is the <i>only</i> way to retry one, so rejecting
    /// <c>PURGE</c> here would leave a real configuration with no expressible form. The check still catches
    /// the mistake it was written for -- whitespace, an empty entry, a URL pasted into the list.
    /// </remarks>
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
    /// Builds a case-insensitive lookup of the methods a caller explicitly opted in to.
    /// </summary>
    public static FrozenSet<string> ToMethodSet(IEnumerable<string> methods) =>
        methods.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
}
