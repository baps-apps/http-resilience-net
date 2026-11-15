using HttpResilience.NET;
using HttpResilience.NET.Internal;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;


namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Adds a resilience pipeline to a named or typed <see cref="HttpClient"/>.
/// </summary>
public static partial class HttpResilienceHttpClientBuilderExtensions
{
    private const string ConcurrencyLimiterHandlerName = "http-resilience-concurrency";
    private const string RateLimiterHandlerName = "http-resilience-rate-limiter";
    private const string ConcurrencyBackstopHandlerName = "http-resilience-concurrency-backstop";

    /// <summary>The factory's own handler lifetime, which a consumer who never called SetHandlerLifetime has.</summary>
    private static readonly TimeSpan DefaultHandlerLifetime = new HttpClientFactoryOptions().HandlerLifetime;

    /// <summary>
    /// Adds the standard resilience pipeline to this client: timeouts, retries and a circuit breaker, plus
    /// optional rate and concurrency limits. Reads <c>HttpResilience:Clients:{client name}</c>.
    /// </summary>
    /// <param name="builder">The client builder.</param>
    /// <returns>The client builder, for chaining.</returns>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description>Also call <c>services.AddHttpResilience(configuration)</c> or <c>services.AddHttpResilience(options => ...)</c>, before or after this.</description></item>
    /// <item><description>Configuration is read when the client is first created, so sources added later are honoured. Invalid configuration fails at host start, or at first creation without a host.</description></item>
    /// <item><description>Call once per client; a second call throws, because nested pipelines multiply retries.</description></item>
    /// <item><description>Call it on each client, not inside <c>ConfigureHttpClientDefaults</c>, which throws.</description></item>
    /// <item><description>Replaces a resilience handler added through <c>ConfigureHttpClientDefaults</c>, such as the standard handler .NET Aspire's ServiceDefaults add, so pipelines never nest (event 17). A resilience handler added to this client before this call fails client creation instead. A disabled client keeps its own handlers but loses the defaults' resilience handlers, which would retry unsafe methods.</description></item>
    /// <item><description>Code that conflicts with the resolved settings fails client creation, at host start: a <c>ConfigureHttpClient</c> timeout, or a finite <c>SetHandlerLifetime</c> with <c>Connection:Enabled</c>.</description></item>
    /// <item><description>Only GET, HEAD, OPTIONS and TRACE are retried unless <see cref="HttpResilienceRetryOptions.RetryableMethods"/> names others.</description></item>
    /// <item><description>For a strategy this schema does not cover, add <c>AddResilienceHandler</c> after this call.</description></item>
    /// </list>
    /// </remarks>
    /// <example>
    /// <code language="csharp">
    /// builder.Services.AddHttpResilience(builder.Configuration);
    ///
    /// builder.Services.AddHttpClient&lt;IOrdersApi, OrdersApi&gt;().AddHttpResilienceHandler();
    /// </code>
    /// </example>
    /// <exception cref="InvalidOperationException">
    /// Resilience is already configured for this client, or the builder is the
    /// <c>ConfigureHttpClientDefaults</c> builder.
    /// </exception>
    public static IHttpClientBuilder AddHttpResilienceHandler(this IHttpClientBuilder builder) =>
        AddStandard(builder, sectionName: null, configure: null);

    /// <summary>
    /// Adds the standard resilience pipeline to this client, reading the named section.
    /// </summary>
    /// <param name="builder">The client builder.</param>
    /// <param name="sectionName">
    /// The section under <c>HttpResilience:Clients</c> to read, when it is not named after the client. A
    /// client without a section of its own reads the root values; that needs no section name.
    /// </param>
    /// <returns>The client builder, for chaining.</returns>
    /// <remarks>See <see cref="AddHttpResilienceHandler(IHttpClientBuilder)"/>.</remarks>
    /// <example>
    /// <code language="csharp">
    /// builder.Services.AddHttpClient&lt;IOrdersApi, OrdersApi&gt;().AddHttpResilienceHandler("Orders");
    /// </code>
    /// </example>
    /// <exception cref="ArgumentException"><paramref name="sectionName"/> is empty or blank.</exception>
    /// <exception cref="InvalidOperationException">
    /// Resilience is already configured for this client, or the builder is the
    /// <c>ConfigureHttpClientDefaults</c> builder.
    /// </exception>
    public static IHttpClientBuilder AddHttpResilienceHandler(this IHttpClientBuilder builder, string sectionName)
    {
        RequireSectionName(sectionName);
        return AddStandard(builder, sectionName, configure: null);
    }

