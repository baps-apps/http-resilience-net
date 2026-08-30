using HttpResilience.NET.Configuration;
using HttpResilience.NET.Internal;
using HttpResilience.NET.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers HTTP resilience configuration for the application. Call this once, before any client.
/// </summary>
public static class HttpResilienceServiceCollectionExtensions
{

    /// <summary>
    /// Registers HTTP resilience configuration from the <c>HttpResilience</c> section, validating it at
    /// startup.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Application configuration containing an <c>HttpResilience</c> section.</param>
    /// <returns>The service collection, for chaining.</returns>
    /// <remarks>
    /// This is step one of two. It registers the schema and the section; each client then opts in with
    /// <see cref="HttpResilienceHttpClientBuilderExtensions.AddHttpResilience"/>, which reads its own override section under
    /// <c>HttpResilience:Clients</c> and inherits everything it does not state.
    /// <para>
    /// Call it before registering any client. A client registration needs the section while the service
    /// collection is still being built -- handlers have to be added at registration time -- so calling it
    /// afterwards throws with a message saying so.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code language="csharp">
    /// builder.Services.AddHttpResilience(builder.Configuration);
    ///
    /// builder.Services.AddHttpClient&lt;IOrdersApi, OrdersApi&gt;().AddHttpResilience();
    /// builder.Services.AddHttpClient("Search").AddHedgedHttpResilience();
    /// </code>
    /// </example>
    public static IServiceCollection AddHttpResilience(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        return services.AddHttpResilience(configuration.GetSection(HttpResilienceConfigurationKeys.RootSection));
    }

    /// <summary>
    /// Registers HTTP resilience configuration from an explicit section, validating it at startup.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="section">
    /// The root resilience section. Per-client overrides are read from its <c>Clients</c> child.
    /// </param>
    /// <returns>The service collection, for chaining.</returns>
    /// <remarks>
    /// Use this overload when the schema does not live at the root <c>HttpResilience</c> key -- a service
    /// hosting several components under separate sections, say.
    /// <para>
    /// Idempotent: calling it again with the same section is a no-op, so a shared platform extension and the
    /// application that uses it may both call it without coordinating. Calling it with a <i>different</i>
    /// section throws, because clients registered on either side of the second call would silently read
    /// different configuration.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code language="csharp">
    /// builder.Services.AddHttpResilience(builder.Configuration.GetSection("Platform:Http"));
    /// </code>
    /// </example>
    /// <exception cref="InvalidOperationException">
    /// A previous call registered a different configuration section.
    /// </exception>
    public static IServiceCollection AddHttpResilience(this IServiceCollection services, IConfigurationSection section)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(section);

        // The ledger of clients that already have a pipeline is a field on the registered instance. Replacing
        // that instance re-arms a second AddHttpResilience on a client that already has one, which nests two
        // pipelines: three configured attempts become nine origin calls, the total timeout is applied twice,
        // and nothing throws or logs. So a repeat call keeps what is already there.
        if (HttpResilienceRegistration.Find(services) is { } existing)
        {
            if (!string.Equals(existing.Section.Path, section.Path, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"AddHttpResilience was already called with configuration section " +
                    $"'{existing.Section.Path}' and is now being called with '{section.Path}'. Clients " +
                    "registered before this call read the first section and clients registered after it " +
                    "would read the second, so the two would silently disagree about every value. Call " +
                    "AddHttpResilience once, with one section.");
            }

            return services;
        }

