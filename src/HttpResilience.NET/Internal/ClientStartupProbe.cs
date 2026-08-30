using Microsoft.Extensions.Hosting;

namespace HttpResilience.NET.Internal;

/// <summary>
/// A marker registered by <c>ValidateHttpResilienceClientsOnStart</c>, so the probe can tell an explicit
/// request apart from its own default.
/// </summary>
/// <remarks>
/// Taken by the probe as an <see cref="IEnumerable{T}"/> because that is the one shape a container answers
/// with an empty result rather than a resolution failure when nothing registered it.
/// </remarks>
internal sealed class ExplicitClientProbeRequest;

/// <summary>
/// Creates every client this package configured, once, while the host is starting.
/// </summary>
/// <remarks>
/// Registered by <c>AddHttpResilience</c> itself. It was opt-in until the fourth review, on the reasoning
/// that eagerly constructing every handler chain has a cost and is wrong for a process that registers
/// clients it may never use -- which is weak for a client that has explicitly opted into a resilience
/// pipeline, and left the only control over a deployment-versus-first-request failure sitting in a checklist
/// item that every adopting service has to remember separately.
/// <para>
/// Opting out is <c>HttpResilience:ValidateClientsOnStart</c>, read from the raw section rather than from
/// bound options for the same reason as <c>AllowUnusedClientSections</c>: it is a statement about the
/// process, and binding it would make it look inheritable per client when it is not. A configuration key
/// rather than a code change so it is reachable during an incident.
/// </para>
/// </remarks>
internal sealed class ClientStartupProbe : IHostedService
{
    internal const string EnabledKey = "ValidateClientsOnStart";

    private readonly IHttpClientFactory _factory;
    private readonly HttpResilienceRegistration _registration;
    private readonly bool _requestedInCode;

    public ClientStartupProbe(
        IHttpClientFactory factory,
        HttpResilienceRegistration registration,
        IEnumerable<ExplicitClientProbeRequest> explicitRequests)
    {
        ArgumentNullException.ThrowIfNull(explicitRequests);

        _factory = factory;
        _registration = registration;
        _requestedInCode = explicitRequests.Any();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!IsEnabled())
        {
            return Task.CompletedTask;
        }

        foreach (string client in _registration.Clients)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _factory.CreateClient(client).Dispose();
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Whether to create the clients, refusing the two ways of leaving that question unanswered.
    /// </summary>
    private bool IsEnabled()
    {
        string path = $"{_registration.Section.Path}:{EnabledKey}";
        string? stated = _registration.Section[EnabledKey];

        if (stated is null)
        {
            return true;
        }

        // Not defaulted either way: an operator who wrote "yes" would otherwise watch the probe keep running
        // on a key they believed they had turned off, or stop running on one they believed they had turned
        // on, with nothing naming the key in either case.
        if (!bool.TryParse(stated, out bool enabled))
        {
            throw new InvalidOperationException(
                $"{path} -- value '{stated}' is invalid. Expected 'true' or 'false'. Reason: this key " +
                "decides whether every client this package configured is created while the host starts, so " +
                "that a client whose handler chain cannot be built fails the deployment rather than the " +
                "first request that reaches it. A value that is neither leaves that ambiguous.");
        }

        if (enabled)
        {
            return true;
        }

        // Two written statements about the same thing, one of which is not in force. The call would win, so
        // the key is the one that would silently do nothing -- and the person reaching for a configuration
        // key during an incident is exactly who most needs telling that a line of code overrides them.
        if (_requestedInCode)
        {
            throw new InvalidOperationException(
                $"{path} is false, but ValidateHttpResilienceClientsOnStart() is also called in code. Two " +
                "statements about whether this package's clients are created at startup, and only one can " +
                "be in force. Remove the ValidateHttpResilienceClientsOnStart() call -- it has been " +
                $"redundant since the probe became the default -- or remove {path}.");
        }

        return false;
    }
}
