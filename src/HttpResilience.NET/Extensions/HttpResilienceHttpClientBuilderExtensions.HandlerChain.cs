using HttpResilience.NET;
using HttpResilience.NET.Internal;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

public static partial class HttpResilienceHttpClientBuilderExtensions
{
    /// <summary>
    /// Registers a group of handlers unconditionally and decides, each time the client's handler chain is
    /// built, whether to keep it.
    /// </summary>
    /// <remarks>
    /// Handler-builder actions run in registration order, so the handlers <paramref name="addHandlers"/> adds
    /// land between the two markers registered around it. An inactive group is removed before the chain is
    /// used, so it costs nothing per request. The platform may build a handler's pipeline when the handler is
    /// constructed, so each group's pipeline must also add nothing when inactive.
    /// <para>
    /// The group's own <see cref="HttpClient"/> actions (the platform handler sets
    /// <see cref="HttpClient.Timeout"/> to infinite) are bracketed the same way, and an inactive group's are
    /// removed from the factory options once, when those options are built, so <c>CreateClient</c> does no
    /// per-call work for them.
    /// </para>
    /// </remarks>
    private static void AddHandlerGroup(
        IHttpClientBuilder builder,
        Func<HttpResilienceOptions, bool> isActive,
        Action addHandlers)
    {
        string name = builder.Name;

        // Closures, so each group gets distinct delegate instances that are found by reference. Both are
        // removed before any client is created, so they never run.
        Action<HttpClient> clientStart = _ => GC.KeepAlive(name);
        Action<HttpClient> clientEnd = _ => GC.KeepAlive(name);

        builder.ConfigureAdditionalHttpMessageHandlers(static (handlers, _) => handlers.Add(new GroupStart()));
        builder.ConfigureHttpClient(clientStart);

        addHandlers();

        builder.ConfigureHttpClient(clientEnd);
        builder.Services.AddTransient<IPostConfigureOptions<HttpClientFactoryOptions>>(serviceProvider =>
            new PostConfigureOptions<HttpClientFactoryOptions>(
                name,
                factoryOptions => RemoveClientActions(
                    factoryOptions.HttpClientActions,
                    clientStart,
                    clientEnd,
                    keepGroup: isActive(Live(serviceProvider, name)))));

        builder.ConfigureAdditionalHttpMessageHandlers((handlers, serviceProvider) =>
        {
            int start = LastIndexOfGroupStart(handlers);
            bool active = isActive(Live(serviceProvider, name));
            for (int i = handlers.Count - 1; i > start; i--)
            {
                if (active)
                {
                    // So ResilienceHandlerCountFilter can tell if a later registration removes them.
                    PackageHandlers.Mark(handlers[i]);
                    continue;
                }

                handlers[i].Dispose();
                handlers.RemoveAt(i);
            }

            handlers[start].Dispose();
            handlers.RemoveAt(start);
        });
    }

    /// <summary>
    /// Removes a handler group's two markers and, when the group is inactive, every client action between them.
    /// </summary>
    private static void RemoveClientActions(
        IList<Action<HttpClient>> actions,
        Action<HttpClient> start,
        Action<HttpClient> end,
        bool keepGroup)
    {
        int first = actions.IndexOf(start);
        int last = actions.IndexOf(end);

        // Both gone: a consumer cleared HttpClientActions, which removed the group's actions too.
        if (first < 0 && last < 0)
        {
            return;
        }

        // One gone, or out of order: something edited inside the group, so its extent is unknown.
        if (first < 0 || last < first)
        {
            throw new InvalidOperationException(
                "HttpResilience could not find the client actions it registered for one of its handler groups " +
                "in HttpClientFactoryOptions.HttpClientActions: a Configure or PostConfigure of " +
                "HttpClientFactoryOptions removed or reordered some of them. Add to that list or clear it, but " +
                "do not remove or reorder individual entries.");
        }

        for (int i = last; i >= first; i--)
        {
            if (!keepGroup || i == first || i == last)
            {
                actions.RemoveAt(i);
            }
        }
    }