    /// <summary>
    /// Adds the standard resilience pipeline to this client, then applies <paramref name="configure"/>.
    /// </summary>
    /// <param name="builder">The client builder.</param>
    /// <param name="configure">Adjustments applied after configuration is bound, then validated.</param>
    /// <returns>The client builder, for chaining.</returns>
    /// <remarks>See <see cref="AddHttpResilienceHandler(IHttpClientBuilder)"/>.</remarks>
    /// <example>
    /// <code language="csharp">
    /// builder.Services.AddHttpClient("Reports")
    ///     .AddHttpResilienceHandler(options => options.Timeout.Total = TimeSpan.FromMinutes(2));
    /// </code>
    /// </example>
    /// <exception cref="InvalidOperationException">
    /// Resilience is already configured for this client, or the builder is the
    /// <c>ConfigureHttpClientDefaults</c> builder.
    /// </exception>
    public static IHttpClientBuilder AddHttpResilienceHandler(
        this IHttpClientBuilder builder,
        Action<HttpResilienceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return AddStandard(builder, sectionName: null, configure);
    }

    /// <summary>
    /// Adds the standard resilience pipeline to this client, reading the named section, then applies
    /// <paramref name="configure"/>.
    /// </summary>
    /// <param name="builder">The client builder.</param>
    /// <param name="sectionName">
    /// The section under <c>HttpResilience:Clients</c> to read. To read the section named after the client,
    /// use the overload without it.
    /// </param>
    /// <param name="configure">Adjustments applied after configuration is bound, then validated.</param>
    /// <returns>The client builder, for chaining.</returns>
    /// <remarks>See <see cref="AddHttpResilienceHandler(IHttpClientBuilder)"/>.</remarks>
    /// <exception cref="ArgumentException"><paramref name="sectionName"/> is empty or blank.</exception>
    /// <exception cref="InvalidOperationException">
    /// Resilience is already configured for this client, or the builder is the
    /// <c>ConfigureHttpClientDefaults</c> builder.
    /// </exception>
    public static IHttpClientBuilder AddHttpResilienceHandler(
        this IHttpClientBuilder builder,
        string sectionName,
        Action<HttpResilienceOptions> configure)
    {
        RequireSectionName(sectionName);
        ArgumentNullException.ThrowIfNull(configure);
        return AddStandard(builder, sectionName, configure);
    }

    /// <summary>
    /// Adds a hedging pipeline instead of the standard one: a slow request is raced against another copy and
    /// the first response wins. Reads <c>HttpResilience:Clients:{client name}</c>.
    /// </summary>
    /// <param name="builder">The client builder.</param>
    /// <returns>The client builder, for chaining.</returns>
    /// <remarks>
    /// Use instead of <see cref="AddHttpResilienceHandler(IHttpClientBuilder)"/>, not with it. Compared with
    /// the standard pipeline:
    /// <list type="bullet">
    /// <item><description><c>PipelineSelection:Authorities</c> is required; requests to other authorities fail with <see cref="InvalidOperationException"/>, which no retry policy treats as transient.</description></item>
    /// <item><description>There is no retry strategy; <c>Retry</c> keys on the client fail startup.</description></item>
    /// <item><description>The circuit breaker and concurrency backstop are per authority.</description></item>
    /// <item><description>Redirects are not followed by default.</description></item>
    /// <item><description>Only GET, HEAD, OPTIONS and TRACE are hedged by default; see <see cref="HttpResilienceHedgingOptions.DisableForUnsafeHttpMethods"/>.</description></item>
    /// </list>
    /// </remarks>
    /// <example>
    /// <code language="csharp">
    /// builder.Services.AddHttpClient("Search").AddHttpResilienceHedgingHandler();
    /// </code>
    /// <code language="json">
    /// {
    ///   "HttpResilience": {
    ///     "Clients": {
    ///       "Search": {
    ///         "Timeout": { "Total": "00:00:06", "Attempt": "00:00:02" },
    ///         "Hedging": { "Delay": "00:00:00.300", "MaxHedgedAttempts": 1 },
    ///         "PipelineSelection": { "Authorities": [ "https://search.internal" ] }
    ///       }
    ///     }
    ///   }
    /// }
    /// </code>
    /// </example>
    /// <exception cref="InvalidOperationException">
    /// Resilience is already configured for this client, or the builder is the
    /// <c>ConfigureHttpClientDefaults</c> builder.
    /// </exception>
    public static IHttpClientBuilder AddHttpResilienceHedgingHandler(this IHttpClientBuilder builder) =>
        AddHedging(builder, sectionName: null, configure: null);

