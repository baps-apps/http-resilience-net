using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HttpResilience.NET.Internal;

/// <summary>
/// One client this package configures.
/// </summary>
/// <param name="Name">The HTTP client's name, which is also its options name.</param>
/// <param name="SectionName">Its section under <c>Clients</c>, or empty for the unnamed client, which reads root values only.</param>
/// <param name="Scope">The configuration path its values come from, used in messages.</param>
/// <param name="Kind">The pipeline it runs.</param>
internal sealed record RegisteredClient(string Name, string SectionName, string Scope, PipelineKind Kind);

/// <summary>
/// State shared by <c>services.AddHttpResilience</c> and the per-client registrations: the root section and
/// the ledger of registered clients. Created by whichever call comes first, so the two may run in any order.
/// </summary>
internal sealed class HttpResilienceRegistration
{
    private readonly ConcurrentDictionary<string, RegisteredClient> _clients = new(StringComparer.Ordinal);
    private readonly List<RegisteredClient> _ordered = [];

    private readonly ConcurrentDictionary<string, byte> _sections = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _replacementsReported = new(StringComparer.Ordinal);
    private readonly List<Action<HttpResilienceOptions>> _rootConfigures = [];
    private readonly List<IConfigurationSection> _repeatedSections = [];

    /// <summary>
    /// An empty root, for an application that configures in code only. Every raw-section rule finds nothing in it.
    /// </summary>
    private static readonly IConfigurationSection s_emptyRoot =
        new ConfigurationBuilder().Build().GetSection(HttpResilienceConfigurationKeys.RootSection);

    /// <summary>
    /// The root section, or <see langword="null"/> until <c>services.AddHttpResilience</c> is called with one.
    /// </summary>
    public IConfigurationSection? Section { get; set; }

    /// <summary>
    /// Records a later <c>AddHttpResilience</c> call with the root section's path from another configuration
    /// object. Same path is not same section: its values are compared when options are built.
    /// </summary>
    public void AddRepeatedSection(IConfigurationSection section)
    {
        lock (_repeatedSections)
        {
            _repeatedSections.Add(section);
        }
    }

    /// <summary>Whether the services shared by every client have been registered.</summary>
    public bool CoreServicesRegistered { get; set; }

    /// <summary>
    /// The root delegates passed to <c>services.AddHttpResilience(configure)</c>, in registration order. Applied
    /// after the root section and before each client's section.
    /// </summary>
    public IReadOnlyList<Action<HttpResilienceOptions>> RootConfigures
    {
        get
        {
            lock (_rootConfigures)
            {
                return [.. _rootConfigures];
            }
        }
    }

    public void AddRootConfigure(Action<HttpResilienceOptions> configure)
    {
        lock (_rootConfigures)
        {
            _rootConfigures.Add(configure);
        }
    }

    public IReadOnlyCollection<string> Clients => [.. RegisteredClients.Select(client => client.Name)];

    /// <summary>The registered clients, in registration order.</summary>
    public IReadOnlyList<RegisteredClient> RegisteredClients
    {
        get
        {
            lock (_ordered)
            {
                return [.. _ordered];
            }
        }
    }

    public IReadOnlyCollection<string> ConsumedSections => _sections.Keys.ToArray();

    /// <summary>
    /// The root section; an empty one when the application configures in code only; otherwise an exception
    /// naming the missing call.
    /// </summary>
    public IConfigurationSection RequireSection(string clientName)
    {
        if (Section is null)
        {
            return RootConfigures.Count > 0 ? s_emptyRoot : throw new InvalidOperationException(
                $"HTTP client '{clientName}' uses HttpResilience, but services.AddHttpResilience was never called, " +
                $"so there is no '{HttpResilienceConfigurationKeys.RootSection}' configuration section and no root " +
                "values in code to read. Call AddHttpResilience(configuration) or AddHttpResilience(options => ...) " +
                "once, anywhere in service registration.");
        }

        RequireRepeatsToAgree(Section);
        return Section;
    }

    /// <summary>
    /// A repeat from another configuration object with the same values changes nothing; with different values the
    /// first call's would win silently, so it fails, naming a key that differs.
    /// </summary>
    private void RequireRepeatsToAgree(IConfigurationSection section)
    {
        IConfigurationSection[] repeats;
        lock (_repeatedSections)
        {
            repeats = [.. _repeatedSections];
        }

        if (repeats.Length == 0)
        {
            return;
        }

        Dictionary<string, string?> first = Flatten(section);
        foreach (IConfigurationSection repeat in repeats)
        {
            Dictionary<string, string?> other = Flatten(repeat);
            string? differing = first.Keys.Union(other.Keys, StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(key => !string.Equals(
                    first.GetValueOrDefault(key), other.GetValueOrDefault(key), StringComparison.Ordinal));

            if (differing is not null)
            {
                throw new InvalidOperationException(
                    $"AddHttpResilience was called more than once with configuration section '{section.Path}', " +
                    $"from different configuration objects whose values differ: {section.Path}:{differing} is " +
                    $"'{first.GetValueOrDefault(differing)}' in the first and '{other.GetValueOrDefault(differing)}' " +
                    "in a later one. Only the first would be read. Pass the application's configuration to every " +
                    "AddHttpResilience call.");
            }
        }
    }

    private static Dictionary<string, string?> Flatten(IConfigurationSection section) =>
        section.AsEnumerable(makePathsRelative: true)
            .Where(pair => pair.Value is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Records a client, returning <see langword="false"/> if it was already registered.
    /// </summary>
    public bool TryAddClient(RegisteredClient client)
    {
        lock (_ordered)
        {
            if (!_clients.TryAdd(client.Name, client))
            {
                return false;
            }

            _ordered.Add(client);
            return true;
        }
    }

    /// <summary>
    /// The pipeline kind registered for a client, or <see langword="null"/> for a client this package does
    /// not configure.
    /// </summary>
    public PipelineKind? KindOf(string clientName) =>
        _clients.TryGetValue(clientName, out RegisteredClient? client) ? client.Kind : null;

    public void MarkSectionConsumed(string sectionName)
    {
        if (!string.IsNullOrEmpty(sectionName))
        {
            _sections.TryAdd(sectionName, 0);
        }
    }

    public bool IsSectionConsumed(string sectionName) => _sections.ContainsKey(sectionName);

    /// <summary>
    /// Returns <see langword="true"/> the first time it is called for a client, so the replacement of earlier
    /// resilience handlers (event 17) is logged once rather than on every handler rotation.
    /// </summary>
    public bool MarkReplacementReported(string clientName) => _replacementsReported.TryAdd(clientName, 0);

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

    /// <summary>
    /// Returns the registration in <paramref name="services"/>, adding one if there is none. Never replaces an
    /// existing instance, because its client ledger is what refuses a duplicate registration.
    /// </summary>
    public static HttpResilienceRegistration GetOrAdd(IServiceCollection services)
    {
        if (Find(services) is { } existing)
        {
            return existing;
        }

        var registration = new HttpResilienceRegistration();
        services.AddSingleton(registration);
        return registration;
    }
}
