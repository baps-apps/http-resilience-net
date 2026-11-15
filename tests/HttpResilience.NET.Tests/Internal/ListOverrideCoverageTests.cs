using System.Collections;
using System.Reflection;

namespace HttpResilience.NET.Tests.Internal;

/// <summary>
/// Every collection in the schema has to be listed in the client-section override, or it silently unions with
/// the root's.
/// </summary>
/// <remarks>
/// The binder adds to an existing collection instead of replacing it, so
/// <c>HttpClientBuilderExtensions.ResetListsStatedBy</c> clears each list a client states. It names its paths
/// as literals, so this uses reflection to catch a new list added to the schema.
/// </remarks>
public class ListOverrideCoverageTests
{
    /// <summary>
    /// The configuration paths <c>ResetListsStatedBy</c> clears before binding a client section.
    /// </summary>
    private static readonly string[] _handled =
    [
        "Retry:RetryableMethods",
        "PipelineSelection:Authorities"
    ];

    /// <summary>
    /// Fails when a collection property is added to the schema without being cleared for a client override.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public void EveryCollectionInTheSchema_IsClearedBeforeAClientSectionIsBound()
    {
        List<string> collections = [];
        Collect(typeof(HttpResilienceOptions), prefix: string.Empty, collections, depth: 0);

        Assert.NotEmpty(collections);

        string[] unhandled = [.. collections.Except(_handled, StringComparer.Ordinal)];

        Assert.True(unhandled.Length == 0,
            "These configuration lists are bound onto the root's values and would union with them rather " +
            "than replace them. Add each to HttpClientBuilderExtensions.ResetListsStatedBy and to the " +
            $"_handled list here: {string.Join(", ", unhandled)}");

        // The reverse direction too: a listed path that no longer exists in the schema.
        string[] stale = [.. _handled.Except(collections, StringComparer.Ordinal)];

        Assert.True(stale.Length == 0,
            "These paths are cleared before a client section is bound but no longer exist in the schema, so " +
            $"the override no longer reaches the property it was written for: {string.Join(", ", stale)}");
    }

    /// <summary>
    /// Walks the options graph and records the configuration path of every non-string collection property.
    /// </summary>
    private static void Collect(Type type, string prefix, List<string> collections, int depth)
    {
        // The schema is two levels deep by construction; the bound guards against a cycle rather than
        // expressing an expected shape.
        if (depth > 4)
        {
            return;
        }

        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            string path = prefix.Length == 0 ? property.Name : $"{prefix}:{property.Name}";
            Type propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

            if (propertyType == typeof(string) || propertyType.IsPrimitive || propertyType.IsEnum ||
                propertyType == typeof(TimeSpan))
            {
                continue;
            }

            if (typeof(IEnumerable).IsAssignableFrom(propertyType))
            {
                collections.Add(path);
                continue;
            }

            Collect(propertyType, path, collections, depth + 1);
        }
    }
}
