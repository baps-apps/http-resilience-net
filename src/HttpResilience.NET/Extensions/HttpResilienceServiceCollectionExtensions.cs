using HttpResilience.NET;
using HttpResilience.NET.Internal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers HTTP resilience configuration for the application.
/// </summary>
public static class HttpResilienceServiceCollectionExtensions
{
    /// <summary>
    /// Registers HTTP resilience configuration from the <c>HttpResilience</c> section, validated at startup.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Application configuration containing an <c>HttpResilience</c> section.</param>
    /// <returns>The service collection, for chaining.</returns>
    /// <remarks>
    /// Each client then opts in with
    /// <see cref="HttpResilienceHttpClientBuilderExtensions.AddHttpResilienceHandler(IHttpClientBuilder)"/>, reading
    /// <c>HttpResilience:Clients:{name}</c> on top of the root values. Order does not matter: configuration
    /// is read when each client is first created, so sources added after this call are honoured. A client
    /// whose application never calls this fails on creation with a message saying so.
    /// <para>
    /// Every configured client is created once at host start, so a broken handler chain fails the deployment
    /// rather than a later request. Set <c>HttpResilience:ValidateClientsOnStart</c> to <c>false</c>, or
    /// <see cref="HttpResilienceStartupOptions.ValidateClientsOnStart"/> in code, to opt out.
    /// </para>
    /// <para>
    /// A key the schema does not have fails startup, unless <c>HttpResilience:SchemaVersion</c> says the file was
    /// written for a newer minor version (then it is a warning); see
    /// <see cref="HttpResilienceStartupOptions.AllowUnknownKeys"/>.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code language="csharp">
    /// builder.Services.AddHttpResilience(builder.Configuration);
    ///
    /// builder.Services.AddHttpClient&lt;IOrdersApi, OrdersApi&gt;().AddHttpResilienceHandler();
    /// builder.Services.AddHttpClient("Search").AddHttpResilienceHedgingHandler();
    /// </code>
    /// </example>
    public static IServiceCollection AddHttpResilience(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        return services.AddHttpResilience(configuration.GetSection(HttpResilienceConfigurationKeys.RootSection));
    }

    /// <summary>
    /// Registers HTTP resilience configuration from an explicit section, validated at startup.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="section">The root resilience section. Per-client overrides are read from its <c>Clients</c> child.</param>
    /// <returns>The service collection, for chaining.</returns>
    /// <remarks>
    /// Idempotent for the same section, so a shared library and the application may both call it. A call
    /// with a different section throws; one with the same path from another configuration object whose values
    /// differ fails when client options are built.
    /// </remarks>
    /// <example>
    /// <code language="csharp">
    /// builder.Services.AddHttpResilience(builder.Configuration.GetSection("Platform:Http"));
    /// </code>
    /// </example>
    /// <exception cref="InvalidOperationException">A previous call registered a different section.</exception>
    public static IServiceCollection AddHttpResilience(this IServiceCollection services, IConfigurationSection section)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(section);

        if (HttpResilienceRegistration.Find(services) is { Section: { } existing } registered)
        {
            if (!string.Equals(existing.Path, section.Path, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"AddHttpResilience was already called with configuration section " +
                    $"'{existing.Path}' and is now called with '{section.Path}'. Clients would read " +
                    "different sections depending on registration order. Call AddHttpResilience with one section.");
            }

            // Same path, maybe another configuration object: compared when options are built, since sources
            // may still be added.
            if (!ReferenceEquals(existing, section))
            {
                registered.AddRepeatedSection(section);
            }

            return services;
        }

        // Fails early when the section already says so. Each client re-checks when its options are built,
        // which also covers configuration sources added after this call.
        if (RootSectionRules.CollectUnsafeMethodGuardFailures(section) is { } failures)
        {
            throw new OptionsValidationException(
                Microsoft.Extensions.Options.Options.DefaultName, typeof(HttpResilienceOptions), failures);
        }

