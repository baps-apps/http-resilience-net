namespace HttpResilience.NET;

/// <summary>
/// What this package checks while the host starts. Applies to the application as a whole, not to one client.
/// </summary>
/// <remarks>
/// Read from the root section (<c>HttpResilience:ValidateClientsOnStart</c>, <c>AllowUnusedClientSections</c>,
/// <c>AllowUnknownKeys</c>). A value set in code wins over configuration, whichever is registered first. A
/// client section cannot state these keys.
/// </remarks>
/// <example>
/// <code language="csharp">
/// builder.Services.Configure&lt;HttpResilienceStartupOptions&gt;(options => options.ValidateClientsOnStart = false);
/// </code>
/// <code language="json">
/// { "HttpResilience": { "ValidateClientsOnStart": false } }
/// </code>
/// </example>
public sealed class HttpResilienceStartupOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether every client this package configures is created once while the
    /// host starts, so a client whose handler chain cannot be built fails the deployment rather than its first
    /// request. Defaults to <see langword="true"/>.
    /// </summary>
    public bool ValidateClientsOnStart { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether a section under <c>HttpResilience:Clients</c> that no registered
    /// client reads is allowed. Defaults to <see langword="false"/>, which fails startup.
    /// </summary>
    /// <remarks>
    /// Set it when one configuration file is deliberately shared by services that register different subsets
    /// of its clients.
    /// </remarks>
    public bool AllowUnusedClientSections { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a key this package does not recognize is allowed. Defaults to
    /// <see langword="false"/>, which fails startup naming the key and the nearest real one.
    /// </summary>
    /// <remarks>
    /// For one configuration file shared by services running different versions of this package, prefer stating
    /// <c>HttpResilience:SchemaVersion</c>: a key added in a newer minor version is then a startup warning on the
    /// older services, and a typo in a file written for their own version still fails. This switch turns the check
    /// off entirely.
    /// </remarks>
    public bool AllowUnknownKeys { get; set; }
}
