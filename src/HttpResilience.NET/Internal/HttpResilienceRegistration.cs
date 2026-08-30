using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HttpResilience.NET.Internal;

/// <summary>
/// The root configuration section, handed from <c>AddHttpResilience</c> to the per-client builder extension,
/// plus the ledger of what has already been registered against it.
/// </summary>
/// <remarks>
/// Held as a registered <i>instance</i> so a client registration can read configuration while the service
/// collection is still being built. Replacing the instance would re-arm the duplicate-client guard below,
/// which is why the root registration is idempotent rather than last-wins.
/// </remarks>
internal sealed class HttpResilienceRegistration
{
    // Concurrent collections rather than HashSet. In practice these are written during service registration
    // and read at startup, with BuildServiceProvider as the barrier between the two -- but nothing in the
    // options pattern or in IServiceCollection requires registration to be single-threaded, and a container
    // populated in parallel would corrupt a plain HashSet silently. The cost is one allocation per entry on
    // a path that runs once per client at startup.
    private readonly ConcurrentDictionary<string, byte> _clients = new(StringComparer.Ordinal);

    // Ordinal-ignore-case because configuration keys are: GetSection("orders") finds "Orders", so a section
    // read under either spelling has been read.
    private readonly ConcurrentDictionary<string, byte> _sections = new(StringComparer.OrdinalIgnoreCase);

    // How many resilience handlers this package added per client, so a handler the package did not add is
    // detectable. See ResilienceHandlerCountFilter.
    private readonly ConcurrentDictionary<string, int> _resilienceHandlers = new(StringComparer.Ordinal);

    public HttpResilienceRegistration(IConfigurationSection section) => Section = section;

    public IConfigurationSection Section { get; }

    // Snapshots. Both are read at startup, twice in total, and a stable view is what the callers want:
    // UnusedClientSectionValidator orders and joins its copy into a message, and ClientStartupProbe creates
    // a client for each entry.
    public IReadOnlyCollection<string> Clients => _clients.Keys.ToArray();

    public IReadOnlyCollection<string> ConsumedSections => _sections.Keys.ToArray();

    /// <summary>
    /// Records a client, returning <see langword="false"/> if it already had a pipeline.
    /// </summary>
    public bool TryAddClient(string clientName) => _clients.TryAdd(clientName, 0);

    public void MarkSectionConsumed(string sectionName)
    {
        if (!string.IsNullOrEmpty(sectionName))
        {
            _sections.TryAdd(sectionName, 0);
        }
    }

    public bool IsSectionConsumed(string sectionName) => _sections.ContainsKey(sectionName);

    /// <summary>
    /// Records how many resilience handlers this package added to a client.
    /// </summary>
    /// <remarks>
    /// Recorded only for a client this package actually gave a pipeline to. A client with
    /// <c>Enabled: false</c> is deliberately absent, so it may add the platform's own handler -- which is how
    /// a client migrates onto this package one step at a time, and the opposite of what
    /// <c>Enabled: false</c> would mean if the guard applied to it.
    /// </remarks>
    public void RecordResilienceHandlers(string clientName, int count) =>
        _resilienceHandlers[clientName] = count;

    /// <summary>
    /// How many resilience handlers this package added, or <see langword="null"/> for a client it did not
    /// configure.
    /// </summary>
    public int? ExpectedResilienceHandlers(string clientName) =>
        _resilienceHandlers.TryGetValue(clientName, out int count) ? count : null;

    public static HttpResilienceRegistration? Find(IServiceCollection services)
    {
        foreach (ServiceDescriptor descriptor in services)
        {
            if (descriptor.ServiceType == typeof(HttpResilienceRegistration) &&
                descriptor.ImplementationInstance is HttpResilienceRegistration registration)
            {
                return registration;
            }
        }

        return null;
    }
}
