using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.Configuration;

namespace HttpResilience.NET.Internal;

/// <summary>
/// Keys the schema does not have, checked against the raw section: the binder ignores them, so a typo would
/// silently keep the default.
/// </summary>
/// <remarks>
/// The schema is read from the option types themselves, so it cannot drift from what the binder fills. List
/// entries (<c>RetryableMethods:0</c>) are indexed children and are not checked; a renamed key keeps its own
/// message (<see cref="RenamedKeyRules"/>).
/// </remarks>
internal static class UnknownKeyRules
{
    /// <summary>Keys valid only at the root: the client overrides, the startup switches and the schema version.</summary>
    private static readonly FrozenSet<string> RootOnly =
        PropertiesOf<HttpResilienceStartupOptions>()
            .Append(HttpResilienceConfigurationKeys.ClientsSection)
            .Append(SchemaVersionRules.Key)
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Every top-level key, mapped to its child keys, or to <see langword="null"/> for a scalar or a list.
    /// </summary>
    private static readonly FrozenDictionary<string, FrozenSet<string>?> Schema = BuildSchema();

    /// <summary>Renamed keys, by section, reported by <see cref="RenamedKeyRules"/> instead.</summary>
    private static readonly FrozenSet<string> Renamed =
        new[] { "Retry:MaxAttempts" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reports keys in <paramref name="section"/> the schema does not have.
    /// </summary>
    /// <param name="section">The root section, or one client's section.</param>
    /// <param name="isRoot">Whether <paramref name="section"/> is the root, where the root-only keys are valid.</param>
    /// <param name="rootPath">The root section's path, for the opt-out key in the message.</param>
    public static IEnumerable<string> Collect(IConfigurationSection section, bool isRoot, string rootPath) =>
        Find(section, isRoot).Select(unknown =>
            Describe(unknown.Path, unknown.Key, unknown.Valid, rootPath, unknown.RootOnly));

    /// <summary>The configuration paths of the unknown keys in <paramref name="section"/>, for event 18.</summary>
    public static IEnumerable<string> Paths(IConfigurationSection section, bool isRoot) =>
        Find(section, isRoot).Select(unknown => unknown.Path);

    private static IEnumerable<UnknownKey> Find(IConfigurationSection section, bool isRoot)
    {
        foreach (IConfigurationSection child in section.GetChildren())
        {
            if (isRoot && RootOnly.Contains(child.Key))
            {
                continue;
            }

            if (!Schema.TryGetValue(child.Key, out FrozenSet<string>? childKeys))
            {
                yield return new UnknownKey(child.Path, child.Key, Schema.Keys, RootOnly.Contains(child.Key));
                continue;
            }

            if (childKeys is null)
            {
                continue;
            }

            foreach (IConfigurationSection grandchild in child.GetChildren())
            {
                if (!childKeys.Contains(grandchild.Key) && !Renamed.Contains($"{child.Key}:{grandchild.Key}"))
                {
                    yield return new UnknownKey(grandchild.Path, grandchild.Key, childKeys, RootOnly: false);
                }
            }
        }
    }

    private readonly record struct UnknownKey(string Path, string Key, IEnumerable<string> Valid, bool RootOnly);

    private static string Describe(
        string path,
        string key,
        IEnumerable<string> valid,
        string rootPath,
        bool rootOnly)
    {
        string expected = rootOnly
            ? $"'{key}' only at the root, {rootPath}:{key}: it applies to the application, not to one client"
            : Nearest(key, valid) is { } suggestion
                ? $"a key of the schema. Did you mean '{suggestion}'?"
                : $"one of {string.Join(", ", valid.Order(StringComparer.Ordinal))}";

        return
            $"{path} -- unknown key. Expected {expected}. Reason: the key is bound to nothing, so its value has " +
            "no effect and the default stays in force. If the key comes from a newer version of this package, " +
            $"state that version as {rootPath}:{SchemaVersionRules.Key} (this package is " +
            $"{SchemaVersionRules.Format(SchemaVersionRules.Package)}) and the key becomes a startup warning; " +
            $"{rootPath}:{nameof(HttpResilienceStartupOptions.AllowUnknownKeys)}: true turns the check off entirely.";
    }

    /// <summary>The valid key closest to <paramref name="key"/>, if it is close enough to be a typo.</summary>
    private static string? Nearest(string key, IEnumerable<string> valid)
    {
        int threshold = Math.Max(2, key.Length / 3);
        return valid
            .Select(candidate => (Candidate: candidate, Distance: Distance(key, candidate)))
            .Where(pair => pair.Distance <= threshold)
            .OrderBy(pair => pair.Distance)
            .ThenBy(pair => pair.Candidate, StringComparer.Ordinal)
            .Select(pair => pair.Candidate)
            .FirstOrDefault();
    }

    /// <summary>Levenshtein distance, ignoring case.</summary>
    private static int Distance(string a, string b)
    {
        int[] previous = new int[b.Length + 1];
        int[] current = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = char.ToUpperInvariant(a[i - 1]) == char.ToUpperInvariant(b[j - 1]) ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    private static FrozenDictionary<string, FrozenSet<string>?> BuildSchema()
    {
        var schema = new Dictionary<string, FrozenSet<string>?>(StringComparer.OrdinalIgnoreCase);
        foreach (PropertyInfo property in typeof(HttpResilienceOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            schema[property.Name] = ChildKeys(property.PropertyType);
        }

        return schema.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The keys of a nested options type, or <see langword="null"/> for a scalar or list. Each type is named, so
    /// the trimmer keeps its properties; a new nested type fails here until it is added.
    /// </summary>
    private static FrozenSet<string>? ChildKeys(Type type) => type switch
    {
        _ when type == typeof(HttpResilienceTimeoutOptions) => Keys<HttpResilienceTimeoutOptions>(),
        _ when type == typeof(HttpResilienceRetryOptions) => Keys<HttpResilienceRetryOptions>(),
        _ when type == typeof(HttpResilienceCircuitBreakerOptions) => Keys<HttpResilienceCircuitBreakerOptions>(),
        _ when type == typeof(HttpResilienceConnectionOptions) => Keys<HttpResilienceConnectionOptions>(),
        _ when type == typeof(HttpResilienceRateLimiterOptions) => Keys<HttpResilienceRateLimiterOptions>(),
        _ when type == typeof(HttpResilienceConcurrencyLimiterOptions) => Keys<HttpResilienceConcurrencyLimiterOptions>(),
        _ when type == typeof(HttpResilienceHedgingOptions) => Keys<HttpResilienceHedgingOptions>(),
        _ when type == typeof(HttpResiliencePipelineSelectionOptions) => Keys<HttpResiliencePipelineSelectionOptions>(),
        _ when type.IsClass && type.Namespace == typeof(HttpResilienceOptions).Namespace =>
            throw new InvalidOperationException(
                $"{type.Name} is a nested options type the unknown-key rule does not know. Add it to {nameof(ChildKeys)}."),
        _ => null
    };

    private static FrozenSet<string> Keys<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>() =>
        PropertiesOf<T>().ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> PropertiesOf<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>() =>
        typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(property => property.Name);
}
