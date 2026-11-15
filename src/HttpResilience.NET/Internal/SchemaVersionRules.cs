using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.Configuration;

namespace HttpResilience.NET.Internal;

/// <summary>
/// <c>HttpResilience:SchemaVersion</c>: the package version a configuration file was written for, so a file shared
/// across services can be rolled out ahead of the services that read it.
/// </summary>
/// <remarks>
/// A minor version only adds optional keys (<c>docs/VERSIONING.md</c>), so a file written for a newer minor of
/// this major may hold keys this version does not know: they are a startup Warning (event 18), not a failure. A
/// newer major may rename keys or change their meaning, so it fails startup. Stated at or below this version, or
/// not at all, an unknown key is a typo and fails as before.
/// </remarks>
internal static class SchemaVersionRules
{
    public const string Key = "SchemaVersion";

    /// <summary>This package's schema version: its own major and minor.</summary>
    public static readonly Version Package = ReadPackageVersion();

    /// <summary>The stated version, or <see langword="null"/> when none is stated or it is not a version.</summary>
    public static Version? Declared(IConfigurationSection? root) =>
        root?[Key] is { } stated && TryParse(stated, out Version? version) ? version : null;

    /// <summary>Whether unknown keys are expected: the file was written for a newer minor of this major.</summary>
    public static bool IsNewerMinor(IConfigurationSection? root) =>
        Declared(root) is { } declared && declared.Major == Package.Major && declared.Minor > Package.Minor;

    /// <summary>A failure for a stated value that is not a version, or names a newer major; otherwise null.</summary>
    public static string? Failure(IConfigurationSection root)
    {
        if (root[Key] is not { } stated)
        {
            return null;
        }

        string path = $"{root.Path}:{Key}";
        string package = Format(Package);

        if (!TryParse(stated, out Version? declared))
        {
            return $"{path} -- value '{stated}' is not a version. Expected the package version the file was written " +
                $"for, as major.minor, such as '{package}'. Reason: it decides whether a key this version does not " +
                "know is a typo or a key from a newer version.";
        }

        return declared.Major > Package.Major
            ? $"{path} -- value '{stated}' names a newer major version than this package ({package}). Expected " +
              $"{Package.Major}.x or lower. Reason: a major version may rename keys or change what they mean, so " +
              "this version cannot run on that file safely. Upgrade HttpResilience.NET, or keep a file for each major."
            : null;
    }

    public static string Format(Version version) =>
        string.Create(CultureInfo.InvariantCulture, $"{version.Major}.{version.Minor}");

    /// <summary>Accepts <c>3</c>, <c>3.1</c> or <c>3.1.0</c>; only the major and minor count.</summary>
    private static bool TryParse(string value, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Version? version)
    {
        string trimmed = value.Trim();
        if (int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out int major))
        {
            version = new Version(major, 0);
            return true;
        }

        if (Version.TryParse(trimmed, out Version? parsed) && trimmed.All(c => char.IsAsciiDigit(c) || c == '.'))
        {
            version = new Version(parsed.Major, parsed.Minor);
            return true;
        }

        version = null;
        return false;
    }

    private static Version ReadPackageVersion()
    {
        Assembly assembly = typeof(SchemaVersionRules).Assembly;
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        string? release = informational?.Split('+', '-')[0];

        return release is not null && Version.TryParse(release, out Version? parsed)
            ? new Version(parsed.Major, parsed.Minor)
            : new Version(assembly.GetName().Version?.Major ?? 0, assembly.GetName().Version?.Minor ?? 0);
    }
}
