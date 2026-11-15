using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace HttpResilience.NET.Internal;

/// <summary>
/// Event 10: a client that may send an unsafe HTTP method more than once, and the key that allows it.
/// </summary>
internal static class UnsafeMethodNotice
{
    private const string EveryUnsafeMethod =
        "POST, PUT, PATCH, DELETE and every method this package does not recognize";

    public static void Report(
        ILogger logger,
        RegisteredClient client,
        HttpResilienceOptions options,
        IConfigurationSection? root)
    {
        if (Describe(client, options, root) is { } detail)
        {
            HttpResilienceLogging.UnsafeMethodsRepeated(
                logger, client.Name, detail.Methods, detail.Verb, detail.GuardPath);
        }
    }

    private static (string Methods, string Verb, string GuardPath)? Describe(
        RegisteredClient client,
        HttpResilienceOptions options,
        IConfigurationSection? root)
    {
        if (client.Kind is PipelineKind.Hedging)
        {
            return options.Hedging.DisableForUnsafeHttpMethods
                ? null
                : (EveryUnsafeMethod, "hedged", $"{client.Scope}:Hedging:DisableForUnsafeHttpMethods");
        }

        if (!options.Retry.Enabled)
        {
            return null;
        }

        if (options.Retry.RetryableMethods is { Count: > 0 } allowList)
        {
            string? unsafeMethods = UnsafeEntries(allowList);
            return unsafeMethods is null
                ? null
                : (unsafeMethods, "retried", AllowListPath(client, root));
        }

        return options.Retry.DisableForUnsafeHttpMethods
            ? null
            : (EveryUnsafeMethod, "retried", $"{client.Scope}:Retry:DisableForUnsafeHttpMethods");
    }

    /// <summary>
    /// The entries in an allow-list that are not RFC 9110 safe methods, or <see langword="null"/> if none.
    /// </summary>
    private static string? UnsafeEntries(IList<string> allowList)
    {
        List<string>? unsafeMethods = null;
        foreach (string method in allowList)
        {
            if (!HttpMethodPredicates.IsSafe(method))
            {
                (unsafeMethods ??= []).Add(method.ToUpperInvariant());
            }
        }

        return unsafeMethods is null ? null : string.Join(", ", unsafeMethods);
    }

    /// <summary>
    /// Where this client's <c>Retry:RetryableMethods</c> comes from: its own section, the root, or code.
    /// </summary>
    private static string AllowListPath(RegisteredClient client, IConfigurationSection? root)
    {
        const string Key = "Retry:RetryableMethods";

        if (root is null)
        {
            return $"{Key}, set in code rather than in configuration";
        }

        if (!string.IsNullOrEmpty(client.SectionName) &&
            root.GetSection(HttpResilienceConfigurationKeys.ClientsSection)
                .GetSection(client.SectionName)
                .GetSection(Key)
                .Exists())
        {
            return $"{client.Scope}:{Key}";
        }

        return root.GetSection(Key).Exists()
            ? $"{root.Path}:{Key}, inherited by this client"
            : $"{Key}, set in code rather than in configuration";
    }
}
