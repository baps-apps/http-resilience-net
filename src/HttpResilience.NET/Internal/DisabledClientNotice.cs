using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace HttpResilience.NET.Internal;

/// <summary>
/// Event 6: a registered client has resilience switched off, naming the key that did it.
/// </summary>
internal static class DisabledClientNotice
{
    public static void Report(
        ILogger logger,
        RegisteredClient client,
        HttpResilienceOptions options,
        IConfigurationSection? root)
    {
        if (options.Enabled)
        {
            return;
        }

        string path = root is not null && RootSectionRules.IsSwitchedOff(root)
            ? $"{root.Path}:Enabled"
            : $"{client.Scope}:Enabled";

        HttpResilienceLogging.ResilienceDisabled(logger, client.Name, path);
    }
}
