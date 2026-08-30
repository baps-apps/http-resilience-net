namespace HttpResilience.NET.Configuration;

/// <summary>
/// The configuration paths this library reads, for code that has to build or inspect them.
/// </summary>
/// <remarks>
/// You do not need these to configure a client -- write the JSON and call
/// <c>AddHttpResilience(configuration)</c>. They exist for the cases where a path has to be composed in code:
/// a platform package layering defaults with <c>AddInMemoryCollection</c>, a configuration test, or an
/// operator tool that reports effective settings.
/// </remarks>
/// <example>
/// <code language="csharp">
/// // Supply an organization-wide default that an application can still override.
/// var defaults = new Dictionary&lt;string, string?&gt;
/// {
///     [$"{HttpResilienceConfigurationKeys.RootSection}:Enabled"] = "true",
///     [$"{HttpResilienceConfigurationKeys.RootSection}:{HttpResilienceConfigurationKeys.ClientsSection}:Orders:Timeout:Total"] = "00:00:10"
/// };
///
/// builder.Configuration.AddInMemoryCollection(defaults);
/// </code>
/// </example>
public static class HttpResilienceConfigurationKeys
{
    /// <summary>The root configuration section, <c>HttpResilience</c>.</summary>
    public const string RootSection = "HttpResilience";

    /// <summary>
    /// The child section holding per-client overrides, <c>Clients</c>, so a client named <c>Orders</c> is
    /// configured at <c>HttpResilience:Clients:Orders</c>.
    /// </summary>
    /// <remarks>
    /// Per-client configuration is namespaced under its own child rather than sitting beside the schema's own
    /// properties, so a client may be named <c>Retry</c> or <c>Timeout</c> without colliding with them.
    /// </remarks>
    public const string ClientsSection = "Clients";
}
