using System.Globalization;
using HttpResilience.NET.Configuration;
using HttpResilience.NET.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace HttpResilience.NET.Internal;

/// <summary>
/// Fails startup when a section under <c>HttpResilience:Clients</c> is read by no registered client.
/// </summary>
/// <remarks>
/// The run-time state of a client whose section is misspelled is identical to the state of a client that was
/// never given one: it runs on root defaults. Every other configuration in this package that produces a state
/// indistinguishable from a mistake is reported -- the disabled-client Warning, the allow-list under
/// <c>Mode: None</c>, <c>Retry:*</c> keys on a hedged client -- and this was the one that was not.
/// <para>
/// Two shapes make it ordinary rather than theoretical. A typed client takes its name from <c>TClient</c>, so
/// <c>AddHttpClient&lt;IOrdersApi, OrdersApi&gt;()</c> reads <c>Clients:IOrdersApi</c> while the section an
/// operator writes by hand is <c>Clients:OrdersApi</c>. And renaming or deleting a client leaves its section
/// behind, still valid, still bound, read by nobody.
/// </para>
/// <para>
/// It cannot be checked eagerly at registration the way every other rule is, because a section unread when
/// the third client registers may be read by the fourth. It runs once, against the root options, at the point
/// <c>ValidateOnStart</c> materializes them -- by which time the service collection is built and the ledger
/// is complete.
/// </para>
/// </remarks>
internal sealed class UnusedClientSectionValidator : IValidateOptions<HttpResilienceOptions>
{
    internal const string AllowUnusedKey = "AllowUnusedClientSections";

    private readonly IConfigurationSection _root;
    private readonly HttpResilienceRegistration _registration;

    public UnusedClientSectionValidator(IConfigurationSection root, HttpResilienceRegistration registration)
    {
        _root = root;
        _registration = registration;
    }

    public ValidateOptionsResult Validate(string? name, HttpResilienceOptions options)
    {
        // The root options, once. A per-client run would repeat the same whole-file finding for every client.
        if (!string.IsNullOrEmpty(name) && name != Microsoft.Extensions.Options.Options.DefaultName)
        {
            return ValidateOptionsResult.Skip;
        }

        string? allowUnused = _root[AllowUnusedKey];
        if (allowUnused is not null)
        {
            if (!bool.TryParse(allowUnused, out bool allowed))
            {
                // Not defaulted to false: an operator who wrote "yes" would then see startup fail on a
                // section they believed they had allowed, with a message that never mentioned this key.
                return ValidateOptionsResult.Fail(
                    $"{_root.Path}:{AllowUnusedKey} -- value '{allowUnused}' is invalid. Expected 'true' or " +
                    "'false'. Reason: this key decides whether a client section that no client reads fails " +
                    "startup, and a value that is neither leaves that ambiguous.");
            }

            if (allowed)
            {
                return ValidateOptionsResult.Success;
            }
        }

        List<string>? failures = null;
        foreach (IConfigurationSection client in
                 _root.GetSection(HttpResilienceConfigurationKeys.ClientsSection).GetChildren())
        {
            if (_registration.IsSectionConsumed(client.Key))
            {
                continue;
            }

            (failures ??= []).Add(Describe(client.Path));
        }

        return failures is null
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private string Describe(string path) => string.Create(CultureInfo.InvariantCulture,
        $"{path} -- no registered HTTP client reads this section, so every key under it is bound to nothing " +
        $"and has no effect. A client reads the section named after itself unless AddHttpResilience or " +
        $"AddHedgedHttpResilience is passed a name -- and a typed client is named after TClient, so " +
        $"AddHttpClient<IOrdersApi, OrdersApi>() reads 'IOrdersApi', not 'OrdersApi'. {ReadSections()} " +
        $"Rename this section, pass its name to the client's registration, or remove it. Set " +
        $"{_root.Path}:{AllowUnusedKey} to true if one configuration file is deliberately shared by services " +
        $"that register different subsets of the clients in it.");

    private string ReadSections() =>
        _registration.ConsumedSections.Count == 0
            ? "No client section is read at all: every registered client either asked for root values only " +
              "or none was registered."
            : $"Sections that are read: {string.Join(", ", _registration.ConsumedSections.Order(StringComparer.Ordinal))}.";
}