    /// <summary>
    /// Adds a hedging pipeline instead of the standard one, reading the named section.
    /// </summary>
    /// <param name="builder">The client builder.</param>
    /// <param name="sectionName">
    /// The section under <c>HttpResilience:Clients</c> to read, when it is not named after the client. A
    /// client without a section of its own reads the root values; that needs no section name.
    /// </param>
    /// <returns>The client builder, for chaining.</returns>
    /// <remarks>See <see cref="AddHttpResilienceHedgingHandler(IHttpClientBuilder)"/>.</remarks>
    /// <exception cref="ArgumentException"><paramref name="sectionName"/> is empty or blank.</exception>
    /// <exception cref="InvalidOperationException">
    /// Resilience is already configured for this client, or the builder is the
    /// <c>ConfigureHttpClientDefaults</c> builder.
    /// </exception>
    public static IHttpClientBuilder AddHttpResilienceHedgingHandler(
        this IHttpClientBuilder builder,
        string sectionName)
    {
        RequireSectionName(sectionName);
        return AddHedging(builder, sectionName, configure: null);
    }

    /// <summary>
    /// Adds a hedging pipeline instead of the standard one, then applies <paramref name="configure"/>.
    /// </summary>
    /// <param name="builder">The client builder.</param>
    /// <param name="configure">Adjustments applied after configuration is bound, then validated.</param>
    /// <returns>The client builder, for chaining.</returns>
    /// <remarks>See <see cref="AddHttpResilienceHedgingHandler(IHttpClientBuilder)"/>.</remarks>
    /// <exception cref="InvalidOperationException">
    /// Resilience is already configured for this client, or the builder is the
    /// <c>ConfigureHttpClientDefaults</c> builder.
    /// </exception>
    public static IHttpClientBuilder AddHttpResilienceHedgingHandler(
        this IHttpClientBuilder builder,
        Action<HttpResilienceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return AddHedging(builder, sectionName: null, configure);
    }

    /// <summary>
    /// Adds a hedging pipeline instead of the standard one, reading the named section, then applies
    /// <paramref name="configure"/>.
    /// </summary>
    /// <param name="builder">The client builder.</param>
    /// <param name="sectionName">
    /// The section under <c>HttpResilience:Clients</c> to read. To read the section named after the client,
    /// use the overload without it.
    /// </param>
    /// <param name="configure">Adjustments applied after configuration is bound, then validated.</param>
    /// <returns>The client builder, for chaining.</returns>
    /// <remarks>See <see cref="AddHttpResilienceHedgingHandler(IHttpClientBuilder)"/>.</remarks>
    /// <exception cref="ArgumentException"><paramref name="sectionName"/> is empty or blank.</exception>
    /// <exception cref="InvalidOperationException">
    /// Resilience is already configured for this client, or the builder is the
    /// <c>ConfigureHttpClientDefaults</c> builder.
    /// </exception>
    public static IHttpClientBuilder AddHttpResilienceHedgingHandler(
        this IHttpClientBuilder builder,
        string sectionName,
        Action<HttpResilienceOptions> configure)
    {
        RequireSectionName(sectionName);
        ArgumentNullException.ThrowIfNull(configure);
        return AddHedging(builder, sectionName, configure);
    }