        // Checked here, against the raw section, rather than left to the options validator alone. The
        // validator's copy of this rule runs on the root options, which are materialized by ValidateOnStart
        // -- and that only happens when something calls IStartupValidator, which a generic host does and a
        // bare ServiceCollection does not. A guard that a hosting model can skip is not a guard, and this is
        // the one rule where the whole point is that it cannot be got round. Registration is also the
        // earliest possible point: it fails before a single client has read the section.
        RejectFleetWideUnsafeMethodGuards(section);

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<HttpResilienceOptions>, HttpResilienceOptionsValidator>());

        // Registered unconditionally rather than only by AddHttpResilienceHealthChecks. The tracker is what
        // makes breaker state observable at all, and tying it to the health check meant a service that
        // exports metrics but maps no health endpoint could not see an open circuit.
        services.TryAddSingleton<CircuitBreakerStateTracker>();

        // AddMetrics registers IMeterFactory, which owns the meter's lifetime and scopes it to this
        // container. See HttpResilienceMetrics for why that matters more than it sounds.
        services.AddMetrics();
        services.TryAddSingleton<HttpResilienceMetrics>();

        // Held as a registered instance so the per-client builder extension can read configuration while the
        // service collection is still being built, rather than at resolve time when it is too late to add handlers.
        var registration = new HttpResilienceRegistration(section);
        services.AddSingleton(registration);

        // The duplicate-registration guard covers this package's own API. A consumer calling the platform's
        // AddStandardResilienceHandler on the same client nests two pipelines just as effectively -- measured
        // at nine origin calls for one GET -- and only the composed handler chain shows it at all. Registered
        // unconditionally and once: it examines a client only when the registration has a tally for it, and it
        // reports rather than fails, for the reason on ResilienceHandlerCountFilter.
        // Type-based rather than a factory: TryAddEnumerable deduplicates on the implementation type and
        // refuses a factory descriptor outright, so a shared platform extension and the application both
        // calling AddHttpResilience would otherwise fail the container.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHttpMessageHandlerBuilderFilter, ResilienceHandlerCountFilter>());

        // The core IHttpClientFactory registration, which every extension in this package builds on and
        // which the probe below is activated with. Idempotent -- it is TryAdd-shaped throughout -- and until
        // the probe became the default it was always already there, because the only route to a client was
        // AddHttpClient. Registered unconditionally so that a shared platform extension calling
        // AddHttpResilience in a service with no outbound clients at all still starts, rather than failing
        // with "Unable to resolve service for type 'System.Net.Http.IHttpClientFactory'".
        services.AddHttpClient();

        // On by default. Which primary handler a client ends up with is a DI fact, not an options fact, so
        // ValidateOnStart cannot reach it -- the handler chain is not built until CreateClient. Left opt-in,
        // the only control over "deployment fails" versus "the first request that happens to reach this
        // client fails, hours later, as 500s" was a checklist item that every adopting service had to
        // remember separately. Opting out is HttpResilience:ValidateClientsOnStart. See ClientStartupProbe.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ClientStartupProbe>());

        // A section under Clients that no client reads is inert, and inert configuration reads exactly like
        // configuration that is in force. Checked at startup rather than at registration, because a section
        // unread when the third client registers may be read by the fourth. See UnusedClientSectionValidator.
        services.AddSingleton<IValidateOptions<HttpResilienceOptions>>(
            new UnusedClientSectionValidator(section, registration));

        // Configure rather than Bind: the resilience pipeline is built once at startup from these values, so
        // registering a reload token would let IOptionsMonitor report values that are not in effect.
        // Resilience configuration requires a restart. See docs/OPERATIONS.md.
        services.AddOptions<HttpResilienceOptions>()
            .Configure(options => section.Bind(options))
            .ValidateOnStart();

        return services;
    }

    /// <summary>
    /// Refuses either safe-method guard being switched off in the root section, before any client reads it.
    /// </summary>
    /// <remarks>
    /// Every client inherits the root, so <c>Retry:DisableForUnsafeHttpMethods: false</c> stated there is one
    /// key deciding that every standard client in the process -- including clients registered after this
    /// call, which state nothing -- may deliver POST, PUT, PATCH, DELETE and every unrecognized method to its
    /// origin more than once. Whether repeating a request is safe is a property of one endpoint's idempotency
    /// handling; there is no fleet-wide answer to it, so there is no fleet-wide way to say yes.
    /// <para>
    /// Read from the raw section rather than from bound options because the same rule in
    /// <c>HttpResilienceOptionsValidator</c> runs on the root options, which only materialize when something
    /// invokes <c>IStartupValidator</c> -- a generic host does, a bare <see cref="IServiceCollection"/> plus
    /// <c>BuildServiceProvider</c> does not. Both are kept: this one cannot be skipped by a hosting model,
    /// and that one still catches a <c>Configure&lt;HttpResilienceOptions&gt;</c> setting the flag in code.
    /// </para>
    /// <para>
    /// <c>Retry:RetryableMethods</c> at the root may <b>narrow</b> but never <b>widen</b>. A root list naming
    /// only safe methods is a fleet-wide restriction, which is what the inheritance model exists for; a root
    /// list naming an unsafe one reaches every standard client in the process exactly as the flag above does,
    /// and only the blast radius over unrecognized methods differs. So unsafe entries are refused here and
    /// belong under <c>Clients:{name}</c>.
    /// </para>
    /// </remarks>
    private static void RejectFleetWideUnsafeMethodGuards(IConfigurationSection section)
    {
        List<string>? failures = null;

        Check("Retry:DisableForUnsafeHttpMethods", "standard",
            "Retry:RetryableMethods on that client says which methods rather than 'every method we do not " +
            "recognize as safe'.");

        Check("Hedging:DisableForUnsafeHttpMethods", "hedged",
            "Hedged attempts are simultaneous, so unlike retries they give an origin's idempotency key no " +
            "serialization to rely on: this is the more dangerous of the two flags, not the less.");

        CheckRootAllowList(section, ref failures);

        if (failures is not null)
        {
            throw new OptionsValidationException(
                Microsoft.Extensions.Options.Options.DefaultName, typeof(HttpResilienceOptions), failures);
        }

        void Check(string key, string pipeline, string tail)
        {
            // Only a value that parses as false is refused. A malformed value is the binder's to report, and
            // an absent one is the default, which is true.
            if (!bool.TryParse(section[key], out bool guarded) || guarded)
            {
                return;
            }

            (failures ??= []).Add(
                $"{section.Path}:{key} -- value 'False' is invalid. Expected true at the root section, and " +
                $"false only under {section.Path}:{HttpResilienceConfigurationKeys.ClientsSection}:{{name}} " +
                $"for the one client that needs it. Reason: at the root this removes the guard for every " +
                $"{pipeline} client in the process at once, including clients registered later that state " +
                $"nothing, so a single key decides that mutating requests may be delivered to their origins " +
                $"more than once. That is a property of one endpoint's idempotency handling, not of a " +
                $"fleet. {tail}");
        }
    }

    /// <summary>
    /// Refuses an unsafe method in the <b>root</b> <c>Retry:RetryableMethods</c> list.
    /// </summary>
    /// <remarks>
    /// The list is inheritable so that a fleet can state one allow-list, and that stays true -- but only in
    /// the narrowing direction. A root list of <c>["GET"]</c> restricts every client to retrying GETs, which
    /// is strictly safer than the default and is exactly what one shared statement should be able to say. A
    /// root list naming POST decides that every standard client in the process, including clients registered
    /// after this call that state nothing, may deliver a mutating body to its origin more than once -- the
    /// same fleet-wide decision <c>Retry:DisableForUnsafeHttpMethods: false</c> is refused for, reached by a
    /// different key.
    /// <para>
    /// Confining unsafe entries to a client's own section also collapses the two guards onto one axis: the
    /// list and the flag can then only ever be stated in the same place, which is what makes the
    /// contradiction between them detectable at all. See
    /// <c>HttpResilienceHttpClientBuilderExtensions.CollectInertConfiguration</c>.
    /// </para>
    /// <para>
    /// Only well-formed method tokens are judged here. An entry that is not a method token could never match
    /// a request whatever its case, and belongs to the token rule in <c>HttpResilienceOptionsValidator</c>,
    /// which names it more usefully.
    /// </para>
    /// </remarks>
    private static void CheckRootAllowList(IConfigurationSection section, ref List<string>? failures)
    {
        foreach (IConfigurationSection entry in section.GetSection("Retry:RetryableMethods").GetChildren())
        {
            if (entry.Value is not { } method ||
                !HttpMethodPredicates.IsValidMethodToken(method) ||
                HttpMethodPredicates.IsSafe(method))
            {
                continue;
            }

            (failures ??= []).Add(
                $"{section.Path}:Retry:RetryableMethods -- value '{method}' is invalid at the root section. " +
                $"Expected only the safe methods GET, HEAD, OPTIONS and TRACE here, and any other method " +
                $"under {section.Path}:{HttpResilienceConfigurationKeys.ClientsSection}:{{name}} for the one " +
                "client whose endpoint deduplicates on an idempotency key. Reason: every client inherits the " +
                "root, including clients registered later that state nothing, so this one entry decides that " +
                $"'{method}' may be delivered to an origin more than once across the whole process. A root " +
                "list may narrow what is retried -- that is what one shared allow-list is for -- but " +
                "widening it is a property of one endpoint's idempotency handling, not of a fleet.");
        }
    }

    /// <summary>
    /// Creates every client this package configured, once, while the host is starting, so a failure that is
    /// only reachable through handler construction fails the deployment rather than a later request.
    /// </summary>
    /// <param name="services">The service collection. <see cref="AddHttpResilience(IServiceCollection, IConfiguration)"/> need not have been called yet.</param>
    /// <returns>The same service collection, for chaining.</returns>
    /// <remarks>
    /// Startup validation covers everything expressible as an options value. It cannot cover which primary
    /// handler a client ends up with: that is decided by the service collection and does not materialize
    /// until <see cref="IHttpClientFactory"/> builds the chain. So <c>Connection:Enabled</c> on a client
    /// whose primary handler is not a <see cref="SocketsHttpHandler"/> throws at
    /// <see cref="IHttpClientFactory.CreateClient"/> -- which, for a client on a rare code path, is hours
    /// after the deployment that introduced it.
    /// <para>
    /// <b>This is on by default and calling it is redundant.</b> <c>AddHttpResilience</c> registers the same
    /// probe itself, so a service that calls neither still gets the behavior. It was opt-in until 2.0, on
    /// the reasoning that eagerly building every handler chain has a cost and is wrong for a process that
    /// registers clients it may never use -- which is weak for a client that has explicitly opted into a
    /// resilience pipeline, since registering one is the statement of intent to use it, and it left the
    /// whole difference between a failed deployment and a client returning 500s hours later resting on a
    /// checklist item every adopting service had to remember separately.
    /// </para>
    /// <para>
    /// The method is kept, and stays idempotent, because removing it would break every consumer that
    /// followed that checklist. Only the clients this package registered are created.
    /// </para>
    /// <para>
    /// <b>To opt out, set <c>HttpResilience:ValidateClientsOnStart</c> to <c>false</c></b> -- root-only, read
    /// from the raw section, so it is reachable during an incident without a redeploy. Setting it to
    /// <see langword="false"/> while <i>also</i> calling this method fails startup naming both: the call
    /// would win, and an operator who reached for a configuration key that silently does nothing is worse
    /// off than one who had no key at all.
    /// </para>
    /// </remarks>
    /// <example>
    /// Both of these get the probe; the second line is redundant and harmless.
    /// <code language="csharp">
    /// builder.Services.AddHttpResilience(builder.Configuration);
    /// builder.Services.ValidateHttpResilienceClientsOnStart();
    /// </code>
    /// Opting out:
    /// <code language="json">
    /// { "HttpResilience": { "ValidateClientsOnStart": false } }
    /// </code>
    /// </example>
    public static IServiceCollection ValidateHttpResilienceClientsOnStart(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // TryAddEnumerable so a shared platform extension and the application can both ask for it without
        // creating every client twice. Redundant since the probe became the default, and kept because
        // removing it would break every consumer that followed the production checklist.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ClientStartupProbe>());

        // Recorded so that a HttpResilience:ValidateClientsOnStart of false, which this call would silently
        // override, fails at startup naming both instead. See ClientStartupProbe.
        services.TryAddSingleton(new ExplicitClientProbeRequest());

        return services;
    }
}
