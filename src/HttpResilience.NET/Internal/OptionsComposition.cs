using System.Runtime.CompilerServices;

namespace HttpResilience.NET.Internal;

/// <summary>
/// What composing an options instance recorded that the finished values cannot show: whether
/// <c>Connection:AllowAutoRedirect</c> was stated before it was resolved, what the root delegates did, and which
/// startup switches were malformed.
/// </summary>
/// <remarks>
/// Held beside the instance rather than on the public option types. Keyed by instance and held weakly, so each
/// <c>IOptionsSnapshot</c> scope's copy has its own record and a discarded copy leaves nothing behind.
/// </remarks>
internal static class OptionsComposition
{
    private static readonly object Stated = new();
    private static readonly ConditionalWeakTable<HttpResilienceConnectionOptions, object> RedirectStated = new();
    private static readonly ConditionalWeakTable<HttpResilienceOptions, List<string>> RootDelegateFailures = new();
    private static readonly ConditionalWeakTable<HttpResilienceStartupOptions, List<string>> MalformedKeys = new();

    /// <summary>Records whether <c>AllowAutoRedirect</c> was stated, before resolving erases the difference.</summary>
    public static void SetRedirectStated(HttpResilienceConnectionOptions connection, bool stated)
    {
        if (stated)
        {
            RedirectStated.AddOrUpdate(connection, Stated);
        }
        else
        {
            RedirectStated.Remove(connection);
        }
    }

    /// <summary>Only a stated redirect value is written onto a consumer's handler.</summary>
    public static bool IsRedirectStated(HttpResilienceConnectionOptions connection) =>
        RedirectStated.TryGetValue(connection, out _);

    public static void SetRootDelegateFailures(HttpResilienceOptions options, List<string>? failures)
    {
        if (failures is { Count: > 0 })
        {
            RootDelegateFailures.AddOrUpdate(options, failures);
        }
        else
        {
            RootDelegateFailures.Remove(options);
        }
    }

    public static List<string>? RootDelegateFailuresOf(HttpResilienceOptions options) =>
        RootDelegateFailures.TryGetValue(options, out List<string>? failures) ? failures : null;

    public static void AddMalformedKey(HttpResilienceStartupOptions options, string failure) =>
        MalformedKeys.GetValue(options, static _ => []).Add(failure);

    public static List<string>? MalformedKeysOf(HttpResilienceStartupOptions options) =>
        MalformedKeys.TryGetValue(options, out List<string>? failures) ? failures : null;
}
