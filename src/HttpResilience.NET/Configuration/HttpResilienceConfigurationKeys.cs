namespace HttpResilience.NET;

/// <summary>
/// The configuration paths this library reads, for code that has to build or inspect them.
/// </summary>
/// <remarks>
/// Needed only when composing paths in code, for example to supply organization-wide defaults.
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
    /// The child section holding per-client overrides, <c>Clients</c>: a client named <c>Orders</c> reads
    /// <c>HttpResilience:Clients:Orders</c>.
    /// </summary>
    public const string ClientsSection = "Clients";
}