        AddCoreServices(services).Section = section;
        return services;
    }

    /// <summary>
    /// Registers HTTP resilience with root values set in code, validated at startup.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">
    /// Sets the root values every client starts from. Applied after the <c>HttpResilience</c> section, if one is
    /// also registered, and before each client's own section.
    /// </param>
    /// <returns>The service collection, for chaining.</returns>
    /// <remarks>
    /// May be combined with <see cref="AddHttpResilience(IServiceCollection, IConfiguration)"/> in either order;
    /// each call's delegate is applied in registration order. Values are validated exactly like configuration.
    /// <para>
    /// The delegate applies to every client, so it is held to the root section's rules: it cannot set
    /// <c>Retry.DisableForUnsafeHttpMethods</c> or <c>Hedging.DisableForUnsafeHttpMethods</c> to
    /// <see langword="false"/>, or add a method other than GET, HEAD, OPTIONS or TRACE to
    /// <c>Retry.RetryableMethods</c>. Do that for one client, in its section or its own
    /// <c>AddHttpResilienceHandler(configure)</c>. The root <c>Enabled: false</c> kill switch is read from
    /// configuration only.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code language="csharp">
    /// builder.Services.AddHttpResilience(options =>
    /// {
    ///     options.Timeout.Total = TimeSpan.FromSeconds(10);
    ///     options.Retry.MaxRetries = 3;
    /// });
    ///
    /// builder.Services.AddHttpClient("Orders").AddHttpResilienceHandler();
    /// </code>
    /// </example>
    public static IServiceCollection AddHttpResilience(
        this IServiceCollection services,
        Action<HttpResilienceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        AddCoreServices(services).AddRootConfigure(configure);
        return services;
    }

    /// <summary>
    /// Registers HTTP resilience from the <c>HttpResilience</c> section, then applies root values set in code.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Application configuration containing an <c>HttpResilience</c> section.</param>
    /// <param name="configure">
    /// Sets root values after the section is bound and before each client's own section.
    /// </param>
    /// <returns>The service collection, for chaining.</returns>
    /// <remarks>
    /// Equivalent to calling <see cref="AddHttpResilience(IServiceCollection, IConfiguration)"/> and then
    /// <see cref="AddHttpResilience(IServiceCollection, Action{HttpResilienceOptions})"/>.
    /// </remarks>
    public static IServiceCollection AddHttpResilience(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<HttpResilienceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        return services.AddHttpResilience(configuration).AddHttpResilience(configure);
    }

    /// <summary>
    /// Registers HTTP resilience from an explicit section, then applies root values set in code.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="section">The root resilience section. Per-client overrides are read from its <c>Clients</c> child.</param>
    /// <param name="configure">
    /// Sets root values after the section is bound and before each client's own section.
    /// </param>
    /// <returns>The service collection, for chaining.</returns>
    /// <remarks>
    /// Equivalent to calling <see cref="AddHttpResilience(IServiceCollection, IConfigurationSection)"/> and then
    /// <see cref="AddHttpResilience(IServiceCollection, Action{HttpResilienceOptions})"/>.
    /// </remarks>
    /// <example>
    /// <code language="csharp">
    /// builder.Services.AddHttpResilience(
    ///     builder.Configuration.GetSection("Platform:Http"),
    ///     options => options.Retry.MaxRetries = 3);
    /// </code>
    /// </example>
    /// <exception cref="InvalidOperationException">A previous call registered a different section.</exception>
    public static IServiceCollection AddHttpResilience(
        this IServiceCollection services,
        IConfigurationSection section,
        Action<HttpResilienceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        return services.AddHttpResilience(section).AddHttpResilience(configure);
    }

    /// <summary>
    /// Registers the services every client shares, once, however many times and in whichever form
    /// <c>AddHttpResilience</c> is called. Nothing here captures the section or the delegates: both are read
    /// from the registration when options are built.
    /// </summary>
    private static HttpResilienceRegistration AddCoreServices(IServiceCollection services)
    {
        // Shared with client registrations, which may have been made first.
        HttpResilienceRegistration registration = HttpResilienceRegistration.GetOrAdd(services);
        if (registration.CoreServicesRegistered)
        {
            return registration;
        }

        registration.CoreServicesRegistered = true;

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<HttpResilienceOptions>, HttpResilienceOptionsValidator>());

        services.TryAddSingleton<CircuitBreakerStateTracker>();

        services.AddMetrics();
        services.TryAddSingleton<HttpResilienceMetrics>();

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHttpMessageHandlerBuilderFilter, ResilienceHandlerCountFilter>());

        services.AddHttpClient();

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ClientStartupReport>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ClientStartupProbe>());
        services.TryAddSingleton<PlatformSelfCheck>();

        services.AddSingleton<IValidateOptions<HttpResilienceOptions>>(serviceProvider =>
            new UnusedClientSectionValidator(
                registration, serviceProvider.GetRequiredService<IOptions<HttpResilienceStartupOptions>>()));

        // Configuration is the starting value of each instance, so every Configure<HttpResilienceStartupOptions>
        // a consumer registered, before or after this call, is applied on top of it: code wins over configuration.
        // A closed registration, which the container prefers to the open-generic default factory.
        services.AddTransient<IOptionsFactory<HttpResilienceStartupOptions>, StartupSwitches.Factory>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<HttpResilienceStartupOptions>, StartupSwitches.Validator>());
        services.AddOptions<HttpResilienceStartupOptions>().ValidateOnStart();

        // Configure rather than Bind: no reload token, because changes need a restart to take effect.
        services.AddOptions<HttpResilienceOptions>()
            .Configure(options =>
            {
                // Not Section?.Bind: the binding source generator intercepts only a plain call, and anything else
                // falls back to the reflection binder that trimming and AOT break.
                if (registration.Section is { } section)
                {
                    section.Bind(options);
                }

                foreach (Action<HttpResilienceOptions> configure in registration.RootConfigures)
                {
                    configure(options);
                }
            })
            .ValidateOnStart();

        return registration;
    }
}
