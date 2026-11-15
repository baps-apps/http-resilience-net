using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace HttpResilience.NET.Internal;

/// <summary>
/// Event 18: the keys a file written for a newer minor version holds that this version ignores. Logged once for
/// the application, listing the root's keys and every registered client section's.
/// </summary>
/// <remarks>
/// These keys are not a validation failure (<see cref="SchemaVersionRules"/>), so this is the only place they are
/// named. Nothing is logged under <c>AllowUnknownKeys</c>, which turns the check off.
/// </remarks>
internal static class SchemaVersionNotice
{
    public static void Report(
        ILogger logger,
        HttpResilienceRegistration registration,
        HttpResilienceStartupOptions startup)
    {
        if (startup.AllowUnknownKeys ||
            registration.Section is not { } root ||
            !SchemaVersionRules.IsNewerMinor(root) ||
            !logger.IsEnabled(LogLevel.Warning))
        {
            return;
        }

        IConfigurationSection clients = root.GetSection(HttpResilienceConfigurationKeys.ClientsSection);
        List<string> paths =
        [
            .. UnknownKeyRules.Paths(root, isRoot: true),
            .. registration.RegisteredClients
                .Select(client => client.SectionName)
                .Where(section => !string.IsNullOrEmpty(section))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .SelectMany(section => UnknownKeyRules.Paths(clients.GetSection(section), isRoot: false))
        ];

        if (paths.Count == 0)
        {
            return;
        }

        HttpResilienceLogging.KeysFromNewerSchemaIgnored(
            logger,
            SchemaVersionRules.Format(SchemaVersionRules.Declared(root)!),
            SchemaVersionRules.Format(SchemaVersionRules.Package),
            paths.Count,
            string.Join(", ", paths));
    }
}