    private static IHttpClientBuilder AddStandard(
        IHttpClientBuilder builder,
        string? sectionName,
        Action<HttpResilienceOptions>? configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        RejectDefaultsBuilder(builder);

        string name = builder.Name;
        string scope = Register(builder, sectionName, configure, PipelineKind.Standard);
        ReplaceEarlierResilienceHandlers(builder);

        // Outermost first. Every group is registered; live options decide which ones stay.
        AddHandlerGroup(builder, HasConcurrencyLimiter, () => AddConcurrencyLimiter(builder, scope));
        AddHandlerGroup(builder, IsBackstopDisplaced, () => AddConcurrencyBackstop(builder, scope));
        AddHandlerGroup(builder, IsEnabled, () => builder
            .AddStandardResilienceHandler()
            .Configure(StandardPipelineConfigurator.Create(name, name, scope))
            .SelectPipelineBy(AuthoritySelector(name, PipelineKind.Standard)));

        TakeTheReplacedHandlersPlace(builder);
        return builder;
    }

    private static IHttpClientBuilder AddHedging(
        IHttpClientBuilder builder,
        string? sectionName,
        Action<HttpResilienceOptions>? configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        RejectDefaultsBuilder(builder);

        string name = builder.Name;
        string scope = Register(builder, sectionName, configure, PipelineKind.Hedging);
        ReplaceEarlierResilienceHandlers(builder);

        AddHandlerGroup(builder, IsEnabled, () => AddAuthorityAllowList(builder));
        AddHandlerGroup(builder, HasConcurrencyLimiter, () => AddConcurrencyLimiter(builder, scope));
        AddHandlerGroup(builder, HasRateLimiter, () => AddHedgedRateLimiter(builder, scope));
        AddHandlerGroup(builder, IsEnabled, () => builder
            .AddStandardHedgingHandler()
            .Configure(HedgingPipelineConfigurator.Create(name, name, scope))
            .SelectPipelineBy(AuthoritySelector(name, PipelineKind.Hedging)));

        TakeTheReplacedHandlersPlace(builder);
        HedgingPipelineConfigurator.SuppressUnsafeHedgedAttempts(builder.Services, name);

        return builder;
    }

    /// <summary>
    /// A section name is a name. A blank one used to mean "root values only", which is what a client without a
    /// section of its own already reads, so it is refused rather than given a meaning.
    /// </summary>
    private static void RequireSectionName(string sectionName)
    {
        ArgumentNullException.ThrowIfNull(sectionName);
        if (string.IsNullOrWhiteSpace(sectionName))
        {
            throw new ArgumentException(
                "A section name must not be empty or blank. A client without a section of its own under " +
                "HttpResilience:Clients already reads the root values: call the overload without a section name.",
                nameof(sectionName));
        }
    }

    private static bool IsEnabled(HttpResilienceOptions o) => o.Enabled;

    private static bool HasConcurrencyLimiter(HttpResilienceOptions o) => o.Enabled && o.ConcurrencyLimiter.Enabled;

    private static bool HasRateLimiter(HttpResilienceOptions o) => o.Enabled && o.RateLimiter.Enabled;

    /// <summary>
    /// A standard client's rate limiter takes the platform handler's only limiter slot, so the backstop
    /// needs its own handler, unless the client's concurrency cap (never above the backstop) already bounds it.
    /// </summary>
    private static bool IsBackstopDisplaced(HttpResilienceOptions o) =>
        HasRateLimiter(o) && !o.ConcurrencyLimiter.Enabled;

    /// <summary>
    /// Rejects the unnamed builder passed to <c>ConfigureHttpClientDefaults</c>, which has no section to read.
    /// </summary>
    private static void RejectDefaultsBuilder(IHttpClientBuilder builder)
    {
        if (builder.Name is not null)
        {
            return;
        }

        throw new InvalidOperationException(
            "AddHttpResilienceHandler and AddHttpResilienceHedgingHandler cannot be used inside ConfigureHttpClientDefaults. " +
            "Each client is configured from its own section, HttpResilience:Clients:{name}, and the defaults " +
            "builder has no name. Call it on each AddHttpClient registration instead. A standard handler that " +
            "ConfigureHttpClientDefaults adds (as .NET Aspire ServiceDefaults does) can stay: on each client " +
            "configured by this package it is replaced, so the two pipelines do not nest.");
    }
}
