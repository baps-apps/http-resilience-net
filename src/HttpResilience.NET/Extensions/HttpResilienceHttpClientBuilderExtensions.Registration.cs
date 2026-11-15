using System.Threading.RateLimiting;
using HttpResilience.NET;
using HttpResilience.NET.Internal;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

public static partial class HttpResilienceHttpClientBuilderExtensions
{
    /// <summary>
    /// Registers the client's options, validation and connection handling. Reads no configuration:
    /// every value is bound when the client is first created.
    /// </summary>
    private static string Register(
        IHttpClientBuilder builder,
        string? sectionName,
        Action<HttpResilienceOptions>? configure,
        PipelineKind kind)
    {
        HttpResilienceRegistration registration = HttpResilienceRegistration.GetOrAdd(builder.Services);
        string optionsName = builder.Name;

        sectionName ??= optionsName;
        string scope = HttpResilienceOptionsValidator.ScopeFor(sectionName);

        GuardAgainstDoubleRegistration(registration, new RegisteredClient(optionsName, sectionName, scope, kind));
        registration.MarkSectionConsumed(sectionName);

        builder.Services.AddOptions<HttpResilienceOptions>(optionsName)
            .Configure(target => BindEffective(
                registration.RequireSection(optionsName), registration.RootConfigures, sectionName, configure, kind,
                target))
            .ValidateOnStart();

        builder.Services.AddSingleton<IValidateOptions<HttpResilienceOptions>>(serviceProvider =>
            new NamedPipelineOptionsValidator(
                optionsName, sectionName, scope, kind, registration,
                serviceProvider.GetRequiredService<IOptions<HttpResilienceStartupOptions>>()));

        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHttpMessageHandlerBuilderFilter, ConnectionHandlerFilter>());