    private static int LastIndexOfGroupStart(IList<DelegatingHandler> handlers)
    {
        for (int i = handlers.Count - 1; i >= 0; i--)
        {
            if (handlers[i] is GroupStart)
            {
                return i;
            }
        }

        // Each group's markers are adjacent registrations, so only an edit to the list itself separates them.
        throw new InvalidOperationException(
            "HttpResilience could not find where one of its handler groups starts in " +
            "HttpClientFactoryOptions.HttpMessageHandlerBuilderActions: a Configure or PostConfigure of " +
            "HttpClientFactoryOptions removed or reordered some of its entries. Add to that list, but do not " +
            "remove or reorder individual entries.");
    }

    /// <summary>
    /// Removes the resilience handlers registered on the client before this package's, such as the standard
    /// handler .NET Aspire's ServiceDefaults add to every client through <c>ConfigureHttpClientDefaults</c>, so
    /// two pipelines never nest. Handlers registered after this package's are kept: that is where
    /// <c>AddResilienceHandler</c> adds a strategy the schema does not cover.
    /// </summary>
    /// <remarks>
    /// Positional, because a defaults handler and a consumer's own cannot be told apart through public API.
    /// <c>ConfigureHttpClientDefaults</c> actions run before a named client's, whichever is registered first. A
    /// disabled client keeps its own handlers but loses the defaults' resilience handlers, so turning it off
    /// never hands it a pipeline that repeats unsafe methods.
    /// <para>
    /// Marks where the outermost replaced handler stood and where this package's handlers begin, so
    /// <see cref="TakeTheReplacedHandlersPlace"/> can move them into its slot.
    /// </para>
    /// </remarks>
    private static void ReplaceEarlierResilienceHandlers(IHttpClientBuilder builder)
    {
        string name = builder.Name;
        MarkWhereTheClientsOwnActionsBegin(builder);

        builder.ConfigureAdditionalHttpMessageHandlers((handlers, serviceProvider) =>
        {
            int ownActionsStart = IndexOf<ClientActionsStart>(handlers);
            if (ownActionsStart >= 0)
            {
                handlers[ownActionsStart].Dispose();
                handlers.RemoveAt(ownActionsStart);
            }

            if (!Live(serviceProvider, name).Enabled)
            {
                RemoveDefaultsResilienceHandlers(handlers, ownActionsStart, serviceProvider, name);
                return;
            }

            // From the marker to the end: what the consumer registered on this client before this package's call.
            // A resilience handler there is code written for this client, so it is refused, not deleted.
            if (ownActionsStart >= 0)
            {
                for (int i = ownActionsStart; i < handlers.Count; i++)
                {
                    if (handlers[i] is ResilienceHandler)
                    {
                        throw new InvalidOperationException(
                            $"HTTP client '{name}' has a resilience handler (AddResilienceHandler or " +
                            "AddStandardResilienceHandler) registered before AddHttpResilienceHandler or " +
                            "AddHttpResilienceHedgingHandler. Two pipelines would nest and multiply retries, " +
                            "and this package will not delete code written for the client. Add a strategy the " +
                            "schema lacks with AddResilienceHandler after AddHttpResilienceHandler, or remove the " +
                            "platform's standard handler. A handler from ConfigureHttpClientDefaults (.NET " +
                            "Aspire) is replaced automatically.");
                    }
                }
            }

            int replaced = 0;
            int outermost = -1;
            for (int i = handlers.Count - 1; i >= 0; i--)
            {
                if (handlers[i] is ResilienceHandler)
                {
                    handlers[i].Dispose();
                    handlers.RemoveAt(i);
                    replaced++;
                    outermost = i;
                }
            }

            if (replaced > 0)
            {
                handlers.Insert(outermost, new ReplacedSlot());
                handlers.Add(new PackageHandlersStart());
            }

            // Once per client, not once per handler rotation.
            if (replaced > 0 &&
                serviceProvider.GetService<HttpResilienceRegistration>()?.MarkReplacementReported(name) is true &&
                serviceProvider.GetService<ILoggerFactory>()?.CreateLogger(HttpResilienceLogging.ConfigurationCategory)
                    is { } logger)
            {
                HttpResilienceLogging.EarlierResilienceHandlersReplaced(logger, name, replaced);
            }
        });
    }

