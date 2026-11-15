using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace HttpResilience.NET.Internal;

/// <summary>
/// Fails startup when a section under <c>HttpResilience:Clients</c> is read by no registered client.
/// </summary>
internal sealed class UnusedClientSectionValidator : IValidateOptions<HttpResilienceOptions>
{
    private readonly HttpResilienceRegistration _registration;
    private readonly IOptions<HttpResilienceStartupOptions> _startup;

    public UnusedClientSectionValidator(
        HttpResilienceRegistration registration,
        IOptions<HttpResilienceStartupOptions> startup)
    {
        _registration = registration;
        _startup = startup;
    }

    /// <summary>Read when validating, because a configuration call may follow a code-only one.</summary>
    private IConfigurationSection? Root => _registration.Section;

    public ValidateOptionsResult Validate(string? name, HttpResilienceOptions options)
    {
        if (!string.IsNullOrEmpty(name) && name != Options.DefaultName)
        {
            return ValidateOptionsResult.Skip;
        }

        // Code-only registration: there is no Clients section to leave unused.
        if (Root is not { } root || _startup.Value.AllowUnusedClientSections)
        {
            return ValidateOptionsResult.Success;
        }

        List<string>? failures = null;
        foreach (IConfigurationSection client in
                 root.GetSection(HttpResilienceConfigurationKeys.ClientsSection).GetChildren())
        {
            if (_registration.IsSectionConsumed(client.Key))
            {
                continue;
            }

            (failures ??= []).Add(Describe(client.Path, root.Path));
        }

        return failures is null
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private string Describe(string path, string rootPath) => string.Create(CultureInfo.InvariantCulture,
        $"{path} -- no registered HTTP client reads this section, so every key under it is bound to nothing " +
        $"and has no effect. A client reads the section named after itself unless AddHttpResilienceHandler or " +
        $"AddHttpResilienceHedgingHandler is passed a name -- and a typed client is named after TClient, so " +
        $"AddHttpClient<IOrdersApi, OrdersApi>() reads 'IOrdersApi', not 'OrdersApi'. {ReadSections()} " +
        $"Rename this section, pass its name to the client's registration, or remove it. Set " +
        $"{rootPath}:{nameof(HttpResilienceStartupOptions.AllowUnusedClientSections)} to true if one configuration file is deliberately shared by services " +
        $"that register different subsets of the clients in it.");

    private string ReadSections() =>
        _registration.ConsumedSections.Count == 0
            ? "No client section is read at all: every registered client either asked for root values only " +
              "or none was registered."
            : $"Sections that are read: {string.Join(", ", _registration.ConsumedSections.Order(StringComparer.Ordinal))}.";
}
