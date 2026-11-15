using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace HttpResilience.NET.Internal;

/// <summary>
/// Keys that were renamed, checked against the raw section so an old configuration file fails startup instead
/// of binding to nothing. Read from configuration only: the options types no longer carry the old names.
/// </summary>
internal static class RenamedKeyRules
{
    private const string MaxAttemptsKey = "Retry:MaxAttempts";

    public static IEnumerable<string> Collect(IConfigurationSection section)
    {
        if (section[MaxAttemptsKey] is not { } legacy)
        {
            yield break;
        }

        string requests = int.TryParse(legacy, NumberStyles.Integer, CultureInfo.InvariantCulture, out int retries)
            ? string.Create(CultureInfo.InvariantCulture, $", so {retries} sends {retries + 1} requests")
            : string.Empty;

        yield return
            $"{section.Path}:{MaxAttemptsKey} -- value '{legacy}' is invalid. Expected Retry:MaxRetries " +
            $"instead, with the same value. Reason: Retry:MaxAttempts was renamed to Retry:MaxRetries. The " +
            $"value counts retries after the first attempt{requests}.";
    }
}