    /// <summary>
    /// On a disabled client, removes the resilience handlers <c>ConfigureHttpClientDefaults</c> added, keeping the
    /// client's own. Kept, the defaults' standard handler (.NET Aspire's) would retry every method, POST included,
    /// once the kill switch is on. <paramref name="ownActionsStart"/> is where the client's own handlers begin,
    /// or -1 when it registered none before this package's call.
    /// </summary>
    private static void RemoveDefaultsResilienceHandlers(
        IList<DelegatingHandler> handlers,
        int ownActionsStart,
        IServiceProvider serviceProvider,
        string name)
    {
        int removed = 0;
        for (int i = (ownActionsStart >= 0 ? ownActionsStart : handlers.Count) - 1; i >= 0; i--)
        {
            if (handlers[i] is ResilienceHandler)
            {
                handlers[i].Dispose();
                handlers.RemoveAt(i);
                removed++;
            }
        }

        // Once per client, not once per handler rotation.
        if (removed > 0 &&
            serviceProvider.GetService<HttpResilienceRegistration>()?.MarkReplacementReported(name) is true &&
            serviceProvider.GetService<ILoggerFactory>()?.CreateLogger(HttpResilienceLogging.ConfigurationCategory)
                is { } logger)
        {
            HttpResilienceLogging.DefaultsResilienceHandlersRemoved(logger, name, removed);
        }
    }

    /// <summary>
    /// Places a marker handler before the first action the consumer registered for this client ahead of this
    /// package's call, so a resilience handler from that code can be told apart from one added by
    /// <c>ConfigureHttpClientDefaults</c>, whose actions the platform applies from its own registration.
    /// </summary>
    /// <remarks>
    /// Options configure in registration order. A <c>ConfigureNamedOptions</c> instance names its client publicly,
    /// so it counts when it names this one. A registration whose client cannot be read without running it (a
    /// factory, as <c>AddOptions(name).Configure&lt;TDep&gt;</c> registers, or a type) counts as this client's
    /// when it follows the platform's own registration, the first unnamed <c>ConfigureNamedOptions</c>, where the
    /// defaults are applied: placing the marker too early fails client creation, too late would delete the
    /// consumer's handler unseen. Before that registration it cannot count, or the defaults' handlers would be
    /// taken for the client's; with no such registration there are no defaults, and any of them counts. None means
    /// nothing can precede this package's handlers but the defaults.
    /// </remarks>
    private static void MarkWhereTheClientsOwnActionsBegin(IHttpClientBuilder builder)
    {
        string name = builder.Name;
        IServiceCollection services = builder.Services;

        // With no defaults registered there are no defaults' handlers to confuse with the client's.
        bool afterThePlatformsRegistration = !services.Any(static descriptor =>
            descriptor.ServiceType == typeof(IConfigureOptions<HttpClientFactoryOptions>) &&
            !descriptor.IsKeyedService &&
            descriptor.ImplementationInstance is ConfigureNamedOptions<HttpClientFactoryOptions> { Name: null });

        for (int i = 0; i < services.Count; i++)
        {
            ServiceDescriptor descriptor = services[i];
            if (descriptor.ServiceType != typeof(IConfigureOptions<HttpClientFactoryOptions>) ||
                descriptor.IsKeyedService)
            {
                continue;
            }

            bool mayBeThisClients;
            if (descriptor.ImplementationInstance is ConfigureNamedOptions<HttpClientFactoryOptions> configure)
            {
                afterThePlatformsRegistration |= configure.Name is null;
                mayBeThisClients = string.Equals(configure.Name, name, StringComparison.Ordinal);
            }
            else
            {
                mayBeThisClients = afterThePlatformsRegistration;
            }

            if (mayBeThisClients)
            {
                services.Insert(i, ServiceDescriptor.Singleton<IConfigureOptions<HttpClientFactoryOptions>>(
                    new ConfigureNamedOptions<HttpClientFactoryOptions>(name, static options =>
                        options.HttpMessageHandlerBuilderActions.Add(
                            static handlerBuilder => handlerBuilder.AdditionalHandlers.Add(new ClientActionsStart())))));
                return;
            }
        }
    }

