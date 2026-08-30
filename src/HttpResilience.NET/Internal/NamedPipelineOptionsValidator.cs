using HttpResilience.NET.Options;
using Microsoft.Extensions.Options;

namespace HttpResilience.NET.Internal;

/// <summary>
/// Validates one client's options at startup, against the rules for the pipeline that client actually uses,
/// and against the handler composition its registration already fixed.
/// </summary>
/// <remarks>
/// A shared validator cannot do this: it sees an options name but not whether that client was registered with
/// the standard or the hedging pipeline, and the two have different budget rules -- retry options are bound
/// but never used on the hedging path. Registering one validator per client keeps every rule accurate and
/// keeps every message pointed at the configuration section an operator would edit.
/// </remarks>
internal sealed class NamedPipelineOptionsValidator : IValidateOptions<HttpResilienceOptions>
{
    private readonly string _optionsName;
    private readonly string _scope;
    private readonly PipelineKind _kind;
    private readonly StructuralDecisions _structural;

    public NamedPipelineOptionsValidator(
        string optionsName, string scope, PipelineKind kind, StructuralDecisions structural)
    {
        _optionsName = optionsName;
        _scope = scope;
        _kind = kind;
        _structural = structural;
    }

    public ValidateOptionsResult Validate(string? name, HttpResilienceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!string.Equals(name, _optionsName, StringComparison.Ordinal))
        {
            return ValidateOptionsResult.Skip;
        }

        List<string> failures = [.. HttpResilienceOptionsValidator.Collect(options, _scope, _kind)];
        failures.AddRange(UnreachableChanges(options));

        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }

    /// <summary>
    /// Reports a change to a decision the registration has already acted on and cannot revisit.
    /// </summary>
    /// <remarks>
    /// Values reach the pipeline on their own: it reads <c>IOptionsMonitor</c> when it is built, which is
    /// after every <c>Configure</c> and <c>PostConfigure</c>, so a consumer changing a timeout changes what
    /// runs and there is nothing to detect. Handler composition is different -- it was settled while the
    /// service collection was being built -- so a change to one of these would be reported without being in
    /// effect. That fails startup rather than being tolerated, for the same reason the whole snapshot
    /// comparison used to: reporting a number a service is not using is worse than refusing to start.
    /// </remarks>
    private IEnumerable<string> UnreachableChanges(HttpResilienceOptions options)
    {
        StructuralDecisions current = StructuralDecisions.From(options);
        if (current == _structural)
        {
            yield break;
        }

        foreach (string difference in current.Differences(_structural))
        {
            yield return
                $"{_scope} -- {difference}: this setting decides which handlers the client is built from, " +
                "and a client's handlers are fixed when the service collection is built. Something changed " +
                "it afterwards -- a Configure or PostConfigure registered after the client -- so the value " +
                "would be reported without being in effect. Every other setting does reach the pipeline " +
                "this way; only the ones that add or remove a handler cannot. Move this one to the " +
                "'configure' parameter on AddHttpResilience or AddHedgedHttpResilience, or to the " +
                "configuration section itself.";
        }
    }

}
