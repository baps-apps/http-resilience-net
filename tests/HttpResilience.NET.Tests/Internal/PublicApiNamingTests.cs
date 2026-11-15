using System.Reflection;

namespace HttpResilience.NET.Tests.Internal;

/// <summary>
/// The public surface follows one naming rule: every type starts with <c>HttpResilience</c> and lives in
/// <c>HttpResilience.NET</c>, or in <c>Microsoft.Extensions.DependencyInjection</c> for the extension classes.
/// </summary>
public class PublicApiNamingTests
{
    private static readonly Type[] _exported = typeof(HttpResilienceOptions).Assembly.GetExportedTypes();

    /// <summary>
    /// Unprefixed names such as <c>RetryOptions</c> or <c>ConnectionOptions</c> collide with types of the same
    /// name in other libraries a consumer imports.
    /// </summary>
    /// <remarks>Fails if a public type is added or renamed without the prefix.</remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public void EveryPublicType_StartsWithThePackagePrefix()
    {
        Assert.NotEmpty(_exported);

        string[] unprefixed =
            [.. _exported.Where(type => !type.Name.StartsWith("HttpResilience", StringComparison.Ordinal))
                .Select(type => type.FullName!)];

        Assert.True(unprefixed.Length == 0, $"Public types without the HttpResilience prefix: {string.Join(", ", unprefixed)}");
    }

    /// <summary>
    /// A sub-namespace named <c>Options</c> or <c>Configuration</c> hides <c>Microsoft.Extensions.Options.Options</c>
    /// or the <c>Microsoft.Extensions.Configuration</c> namespace from code inside this package's namespaces.
    /// </summary>
    /// <remarks>Fails if a public type is placed in a new namespace.</remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public void EveryPublicType_LivesInTheRootOrTheDependencyInjectionNamespace()
    {
        string[] elsewhere =
            [.. _exported.Where(type => type.Namespace is not ("HttpResilience.NET" or "Microsoft.Extensions.DependencyInjection"))
                .Select(type => type.FullName!)];

        Assert.True(elsewhere.Length == 0, $"Public types outside the two public namespaces: {string.Join(", ", elsewhere)}");
    }

    /// <summary>
    /// A public option type is the configuration schema. Anything derived from it, or recorded while composing
    /// it, lives in <c>OptionDerivations</c> or <c>OptionsComposition</c>, so the schema cannot grow hidden
    /// state or behavior a consumer's copy of the object does not have.
    /// </summary>
    /// <remarks>Fails if an internal or private member (other than a property's backing field) is added to one.</remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public void PublicOptionTypes_CarryOnlyTheSchema()
    {
        const BindingFlags everything = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
            BindingFlags.Static | BindingFlags.DeclaredOnly;

        Type[] optionTypes = [.. _exported.Where(type => type.IsClass && type.Name.EndsWith("Options", StringComparison.Ordinal))];
        Assert.Contains(typeof(HttpResilienceTimeoutOptions), optionTypes);

        string[] hidden =
            [.. optionTypes.SelectMany(type => type.GetMembers(everything)
                .Where(member => member switch
                {
                    MethodBase method => !method.IsPublic && !method.IsSpecialName,
                    FieldInfo field => !field.IsPublic && !field.Name.Contains("k__BackingField", StringComparison.Ordinal),
                    PropertyInfo property => property.GetMethod is not { IsPublic: true },
                    _ => false
                })
                .Select(member => $"{type.Name}.{member.Name}"))];

        Assert.True(hidden.Length == 0, $"Non-public members on public option types: {string.Join(", ", hidden)}");
    }
}
