using HttpResilience.NET.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HttpResilience.NET.Internal;

/// <summary>
/// One log line, at startup, for every client configured to repeat a request the package would otherwise
/// never repeat.
/// </summary>
/// <remarks>
/// The counterpart to <see cref="DisabledClientNotice"/>, and registered for the same reason. That type
/// exists because a client with resilience switched off is indistinguishable at run time from a client whose
/// <c>Enabled</c> key was forgotten, and the state is invisible until the dependency fails. A client that
/// repeats mutating requests is the same shape of hazard and a larger one: the state is invisible until the
/// origin is billed twice, and nothing in the pipeline's own telemetry distinguishes a retried POST from a
/// retried GET.
/// <para>
/// Both mechanisms are reported, not only the blunt one. <c>Retry:RetryableMethods</c> is the supported,
/// reviewable way to opt a method in and it is still worth a line, because "which of our clients can
/// duplicate a mutation?" is a fleet inventory question an operator should be able to answer from logs
/// during an incident rather than by grepping configuration across repositories. The methods are named
/// exactly, so an allow-list of only safe methods produces nothing.
/// </para>
/// <para>
/// Warning rather than Information for the same reason as
/// <see cref="HttpResilienceLogging.ResilienceDisabled"/>: this is the message that has to survive a
/// production log pipeline, and it is emitted once per client at host start, before traffic.
/// </para>
/// </remarks>
internal sealed class UnsafeMethodNotice : IPostConfigureOptions<HttpResilienceOptions>
{
    private const string EveryUnsafeMethod =
        "POST, PUT, PATCH, DELETE and every method this package does not recognize";

    private readonly string _optionsName;
    private readonly string _clientName;
    private readonly string _scope;
    private readonly string _allowListPath;
    private readonly PipelineKind _kind;
    private readonly ILogger? _logger;
    private int _reported;

    /// <param name="optionsName">The named options this notice reports on.</param>
    /// <param name="clientName">The HTTP client's name, as it appears in the message.</param>
    /// <param name="scope">This client's configuration path, for the two flag keys.</param>
    /// <param name="allowListPath">
    /// Where <c>Retry:RetryableMethods</c> is actually stated, resolved at registration. It is not always
    /// under <paramref name="scope"/>: the list is inheritable, so a client that states no list of its own
    /// can still be retrying POST because of the root section. Naming this client's section unconditionally
    /// sent an operator following "remove that key" to a key that does not exist in the file -- on the one
    /// message whose job is to be actionable during an incident.
    /// </param>
    /// <param name="kind">Which pipeline this client runs, since the governing key differs.</param>
    /// <param name="loggerFactory">Logging, if the application has any.</param>
    public UnsafeMethodNotice(
        string optionsName,
        string clientName,
        string scope,
        string allowListPath,
        PipelineKind kind,
        ILoggerFactory? loggerFactory)
    {
        _optionsName = optionsName;
        _clientName = clientName;
        _scope = scope;
        _allowListPath = allowListPath;
        _kind = kind;
        _logger = loggerFactory?.CreateLogger("HttpResilience");
    }

    public void PostConfigure(string? name, HttpResilienceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (_logger is null || !string.Equals(name, _optionsName, StringComparison.Ordinal))
        {
            return;
        }

        // Read from the options rather than from the registration that created this, so a value changed by a
        // consumer's Configure or PostConfigure is the one reported -- the same rule the pipeline runs on.
        // A client with no pipeline repeats nothing, whatever the flags say.
        if (!options.Enabled)
        {
            return;
        }

        (string Methods, string Verb, string GuardPath)? repeated = Describe(options);
        if (repeated is not { } detail)
        {
            return;
        }

        if (Volatile.Read(ref _reported) != 0 || Interlocked.Exchange(ref _reported, 1) != 0)
        {
            return;
        }

        HttpResilienceLogging.UnsafeMethodsRepeated(
            _logger, _clientName, detail.Methods, detail.Verb, detail.GuardPath);
    }

    private (string Methods, string Verb, string GuardPath)? Describe(HttpResilienceOptions options)
    {
        if (_kind is PipelineKind.Hedging)
        {
            return options.Hedging.DisableForUnsafeHttpMethods
                ? null
                : (EveryUnsafeMethod, "hedged", $"{_scope}:Hedging:DisableForUnsafeHttpMethods");
        }

        if (!options.Retry.Enabled)
        {
            return null;
        }

        // The allow-list branch of StandardPipelineConfigurator.ConfigureRetry wins outright when it is
        // populated, so the two cases are reported the way the pipeline resolves them, not both at once.
        if (options.Retry.RetryableMethods is { Count: > 0 } allowList)
        {
            string? unsafeMethods = UnsafeEntries(allowList);
            return unsafeMethods is null
                ? null
                : (unsafeMethods, "retried", _allowListPath);
        }

        return options.Retry.DisableForUnsafeHttpMethods
            ? null
            : (EveryUnsafeMethod, "retried", $"{_scope}:Retry:DisableForUnsafeHttpMethods");
    }

    /// <summary>
    /// The entries in an allow-list that are not RFC 9110 safe methods, or <see langword="null"/> when every
    /// entry is one -- an allow-list of GET and HEAD changes nothing and deserves no line.
    /// </summary>
    private static string? UnsafeEntries(List<string> allowList)
    {
        List<string>? unsafeMethods = null;
        foreach (string method in allowList)
        {
            if (!HttpMethodPredicates.IsSafe(method))
            {
                (unsafeMethods ??= []).Add(method.ToUpperInvariant());
            }
        }

        return unsafeMethods is null ? null : string.Join(", ", unsafeMethods);
    }
}
