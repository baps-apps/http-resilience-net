using System.Reflection;
using HttpResilience.NET.Internal;
using HttpResilience.NET.Options;

namespace HttpResilience.NET.Tests.Behavior;

/// <summary>
/// Every configuration key a consumer can set appears in docs/CONFIGURATION.md.
/// </summary>
/// <remarks>
/// That table is what a team adopting the package reads to find out what it can configure, and it is the
/// only place the whole schema is listed. Four keys were missing from it -- <c>SegmentsPerWindow</c>,
/// <c>TokenLimit</c>, <c>TokensPerPeriod</c> and <c>ReplenishmentPeriod</c> -- so <c>TokenBucket</c>, one of
/// three advertised algorithms, had all three of its required keys documented nowhere but a
/// <c>docs/RECIPES.md</c> example. A team selecting it found out from a startup validation failure.
/// <para>
/// The same shape as <c>scripts/check-benchmark-docs.py</c> and
/// <c>TelemetryMeterTests.TheDocumentedInstrumentNames_AreTheOnesTheMeterPublishes</c>: this package's
/// documentation makes specific claims, and the ones nothing compares against the code are the ones that
/// drift. A property is added to the options graph in one commit and to the reference in another, or in
/// none -- and <c>PublicAPI.Unshipped.txt</c> catches the API change while nothing catches the omission.
/// </para>
/// <para>
/// Deliberately a containment check rather than a row check. The reference documents
/// <c>Retry:MaxAttempts</c> inside the <c>MaxRetries</c> row, because it is a tombstone that fails startup
/// rather than a key anyone should set, and demanding a row of its own would be demanding the wrong
/// documentation. What this asserts is that no key is absent entirely.
/// </para>
/// </remarks>
public class ConfigurationReferenceTests
{
    private const string SectionHeading = "## Keys";

    private const string ReferencePath = "docs/CONFIGURATION.md";

    /// <summary>
    /// The two root-only keys that are not properties on the options graph. Both are read from the raw
    /// configuration section rather than bound, precisely because they are statements about the process and
    /// binding them would make them look inheritable per client -- so reflection cannot find them and they
    /// are named here instead.
    /// </summary>
    private static readonly string[] _rawSectionKeys =
    [
        UnusedClientSectionValidator.AllowUnusedKey,
        ClientStartupProbe.EnabledKey
    ];

    [Fact]
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
