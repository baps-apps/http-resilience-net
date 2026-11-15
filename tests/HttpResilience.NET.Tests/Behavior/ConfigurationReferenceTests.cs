using System.Reflection;

namespace HttpResilience.NET.Tests.Behavior;

/// <summary>
/// Every configuration key a consumer can set appears in docs/CONFIGURATION.md.
/// </summary>
/// <remarks>
/// That table is the only place the whole schema is listed. A containment check, not a row check:
/// <c>Retry:MaxAttempts</c> is mentioned inside the <c>MaxRetries</c> row because it is a renamed key that
/// fails startup, not a key to set.
/// </remarks>
public class ConfigurationReferenceTests
{
    private const string SectionHeading = "## Keys";

    private const string ReferencePath = "docs/CONFIGURATION.md";

    /// <summary>
    /// The root-only startup switches, which live on their own options type rather than the per-client graph.
    /// </summary>
    private static readonly string[] _rawSectionKeys =
    [
        .. typeof(HttpResilienceStartupOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
    ];

    [Fact(Timeout = TestTimeouts.Default)]
    public void EveryConfigurableKey_AppearsInTheConfigurationReference()
    {
        string reference = ConfigurationReferenceSection();

        List<string> missing = [];
        foreach (string key in ConfigurableKeys().Concat(_rawSectionKeys).Distinct(StringComparer.Ordinal))
        {
            if (!reference.Contains(key, StringComparison.Ordinal))
            {
                missing.Add(key);
            }
        }

        Assert.True(
            missing.Count == 0,
            $"{ReferencePath}'s \"{SectionHeading}\" does not mention {string.Join(", ", missing)}. It is the only " +
            "place the whole schema is listed, so a key absent from it is a key an adopting team finds out " +
            "about from a startup validation failure.");
    }

    /// <summary>
    /// Every public property on the options graph, section objects included -- those are the section names
    /// in the table's first column.
    /// </summary>
    private static IEnumerable<string> ConfigurableKeys()
    {
        var seen = new HashSet<Type>();
        return Walk(typeof(HttpResilienceOptions));

        IEnumerable<string> Walk(Type type)
        {
            if (!seen.Add(type))
            {
                yield break;
            }

            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                yield return property.Name;

                // Sub-option objects are held as get-only properties with initialisers, so recursion is what
                // reaches Timeout:Total and the rest. Enums and List<string> are leaves.
                if (property.PropertyType.Namespace == typeof(HttpResilienceOptions).Namespace &&
                    property.PropertyType is { IsClass: true, IsGenericType: false })
                {
                    foreach (string nested in Walk(property.PropertyType))
                    {
                        yield return nested;
                    }
                }
            }
        }
    }

    /// <summary>
    /// The reference section's text, from its heading to the next one.
    /// </summary>
    private static string ConfigurationReferenceSection()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HttpResilience.NET.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        string path = Path.Combine(directory.FullName, ReferencePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"{ReferencePath} was not found at '{path}'.");

        string reference = File.ReadAllText(path);
        int start = reference.IndexOf(SectionHeading, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{ReferencePath} has no \"{SectionHeading}\" heading.");

        int end = reference.IndexOf("\n## ", start + SectionHeading.Length, StringComparison.Ordinal);
        return end < 0 ? reference[start..] : reference[start..end];
    }
}
