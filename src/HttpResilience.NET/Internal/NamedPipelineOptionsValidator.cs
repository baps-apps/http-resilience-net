using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace HttpResilience.NET.Internal;

/// <summary>
/// Validates one client's options when they are first built, against the rules for the pipeline that client
/// uses. Runs at host start under <c>ValidateOnStart</c>, and otherwise when the client is first created.
/// </summary>
internal sealed class NamedPipelineOptionsValidator : IValidateOptions<HttpResilienceOptions>
{
    private readonly string _optionsName;
    private readonly string _sectionName;
    private readonly string _scope;
    private readonly PipelineKind _kind;
    private readonly HttpResilienceRegistration _registration;
    private readonly IOptions<HttpResilienceStartupOptions> _startup;

    public NamedPipelineOptionsValidator(
        string optionsName,
        string sectionName,
        string scope,
        PipelineKind kind,
        HttpResilienceRegistration registration,
        IOptions<HttpResilienceStartupOptions> startup)
    {
        _optionsName = optionsName;
        _sectionName = sectionName;
        _scope = scope;
        _kind = kind;
        _registration = registration;
        _startup = startup;
    }

    public ValidateOptionsResult Validate(string? name, HttpResilienceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!string.Equals(name, _optionsName, StringComparison.Ordinal))
        {
            return ValidateOptionsResult.Skip;
        }

        IConfigurationSection root = _registration.RequireSection(_optionsName);

        List<string> failures = [.. HttpResilienceOptionsValidator.Collect(options, _scope, _kind)];
        failures.AddRange(CollectInertConfiguration(root, options));

        // Every client inherits the root, so a root rule violation fails every client that would inherit it.
        if (RootSectionRules.CollectUnsafeMethodGuardFailures(root) is { } rootFailures)
        {
            failures.AddRange(rootFailures);
        }

        if (OptionsComposition.RootDelegateFailuresOf(options) is { } delegateFailures)
        {
            failures.AddRange(delegateFailures);
        }

        failures.AddRange(RenamedKeyRules.Collect(root));
        IConfigurationSection? client = string.IsNullOrEmpty(_sectionName)
            ? null
            : root.GetSection(HttpResilienceConfigurationKeys.ClientsSection).GetSection(_sectionName);

        if (client is not null)
        {
            failures.AddRange(RenamedKeyRules.Collect(client));
        }

        // A file written for a newer minor may hold keys this version lacks: event 18 reports them instead.
        if (!_startup.Value.AllowUnknownKeys && !SchemaVersionRules.IsNewerMinor(root))
        {
            failures.AddRange(UnknownKeyRules.Collect(root, isRoot: true, root.Path));
            if (client is not null)
            {
                failures.AddRange(UnknownKeyRules.Collect(client, isRoot: false, root.Path));
            }
        }

        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }

    /// <summary>
    /// Reports keys in the client's own section that its pipeline never reads.
    /// </summary>
    /// <remarks>
    /// Only the client's section is checked. Root values are shared by standard and hedged clients alike.
    /// </remarks>
    private IEnumerable<string> CollectInertConfiguration(IConfigurationSection root, HttpResilienceOptions options)
    {
        if (string.IsNullOrEmpty(_sectionName))
        {
            yield break;
        }

        IConfigurationSection client = root
            .GetSection(HttpResilienceConfigurationKeys.ClientsSection)
            .GetSection(_sectionName);

        if (_kind is PipelineKind.Hedging && client.GetSection("Retry").Exists())
        {
            yield return
                $"{_scope} -- Retry: this client uses AddHttpResilienceHedgingHandler, which has no retry strategy, so " +
                "these keys are bound and never read. Remove them or use AddHttpResilienceHandler. To control " +
                "duplicate mutating requests on this pipeline, use Hedging:DisableForUnsafeHttpMethods.";
        }

        if (_kind is not PipelineKind.Standard)
        {
            yield break;
        }

        if (client.GetSection("Hedging").Exists())
        {
            yield return
                $"{_scope} -- Hedging: this client uses AddHttpResilienceHandler, which has no hedging strategy, so " +
                "these keys are bound and never read. Remove them or use AddHttpResilienceHedgingHandler. To control " +
                "duplicate mutating requests on this pipeline, use Retry:DisableForUnsafeHttpMethods.";
        }

        if (client.GetSection("PipelineSelection:Authorities").Exists() &&
            options.PipelineSelection.Mode is HttpResiliencePipelineSelectionMode.None)
        {
            yield return
                $"{_scope} -- PipelineSelection:Authorities: the client lists authorities but " +
                "PipelineSelection:Mode is None, so per-authority circuit breaker isolation silently does not " +
                "happen. Set Mode to ByAuthority or remove the list. A list inherited from " +
                $"{HttpResilienceConfigurationKeys.RootSection}:PipelineSelection:Authorities is allowed.";
        }

        if (client.GetSection("Retry:DisableForUnsafeHttpMethods").Exists() &&
            options.Retry.DisableForUnsafeHttpMethods &&
            options.Retry.RetryableMethods is { Count: > 0 } inForce)
        {
            yield return
                $"{_scope} -- Retry:DisableForUnsafeHttpMethods: Retry:RetryableMethods replaces this flag, so " +
                $"it is bound and never read; the methods actually retried are {string.Join(", ", inForce)}. " +
                "The list may be inherited from " +
                $"{HttpResilienceConfigurationKeys.RootSection}:Retry:RetryableMethods. State " +
                "Retry:RetryableMethods on this client (an empty list restores GET, HEAD, OPTIONS and TRACE), " +
                "or remove the flag.";
        }
    }
}