    /// <summary>
    /// Moves this package's handlers into the slot of the outermost resilience handler they replaced, so the
    /// handlers that pipeline wrapped stay wrapped.
    /// </summary>
    /// <remarks>
    /// .NET Aspire's ServiceDefaults add service discovery after (inside) the standard handler. Appended
    /// after the defaults, this package's pipeline would sit inside the resolver: every retry would go to the
    /// endpoint resolved once, and the authority allow-list would see a resolved address instead of the name
    /// the caller used. Registered after the last handler group, so the groups have settled; handlers added to
    /// the client later follow, as they would have followed the replaced pipeline's slot.
    /// </remarks>
    private static void TakeTheReplacedHandlersPlace(IHttpClientBuilder builder) =>
        builder.ConfigureAdditionalHttpMessageHandlers(static (handlers, _) =>
        {
            int slot = IndexOf<ReplacedSlot>(handlers);
            int start = IndexOf<PackageHandlersStart>(handlers);

            // Nothing was replaced, or a consumer cleared the list: a no-op, like the handler groups.
            if (slot < 0 && start < 0)
            {
                return;
            }

            if (slot < 0 || start < slot)
            {
                throw new InvalidOperationException(
                    "HttpResilience could not find where the resilience handlers it replaced stood in " +
                    "HttpClientFactoryOptions.HttpMessageHandlerBuilderActions: a Configure or PostConfigure of " +
                    "HttpClientFactoryOptions removed or reordered some of its entries. Add to that list, but do " +
                    "not remove or reorder individual entries.");
            }

            // [before] [slot] [wrapped by the replaced pipeline] [start] [this package's] -> [before] [ours] [wrapped]
            List<DelegatingHandler> wrapped = [.. handlers.Skip(slot + 1).Take(start - slot - 1)];
            List<DelegatingHandler> ours = [.. handlers.Skip(start + 1)];

            handlers[slot].Dispose();
            handlers[start].Dispose();
            for (int i = handlers.Count - 1; i >= slot; i--)
            {
                handlers.RemoveAt(i);
            }

            foreach (DelegatingHandler handler in ours.Concat(wrapped))
            {
                handlers.Add(handler);
            }
        });

    private static int IndexOf<TMarker>(IList<DelegatingHandler> handlers)
        where TMarker : DelegatingHandler
    {
        for (int i = 0; i < handlers.Count; i++)
        {
            if (handlers[i] is TMarker)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>A placeholder that marks where a handler group starts. Never left in a chain.</summary>
    private sealed class GroupStart : DelegatingHandler;

    /// <summary>Where the outermost replaced resilience handler stood. Never left in a chain.</summary>
    private sealed class ReplacedSlot : DelegatingHandler;

    /// <summary>Where this package's handler groups begin, after a replacement. Never left in a chain.</summary>
    private sealed class PackageHandlersStart : DelegatingHandler;

    /// <summary>
    /// Where the consumer's own actions for the client begin, after the defaults'. Never left in a chain.
    /// </summary>
    private sealed class ClientActionsStart : DelegatingHandler;
}