        // The platform options this package writes are shared with every registration on the client.
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<HttpStandardResilienceOptions>, PlatformOptionsGuard>());
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<HttpStandardHedgingResilienceOptions>, PlatformOptionsGuard>());

        ApplyClientSettings(builder);

        // Created on first use, so only when the rate limiter is enabled. Keyed on a package-owned type so a
        // consumer's keyed RateLimiter cannot replace it; the container owns and disposes it.
        builder.Services.AddKeyedSingleton<RateLimiter>(
            new RateLimiterKey(optionsName),
            (serviceProvider, key) =>
            {
                RateLimiter limiter = RateLimiterFactory.Create(Live(serviceProvider, optionsName).RateLimiter);
                serviceProvider.GetService<HttpResilienceMetrics>()
                    ?.Track(((RateLimiterKey)key!).ClientName, LimiterKind.Rate, limiter);
                return limiter;
            });

        // The backstop in the platform's limiter slot, one instance per pipeline. Created on first use, from
        // live options, so only when no rate limiter has taken that slot.
        builder.Services.AddKeyedSingleton(
            new RateLimiterKey(optionsName, LimiterKind.Backstop),
            (serviceProvider, _) =>
            {
                HttpResilienceOptions live = Live(serviceProvider, optionsName);
                return new BackstopLimiters(
                    optionsName,
                    live.ConcurrencyLimiter.Backstop,
                    perAuthority: kind is PipelineKind.Hedging ||
                        live.PipelineSelection.Mode is HttpResiliencePipelineSelectionMode.ByAuthority,
                    serviceProvider.GetService<HttpResilienceMetrics>());
            });

        return scope;
    }

    /// <summary>
    /// Applies <c>Timeout:Client</c> to <see cref="HttpClient.Timeout"/> and, with <c>Connection:Enabled</c>,
    /// turns off handler rotation, both from live options.
    /// </summary>
    /// <remarks>
    /// A post-configure, so it runs after every <c>ConfigureHttpClient</c> and <c>SetHandlerLifetime</c>
    /// registration rather than racing them.
    /// </remarks>
    private static void ApplyClientSettings(IHttpClientBuilder builder)
    {
        string optionsName = builder.Name;

        builder.Services.AddTransient<IPostConfigureOptions<HttpClientFactoryOptions>>(serviceProvider =>
            new PostConfigureOptions<HttpClientFactoryOptions>(
                optionsName,
                factoryOptions =>
                {
                    // Read once, when the factory options are built: configuration is restart-only, so
                    // CreateClient, which a typed client may call per request, does no options lookup.
                    HttpResilienceOptions live = Live(serviceProvider, optionsName);

                    if (live.Connection.Enabled)
                    {
                        // PooledConnectionLifetime bounds connection age instead of handler rotation. A lifetime
                        // the consumer stated conflicts, so it fails like a conflicting HttpClient.Timeout.
                        TimeSpan stated = factoryOptions.HandlerLifetime;
                        if (stated != DefaultHandlerLifetime && stated != Timeout.InfiniteTimeSpan)
                        {
                            throw new InvalidOperationException(
                                $"HTTP client '{optionsName}' sets its handler lifetime to {stated} " +
                                "(SetHandlerLifetime), but Connection:Enabled turns handler rotation off: " +
                                "Connection:PooledConnectionLifetime bounds connection age instead. Remove the " +
                                "SetHandlerLifetime call and set Connection:PooledConnectionLifetime, or set " +
                                "Connection:Enabled to false for this client to keep handler rotation.");
                        }

                        factoryOptions.HandlerLifetime = Timeout.InfiniteTimeSpan;
                    }

                    if (!live.Enabled)
                    {
                        return;
                    }

                    TimeSpan configured = live.Timeout.EffectiveClientTimeout();

                    // Reset first, so any finite value seen at the end was assigned by the consumer.
                    factoryOptions.HttpClientActions.Insert(0, static client => client.Timeout = Timeout.InfiniteTimeSpan);
                    factoryOptions.HttpClientActions.Add(client => ApplyClientTimeout(optionsName, configured, client));
                }));
    }

    private static void ApplyClientTimeout(string optionsName, TimeSpan configured, HttpClient client)
    {
        TimeSpan current = client.Timeout;

        if (current != Timeout.InfiniteTimeSpan && current != configured)
        {
            throw new InvalidOperationException(
                $"HTTP client '{optionsName}' sets HttpClient.Timeout to {current} in code, but HttpResilience " +
                $"resolves it to {configured}. Set Timeout:Client in configuration instead (it must be greater " +
                "than Timeout:Total), or remove the ConfigureHttpClient assignment. A typed client that sets " +
                "the timeout in its constructor bypasses this check, so check the constructor too.");
        }

        client.Timeout = configured;
    }

    /// <summary>
    /// The client's current options. Read when handlers and pipelines are built, so configuration sources and
    /// a consumer's <c>Configure</c> or <c>PostConfigure</c> take effect whenever they were registered.
    /// </summary>
    private static HttpResilienceOptions Live(IServiceProvider serviceProvider, string optionsName) =>
        serviceProvider.GetRequiredService<IOptionsMonitor<HttpResilienceOptions>>().Get(optionsName);

    /// <summary>
    /// One pipeline per listed authority for a hedged client (the platform's own per-authority behavior,
    /// bounded by the allow-list) or a standard client under <see cref="HttpResiliencePipelineSelectionMode.ByAuthority"/>;
    /// otherwise one pipeline with an empty instance key, which is how the platform names it without a
    /// selector, so telemetry is unchanged.
    /// </summary>
    private static Func<IServiceProvider, Func<HttpRequestMessage, string>> AuthoritySelector(
        string optionsName,
        PipelineKind kind) =>
        serviceProvider =>
        {
            HttpResiliencePipelineSelectionOptions selection = Live(serviceProvider, optionsName).PipelineSelection;
            return kind is PipelineKind.Hedging || selection.Mode is HttpResiliencePipelineSelectionMode.ByAuthority
                ? PipelineKeySelector.Create(selection)
                : static _ => string.Empty;
        };

    private static void GuardAgainstDoubleRegistration(
        HttpResilienceRegistration registration,
        RegisteredClient client)
    {
        if (!registration.TryAddClient(client))
        {
            throw new InvalidOperationException(
                $"Resilience is already configured for HTTP client '{client.Name}'. A second pipeline nests " +
                "inside the first, multiplying retries and applying the total timeout twice. Remove the " +
                "duplicate AddHttpResilienceHandler or AddHttpResilienceHedgingHandler call; add extra strategies with " +
                "AddResilienceHandler.");
        }
    }
}
