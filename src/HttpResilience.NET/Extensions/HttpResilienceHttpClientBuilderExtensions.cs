using System.Threading.RateLimiting;
using HttpResilience.NET.Configuration;
using HttpResilience.NET.Internal;
using HttpResilience.NET.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.RateLimiting;

// This file uses both the schema's ConcurrencyLimiterOptions and the BCL type the limiter is built from.
using ConcurrencyLimiterOptions = System.Threading.RateLimiting.ConcurrencyLimiterOptions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Adds a standardized resilience pipeline to a named or typed <see cref="HttpClient"/>.
/// </summary>
public static class HttpResilienceHttpClientBuilderExtensions
{
    private const string ConcurrencyLimiterHandlerName = "http-resilience-concurrency";
    private const string RateLimiterHandlerName = "http-resilience-rate-limiter";
    private const string ConcurrencyBackstopHandlerName = "http-resilience-concurrency-backstop";

    /// <summary>
    /// Adds the standard resilience pipeline to this client: timeouts, retries and a circuit breaker, plus
    /// optional rate and concurrency limits. This is the one to reach for.
    /// </summary>
    /// <param name="builder">The client builder.</param>
    /// <param name="clientName">
    /// The override section under <c>HttpResilience:Clients</c>. Defaults to the client's own name, so
    /// <c>AddHttpClient("Orders").AddHttpResilience()</c> reads <c>HttpResilience:Clients:Orders</c>. Pass a
    /// different name to share another client's section, or <see cref="string.Empty"/> to use only the root
    /// values.
    /// </param>
    /// <param name="configure">
    /// An optional final adjustment applied after configuration is bound. Use it for a value that belongs in
    /// code rather than in a config file; it is validated like any other.
    /// </param>
    /// <returns>The client builder, for chaining.</returns>
    /// <remarks>
    /// Requires <c>services.AddHttpResilience(configuration)</c> to have been called first, and may be called
    /// only once per client -- a second call would nest two pipelines and multiply retries, so it throws.
    /// <para>
    /// The pipeline shape is fixed, because ordering is where resilience pipelines go wrong. Outermost to
    /// innermost: concurrency limiter, the handler's own limiter, total timeout, retry, circuit breaker,
    /// attempt timeout. A concurrency slot and a rate-limit permit each cover one logical request
    /// <i>including</i> its retries, so a retrying request is never rejected by its own budget.
    /// </para>
    /// <para>
    /// Only GET, HEAD, OPTIONS and TRACE are retried unless you name others in
    /// <see cref="HttpResilience.NET.Options.RetryOptions.RetryableMethods"/>.
    /// </para>
    /// <para>
    /// Need a strategy this schema does not express? Add it as a separate handler with
    /// <c>AddResilienceHandler</c> from <c>Microsoft.Extensions.Http.Resilience</c>, which this package
    /// already brings in. There is no custom escape hatch here on purpose -- the platform's is better.
    /// </para>
    /// </remarks>
    /// <example>
    /// A typed client on the defaults, and a named client with one value set in code:
    /// <code language="csharp">
    /// builder.Services.AddHttpResilience(builder.Configuration);
    ///
    /// builder.Services.AddHttpClient&lt;IOrdersApi, OrdersApi&gt;().AddHttpResilience();
    ///
    /// builder.Services.AddHttpClient("Reports")
    ///     .AddHttpResilience(configure: options => options.Timeout.Total = TimeSpan.FromMinutes(2));
    ///
    /// // Something unusual, on top of the standard pipeline:
    /// builder.Services.AddHttpClient("Legacy")
    ///     .AddHttpResilience()
    ///     .AddResilienceHandler("legacy-quirk", pipeline => pipeline.AddRetry(new()));
    /// </code>
    /// </example>
    /// <exception cref="InvalidOperationException">
    /// <c>AddHttpResilience</c> was not called on the service collection first, or resilience is already
    /// configured for this client.
    /// </exception>
    /// <exception cref="Microsoft.Extensions.Options.OptionsValidationException">
    /// This client's configuration is invalid. The message names the configuration path, the value, the
    /// expected range and why the rule exists.
    /// </exception>
    public static IHttpClientBuilder AddHttpResilience(
        this IHttpClientBuilder builder,
        string? clientName = null,
        Action<HttpResilienceOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        (HttpResilienceOptions options, string scope) = Register(builder, clientName, configure, PipelineKind.Standard);
        if (!options.Enabled)
        {
            return builder;
        }

        AddConcurrencyLimiterIfEnabled(builder, options, scope);
        AddConcurrencyBackstopIfDisplaced(builder, options, scope);

        IHttpStandardResiliencePipelineBuilder pipeline = builder
            .AddStandardResilienceHandler()
            .Configure(StandardPipelineConfigurator.Create(builder.Name, builder.Name, scope));

        if (options.PipelineSelection.Mode is PipelineSelectionMode.ByAuthority)
        {
            pipeline.SelectPipelineBy(AuthoritySelector(builder.Name));
        }

        ApplyClientTimeout(builder);

        return builder;
    }

    /// <summary>
    /// Adds a hedging pipeline instead of the standard one: a slow request is raced against a second copy and
    /// the first answer wins. Use it for tail latency on read-only calls.
    /// </summary>
    /// <param name="builder">The client builder.</param>
    /// <param name="clientName">
    /// The override section under <c>HttpResilience:Clients</c>. Defaults to the client's own name. Pass
    /// <see cref="string.Empty"/> to use only the root values.
    /// </param>
    /// <param name="configure">An optional final adjustment applied after configuration is bound.</param>
    /// <returns>The client builder, for chaining.</returns>
    /// <remarks>
    /// This is an <i>alternative</i> to <see cref="AddHttpResilience"/>, not an addition -- a client gets one
    /// or the other. It trades outbound traffic for tail latency, so it suits a read-heavy dependency with
    /// spare capacity and suits nothing that is already struggling.
    /// <para>
    /// Hedging is selected here in code rather than by a configuration value, so the decision is visible in
    /// review on the line that registers the client.
    /// </para>
    /// <para>
    /// <b>Differences from the standard pipeline worth knowing before you switch:</b> there is no retry
    /// strategy, so <c>Retry:*</c> keys on this client fail startup rather than binding silently; the
    /// concurrency backstop and circuit breaker are per authority rather than per client; and redirects are
    /// not followed by default, because this pipeline enforces a destination allow-list and a redirect
    /// resolves below every handler that could check one.
    /// </para>
    /// <para>
    /// A hedged client must list the authorities it may call in <c>PipelineSelection:Authorities</c>. The
    /// hedging handler keeps a circuit breaker, a limiter and a metric series per authority for the life of
    /// the process, so an unbounded set of destinations is a memory-exhaustion path. A request to an unlisted
    /// authority is rejected with <see cref="HttpRequestException"/> before it reaches the wire.
    /// </para>
    /// <para>
    /// Mutating methods are never hedged by default -- see
    /// <see cref="HedgingOptions.DisableForUnsafeHttpMethods"/>. Hedged attempts are simultaneous, so unlike
    /// retries they give an origin's idempotency key no serialization to rely on.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code language="csharp">
    /// builder.Services.AddHttpClient("Search").AddHedgedHttpResilience();
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
    /// <c>AddHttpResilience</c> was not called on the service collection first, or resilience is already
    /// configured for this client.
    /// </exception>
    /// <exception cref="Microsoft.Extensions.Options.OptionsValidationException">
    /// This client's configuration is invalid -- most often a missing <c>PipelineSelection:Authorities</c>,
    /// which a hedged client requires.
    /// </exception>
    public static IHttpClientBuilder AddHedgedHttpResilience(
        this IHttpClientBuilder builder,
        string? clientName = null,
        Action<HttpResilienceOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        (HttpResilienceOptions options, string scope) = Register(builder, clientName, configure, PipelineKind.Hedging);
        if (!options.Enabled)
        {
            return builder;
        }

        AddAuthorityAllowList(builder);
        AddConcurrencyLimiterIfEnabled(builder, options, scope);
        AddRateLimiterIfEnabled(builder, options, scope);

        IStandardHedgingHandlerBuilder pipeline = builder
            .AddStandardHedgingHandler()
            .Configure(HedgingPipelineConfigurator.Create(builder.Name, builder.Name));

        // Registered unconditionally, and after AddStandardHedgingHandler on purpose: that method installs
        // its own ActionGenerator in a PostConfigure, so this has to run later to wrap rather than be
        // overwritten. Whether the guard actually suppresses anything is read from the options when a hedged
        // attempt is considered, so Hedging:DisableForUnsafeHttpMethods stays a value like any other -- and
        // no later change can remove the guard by removing its registration.
        HedgingPipelineConfigurator.SuppressUnsafeHedgedAttempts(builder.Services, builder.Name);

        if (options.PipelineSelection.Mode is PipelineSelectionMode.ByAuthority)
        {
            pipeline.SelectPipelineBy(AuthoritySelector(builder.Name));
        }

        ApplyClientTimeout(builder);

        return builder;
    }

    /// <summary>
    /// Puts a finite bound back on <see cref="HttpClient.Timeout"/>, which the platform's resilience handlers
    /// set to infinite.
    /// </summary>
    /// <remarks>
    /// <c>AddStandardResilienceHandler</c> and <c>AddStandardHedgingHandler</c> set
    /// <see cref="HttpClient.Timeout"/> to <see cref="Timeout.InfiniteTimeSpan"/> themselves, so that their
    /// total request timeout is authoritative and the 100-second default cannot truncate a longer budget.
    /// That reasoning holds for the pipeline and not for the request: every strategy lives in the handler
    /// chain, and the chain returns as soon as the response <i>headers</i> arrive. Under the default
    /// <c>HttpCompletionOption.ResponseContentRead</c> the body is then buffered by <see cref="HttpClient"/>
    /// after the chain, where no strategy can see it -- so with an infinite client timeout an origin that
    /// answers headers promptly and then trickles the body holds a connection, a buffer and an inbound
    /// request for as long as it likes, and the pipeline reports a fast successful attempt.
    /// <para>
    /// This must be registered <b>after</b> the platform handler: <c>ConfigureHttpClient</c> actions run in
    /// registration order, so the last assignment to the same property wins. Registered before it, this is
    /// dead code -- which is what it was.
    /// </para>
    /// </remarks>
    private static void ApplyClientTimeout(IHttpClientBuilder builder)
    {
        string optionsName = builder.Name;

        // A post-configure rather than ConfigureHttpClient, for the reason rule 10 gives about the primary
        // handler: every IConfigureOptions runs before every IPostConfigureOptions, so this action is appended
        // to HttpClientActions after every ConfigureHttpClient registration instead of racing them.
        //
        // ConfigureHttpClient was last-wins, and two ordinary consumer actions beat it. A second
        // AddStandardResilienceHandler put the timeout back to infinite -- removing the response-body bound
        // this exists to restore -- and a plain ConfigureHttpClient(c => c.Timeout = 2s) truncated a
        // 30-second pipeline with a bare TaskCanceledException. Both were silent, and both are the condition
        // ValidateTimeouts already refuses when it is written as Timeout:Client.
        //
        // Transient with a factory, which is how ConfigureHttpClient reaches a service provider from an
        // options callback: the descriptor's factory captures the provider, and the action closes over it.
        builder.Services.AddTransient<IPostConfigureOptions<HttpClientFactoryOptions>>(serviceProvider =>
            new PostConfigureOptions<HttpClientFactoryOptions>(
                optionsName,
                factoryOptions =>
                {
                    // Index 0, so it runs before every ConfigureHttpClient action and before the platform
                    // handler's own assignment -- all of which are IConfigureOptions, and therefore already
                    // in this list by the time a post-configure sees it.
                    //
                    // This is what establishes "nothing assigned one" rather than inferring it. Inferring it
                    // meant treating the framework's 100-second default as the unassigned state, so a
                    // consumer who deliberately wrote 100 seconds was indistinguishable from silence and had
                    // it replaced without a word -- the one value in the whole domain the guard below could
                    // not see. The old note here said no non-colliding sentinel exists for a TimeSpan whose
                    // unset value is a real duration. True, and beside the point: the ambiguity was never in
                    // the value, it was in the moment of reading.
                    factoryOptions.HttpClientActions.Insert(
                        0, static client => client.Timeout = Timeout.InfiniteTimeSpan);

                    factoryOptions.HttpClientActions.Add(
                        client => ApplyClientTimeout(serviceProvider, optionsName, client));
                }));
    }

    private static void ApplyClientTimeout(IServiceProvider serviceProvider, string optionsName, HttpClient client)
    {
        TimeSpan configured = Live(serviceProvider, optionsName).Timeout.EffectiveClientTimeout;
        TimeSpan current = client.Timeout;

        // Infinite is either the normalizing action above or the platform resilience handler's own
        // assignment, and this replaces both. Any finite value that survived to here is a consumer's
        // statement -- including exactly 100 seconds -- and one this schema can express properly, which is
        // what the message points at.
        if (current != Timeout.InfiniteTimeSpan && current != configured)
        {
            throw new InvalidOperationException(
                $"HTTP client '{optionsName}' has HttpClient.Timeout set to {current} in code, and " +
                $"HttpResilience resolves it to {configured}. Two statements about the outermost bound on a " +
                "request, and the one in code is outside the schema, so validation cannot check it against " +
                "Timeout:Total -- at or below the total budget it truncates the pipeline rather than backing " +
                "it up, and does so with a bare TaskCanceledException carrying none of the pipeline's " +
                "context. State it as Timeout:Client instead, which is validated to be strictly greater than " +
                "Timeout:Total, or remove the ConfigureHttpClient assignment. The same assignment made in a " +
                "typed client's own constructor cannot be reported at all -- the constructor runs after " +
                "IHttpClientFactory has finished building the client -- so if this client is typed, check " +
                "its constructor too.");
        }

        client.Timeout = configured;
    }

    /// <summary>
    /// This client's effective options, as every consumer of them sees them.
    /// </summary>
    /// <remarks>
    /// Read at the moment a handler chain or a pipeline is built rather than captured at registration, so a
    /// consumer's <c>Configure</c> or <c>PostConfigure</c> reaches the running client instead of being
    /// reported without being in effect. The exceptions are the decisions in
    /// <see cref="StructuralDecisions"/>, which the handler chain has already been composed from.
    /// </remarks>
    private static HttpResilienceOptions Live(IServiceProvider serviceProvider, string optionsName) =>
        serviceProvider.GetRequiredService<IOptionsMonitor<HttpResilienceOptions>>().Get(optionsName);

    /// <summary>
    /// Partitions pipelines by authority, reading the allow-list when the selector is first needed.
    /// </summary>
    private static Func<IServiceProvider, Func<HttpRequestMessage, string>> AuthoritySelector(
        string optionsName) =>
        serviceProvider => PipelineKeySelector.Create(Live(serviceProvider, optionsName).PipelineSelection);

    /// <summary>
    /// Binds and validates the options for this client, registers them under the client's name, and applies
    /// everything that is independent of the resilience pipeline.
    /// </summary>
    private static (HttpResilienceOptions Options, string Scope) Register(
        IHttpClientBuilder builder,
        string? clientName,
        Action<HttpResilienceOptions>? configure,
        PipelineKind kind)
    {
        HttpResilienceRegistration registration = ResolveRegistration(builder);
        IConfigurationSection root = registration.Section;
        string optionsName = builder.Name;

        GuardAgainstDoubleRegistration(registration, builder.Name);

        // A client reads the section named after it unless told otherwise, so the common case needs no
        // argument. An explicit empty string means root values only.
        string sectionName = clientName ?? builder.Name;

        // Recorded so that a section nothing reads fails startup instead of binding to nothing in silence.
        // See UnusedClientSectionValidator.
        registration.MarkSectionConsumed(sectionName);

        var options = new HttpResilienceOptions();
        BindEffective(root, sectionName, configure, kind, options);

        // Validated here, before any of these values is used to build a handler. Deferring to startup
        // validation alone would mean registration code reading a value the validator was about to reject,
        // and the resulting failure would name a type rather than the section an operator has to edit.
        string scope = HttpResilienceOptionsValidator.ScopeFor(sectionName);
        List<string> failures = [.. HttpResilienceOptionsValidator.Collect(options, scope, kind)];
        failures.AddRange(CollectInertConfiguration(root, sectionName, scope, kind, options));
        if (failures.Count > 0)
        {
            throw new OptionsValidationException(optionsName, typeof(HttpResilienceOptions), failures);
        }

        // The registered options are produced by re-running the binding above, not by copying the object it
        // produced. Same inputs, same deterministic path, so the two cannot disagree -- and there is no
        // hand-maintained mirror of the options graph for a new property to be forgotten in.
        //
        // Registered as Configure rather than PostConfigure so that a consumer's own Configure and
        // PostConfigure both compose on top of it, the way the options pattern says they should. The
        // pipeline reads these same options when it is built, so what a consumer changes here is what the
        // pipeline runs -- see StandardPipelineConfigurator.
        builder.Services.AddOptions<HttpResilienceOptions>(optionsName)
            .Configure(target => BindEffective(root, sectionName, configure, kind, target))
            .ValidateOnStart();

        // Re-checked at startup against this client's own pipeline, because the two pipelines have different
        // budget rules -- and against the structural decisions this registration has already made, which are
        // the only values a later change cannot reach. See StructuralDecisions.
        builder.Services.AddSingleton<IValidateOptions<HttpResilienceOptions>>(
            new NamedPipelineOptionsValidator(optionsName, scope, kind, StructuralDecisions.From(options)));

        // Connection settings are infrastructure, not policy: disabling the resilience pipeline during an
        // incident must not also discard connection-pool tuning.
        // The filter is also needed when the only thing to apply is the redirect bound, which holds whether
        // or not connection tuning is switched on.
        if (options.Connection.Enabled || options.Connection.AllowAutoRedirect is false)
        {
            // A handler-builder filter rather than ConfigurePrimaryHttpMessageHandler, so the settings are
            // applied after every other registration instead of racing them. See ConnectionHandlerFilter.
            builder.Services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IHttpMessageHandlerBuilderFilter, ConnectionHandlerFilter>());
        }

        if (options.Connection.Enabled)
        {
            // PooledConnectionLifetime already bounds connection age and DNS staleness. Leaving the factory's
            // own 2-minute handler rotation on as well would cycle the connection pool twice as often. Tied
            // to Connection:Enabled alone -- disabling redirects must not silently stop handler rotation.
            builder.SetHandlerLifetime(Timeout.InfiniteTimeSpan);
        }

        if (options.Enabled)
        {
            // Tallied here, where the decisions are, rather than derived by the guard from the composed
            // chain -- which could not tell a second standard handler from the AddResilienceHandler this
            // package tells consumers to use. See ResilienceHandlerCountFilter.
            //
            // The conditions mirror AddConcurrencyLimiterIfEnabled, AddConcurrencyBackstopIfDisplaced and
            // AddRateLimiterIfEnabled. That duplication is deliberate and is not on trust: every combination
            // is pinned by ConsumerBoundaryTests.EveryPipelineShape_TalliesItsOwnHandlers, and a tally that
            // drifts low makes the guard fire on a legitimate registration -- which is how the hedging base
            // below was found to be 2 rather than the 1 first assumed.
            int resilienceHandlers = PlatformResilienceHandlers(kind);
            if (options.ConcurrencyLimiter.Enabled)
            {
                resilienceHandlers++;
            }

            if (options.RateLimiter.Enabled &&
                (kind is PipelineKind.Hedging || !options.ConcurrencyLimiter.Enabled))
            {
                // The hedged client's own rate-limiter handler, or the backstop a rate limiter displaced on
                // the standard pipeline. Never both: a standard client's rate limiter takes the platform's
                // slot rather than a handler of its own.
                resilienceHandlers++;
            }

            registration.RecordResilienceHandlers(builder.Name, resilienceHandlers);

            // A client that repeats a mutating request is the same shape of invisible state as a client with
            // no pipeline at all, and a worse one. Registered unconditionally and reading the flags inside
            // PostConfigure, for the reason given on the hedging guard: a safety notice a later
            // configuration change can delete by deleting its registration is not a notice.
            string unsafeNoticeClient = builder.Name;
            string allowListPath = AllowListPath(root, sectionName, scope);
            builder.Services.AddSingleton<IPostConfigureOptions<HttpResilienceOptions>>(serviceProvider =>
                new UnsafeMethodNotice(
                    optionsName,
                    unsafeNoticeClient,
                    scope,
                    allowListPath,
                    kind,
                    serviceProvider.GetService<ILoggerFactory>()));

            // Whether a breaker can ever open is arithmetic over two configured values and the traffic one
            // replica sees. The package knows the first half at startup and the operator usually has not
            // done the division -- so it states the rate the configured breaker needs, once, per client.
            builder.Services.AddSingleton<IPostConfigureOptions<HttpResilienceOptions>>(serviceProvider =>
                new CircuitBreakerReachNotice(
                    optionsName,
                    unsafeNoticeClient,
                    scope,
                    kind,
                    serviceProvider.GetService<ILoggerFactory>()));

            if (options.RateLimiter.Enabled)
            {
                // One limiter per client, owned and disposed by the container. Registered with the metrics
                // gauges as it is created: the limiter does not exist until first resolve, and this type has
                // no service provider to go looking for it later.
                //
                // Keyed on RateLimiterKey rather than on the client name: the key namespace is shared per
                // service type, RateLimiter is a BCL type, and a consumer keying its own limiter by a domain
                // name would silently replace this one in the pipeline. See RateLimiterKey.
                builder.Services.AddKeyedSingleton<RateLimiter>(
                    new RateLimiterKey(optionsName),
                    (serviceProvider, key) =>
                    {
                        // Read live, for the reason given on rule 7 in CLAUDE.md and applied everywhere else
                        // in this file: this factory runs when the pipeline is first built, which is after
                        // every Configure and PostConfigure. Capturing the registration-time
                        // RateLimiterOptions instead was the one place a value was not live -- and the one
                        // place nothing caught it, because the limiter's shape is not in StructuralDecisions
                        // so NamedPipelineOptionsValidator had nothing to compare. Measured: a consumer
                        // post-configuring PermitLimit to 50 got a limiter enforcing the configured 2, with
                        // IOptionsMonitor reporting 50 and startup validation clean.
                        RateLimiterOptions live = Live(serviceProvider, optionsName).RateLimiter;
                        RateLimiter limiter = RateLimiterFactory.Create(live);
                        serviceProvider.GetService<HttpResilienceMetrics>()
                            ?.Track(((RateLimiterKey)key!).ClientName, LimiterKind.Rate, limiter);
                        return limiter;
                    });
            }
        }
        else
        {
            // Registering the package and leaving it switched off is supported, but it should never be
            // silent: the same run-time state is produced by a missing configuration key. Registered as a
            // post-configure on this client's options rather than as a ConfigureHttpClient action, so the
            // warning is emitted when the host materializes the options at startup -- before it accepts
            // traffic -- rather than waiting for the client's first use.
            string disabledClientName = builder.Name;
            builder.Services.AddSingleton<IPostConfigureOptions<HttpResilienceOptions>>(serviceProvider =>
                new DisabledClientNotice(
                    optionsName,
                    disabledClientName,
                    scope,
                    serviceProvider.GetService<ILoggerFactory>()));
        }

        return (options, scope);
    }

    /// <summary>
    /// How many resilience handlers the platform's own standard or hedging handler contributes.
    /// </summary>
    /// <remarks>
    /// Measured, not assumed. <c>AddStandardResilienceHandler</c> adds one;
    /// <c>AddStandardHedgingHandler</c> adds <b>two</b>, because it composes a routing handler around the
    /// hedging one. The first version of the handler-count guard assumed one for both and fired on every
    /// legitimate hedged client in the suite, which is the failure direction this number has: too low and the
    /// guard rejects correct registrations loudly, too high and it silently weakens by one. Loud is the right
    /// direction, and <c>ConsumerBoundaryTests.EveryPipelineShape_TalliesItsOwnHandlers</c> pins both numbers
    /// so a platform change names itself instead of surfacing as unrelated failures.
    /// </remarks>
    private static int PlatformResilienceHandlers(PipelineKind kind) =>
        kind is PipelineKind.Hedging ? 2 : 1;

    /// <summary>
    /// Where this client's <c>Retry:RetryableMethods</c> is stated, for the startup notice to name.
    /// </summary>
    /// <remarks>
    /// The list is inheritable, so the client's own section is not always where it came from. Resolved at
    /// registration, where both sections are in hand; a list that neither section states arrived from the
    /// <c>configure</c> delegate or a later <c>Configure</c>, and saying so is more useful than naming a
    /// configuration key that is not in the file.
    /// </remarks>
    private static string AllowListPath(IConfigurationSection root, string sectionName, string scope)
    {
        const string Key = "Retry:RetryableMethods";

        if (!string.IsNullOrEmpty(sectionName) &&
            root.GetSection(HttpResilienceConfigurationKeys.ClientsSection)
                .GetSection(sectionName)
                .GetSection(Key)
                .Exists())
        {
            return $"{scope}:{Key}";
        }

        return root.GetSection(Key).Exists()
            ? $"{root.Path}:{Key}, inherited by this client"
            : $"{Key}, set in code rather than in configuration";
    }

    /// <summary>
    /// Reports configuration a client states and its pipeline never reads.
    /// </summary>
    /// <remarks>
    /// Only the client's own section is examined, never the inherited root: root retry configuration is what
    /// every standard client in the application uses, and a hedged client sharing that root must not be the
    /// thing that fails startup -- and the same holds for root hedging values under a standard client. What
    /// this catches is a client stating keys for the pipeline it does <i>not</i> have. The dangerous case in
    /// either direction is a <c>DisableForUnsafeHttpMethods</c> flag: an author has recorded a decision about
    /// duplicating mutating requests, and it is not the decision in force. The same reasoning already rejects
    /// an authority allow-list under <c>Mode: None</c>.
    /// <para>
    /// The third case is the one that has no mirror. On the standard pipeline
    /// <c>Retry:RetryableMethods</c> replaces <c>Retry:DisableForUnsafeHttpMethods</c> outright, so a client
    /// stating the flag while a list is in force has written the safe statement and had it discarded. The
    /// flag being <c>false</c> beside a list is already refused by the options validator, which also reaches
    /// the <c>configure</c> delegate; this covers the flag being <c>true</c>, which is the direction where
    /// the discarded statement is the protective one. Statedness has to come from the section rather than
    /// from the bound value, because the flag defaults to <c>true</c> and the root is required to leave it
    /// that way -- so a bound <c>true</c> cannot be told apart from silence.
    /// </para>
    /// </remarks>
    private static IEnumerable<string> CollectInertConfiguration(
        IConfigurationSection root,
        string sectionName,
        string scope,
        PipelineKind kind,
        HttpResilienceOptions options)
    {
        if (string.IsNullOrEmpty(sectionName))
        {
            yield break;
        }

        IConfigurationSection client = root
            .GetSection(HttpResilienceConfigurationKeys.ClientsSection)
            .GetSection(sectionName);

        if (kind is PipelineKind.Hedging && client.GetSection("Retry").Exists())
        {
            yield return
                $"{scope} -- Retry: this client is registered with AddHedgedHttpResilience, and the hedging " +
                "pipeline has no retry strategy, so every key under Retry is bound and never read. Remove " +
                "them, or register the client with AddHttpResilience instead. If the intent was to control " +
                "whether mutating requests are duplicated, the setting on this pipeline is " +
                "Hedging:DisableForUnsafeHttpMethods.";
        }

        if (kind is not PipelineKind.Standard)
        {
            yield break;
        }

        if (client.GetSection("Hedging").Exists())
        {
            yield return
                $"{scope} -- Hedging: this client is registered with AddHttpResilience, and the standard " +
                "pipeline has no hedging strategy, so every key under Hedging is bound and never read. " +
                "Remove them, or register the client with AddHedgedHttpResilience instead. If the intent " +
                "was to control whether mutating requests are duplicated, the setting on this pipeline is " +
                "Retry:DisableForUnsafeHttpMethods.";
        }

        // Statedness comes from the section rather than from the bound value, for the reason that moved this
        // rule here from the options validator: a root authority list is how a fleet states one destination
        // allow-list for its hedged clients, every client inherits the root, and judging the bound value made
        // that list fail every standard client in the process. A client that writes the list itself under
        // Mode: None has still written configuration nothing reads.
        if (client.GetSection("PipelineSelection:Authorities").Exists() &&
            options.PipelineSelection.Mode is PipelineSelectionMode.None)
        {
            yield return
                $"{scope} -- PipelineSelection:Authorities: this client states an allow-list while " +
                "PipelineSelection:Mode is None, so every request shares one pipeline and the circuit " +
                "breaker isolation the list implies silently does not happen. Set " +
                "PipelineSelection:Mode to ByAuthority on this client, or remove the list. A list " +
                $"inherited from {HttpResilienceConfigurationKeys.RootSection}:PipelineSelection:Authorities " +
                "is left alone -- it is there for this application's hedged clients, which require one, and " +
                "a standard client sharing that root is not what should fail startup.";
        }

        // False beside a list is the options validator's, and its message is the better one for that case.
        if (client.GetSection("Retry:DisableForUnsafeHttpMethods").Exists() &&
            options.Retry.DisableForUnsafeHttpMethods &&
            options.Retry.RetryableMethods is { Count: > 0 } inForce)
        {
            yield return
                $"{scope} -- Retry:DisableForUnsafeHttpMethods: this client states the guard, but " +
                "Retry:RetryableMethods is in force for it and replaces the guard entirely, so the flag is " +
                "bound and never read. Two written statements about repeating mutating requests, and this " +
                $"is not the one in force: the methods actually retried are {string.Join(", ", inForce)}. " +
                "The list may be inherited -- if this client's section does not state one, it came from " +
                $"{HttpResilienceConfigurationKeys.RootSection}:Retry:RetryableMethods. State " +
                "Retry:RetryableMethods on this client with the methods it may actually repeat (an empty " +
                "list means the default guard: GET, HEAD, OPTIONS and TRACE), or remove this flag.";
        }
    }

    /// <summary>
    /// Fails a second registration on the same client rather than nesting two pipelines.
    /// </summary>
    /// <remarks>
    /// Two pipelines nest rather than merge, so retries multiply: a client configured for three attempts
    /// makes nine origin calls, its total timeout is applied twice, and nothing throws or logs. The common
    /// way in is a shared registration extension that already adds resilience being called by an application
    /// that adds it again.
    /// </remarks>
    private static void GuardAgainstDoubleRegistration(HttpResilienceRegistration registration, string clientName)
    {
        if (!registration.TryAddClient(clientName))
        {
            throw new InvalidOperationException(
                $"Resilience is already configured for HTTP client '{clientName}'. Adding it twice nests " +
                "two pipelines, so retries multiply rather than add and the total timeout is applied twice. " +
                "Remove the duplicate AddHttpResilience or AddHedgedHttpResilience call -- a client that " +
                "needs an extra strategy should add it with AddResilienceHandler instead.");
        }
    }

    /// <summary>
    /// Rejects requests to authorities outside the allow-list, outermost so nothing is allocated for them.
    /// </summary>
    private static void AddAuthorityAllowList(IHttpClientBuilder builder)
    {
        string optionsName = builder.Name;
        builder.AddHttpMessageHandler(serviceProvider =>
            new AuthorityAllowListHandler(
                AuthorityIndex.Create(Live(serviceProvider, optionsName).PipelineSelection)));
    }

    /// <summary>
    /// Restores the concurrency backstop when a configured rate limiter has taken the platform's limiter slot.
    /// </summary>
    /// <remarks>
    /// The standard handler has exactly one limiter slot. Assigning a rate limiter to it replaces the
    /// concurrency limiter that was there, so enabling rate limiting would otherwise remove a safety control
    /// rather than add one. Adding it back as its own handler keeps the invariant that the backstop always
    /// applies, and keeps it outside the retry loop so one slot still covers a whole logical request.
    /// <para>
    /// Not needed when the client has a concurrency cap of its own: startup validation holds
    /// <c>Limit</c> at or below <c>Backstop</c>, and a queued request holds no slot, so that limiter already
    /// bounds in-flight requests below the backstop. Adding a second one would cost a handler and an
    /// allocation per request to enforce a bound that already holds.
    /// </para>
    /// </remarks>
    private static void AddConcurrencyBackstopIfDisplaced(
        IHttpClientBuilder builder,
        HttpResilienceOptions options,
        string scope)
    {
        if (!options.RateLimiter.Enabled || options.ConcurrencyLimiter.Enabled)
        {
            return;
        }

        string clientName = builder.Name;
        string backstopPath = $"{scope}:ConcurrencyLimiter:Backstop";

        AddOwnedConcurrencyLimiter(builder, LimiterKind.Backstop, concurrency => new ConcurrencyLimiterOptions
        {
            PermitLimit = concurrency.Backstop,
            QueueLimit = 0
        });

        builder.AddResilienceHandler(ConcurrencyBackstopHandlerName, (pipeline, context) =>
        {
            int backstop = Live(context.ServiceProvider, clientName).ConcurrencyLimiter.Backstop;
            ILogger? logger = Logger(context.ServiceProvider);
            RateLimiter limiter = context.ServiceProvider.GetRequiredKeyedService<RateLimiter>(
                new RateLimiterKey(clientName, LimiterKind.Backstop));

            pipeline.AddRateLimiter(new RateLimiterStrategyOptions
            {
                RateLimiter = args => limiter.AcquireAsync(1, args.Context.CancellationToken),
                OnRejected = logger is null ? null : _ =>
                {
                    HttpResilienceLogging.ConcurrencyBackstopRejected(logger, clientName, backstop, backstopPath);
                    return default;
                }
            });
        });
    }

    /// <summary>
    /// Registers a concurrency limiter this package owns, rather than letting Polly build one from
    /// <c>DefaultRateLimiterOptions</c>.
    /// </summary>
    /// <remarks>
    /// The reason is observability, and it is the only reason: handing Polly a shape means Polly constructs
    /// the limiter internally and there is no instance to call <c>GetStatistics()</c> on. The concurrency
    /// queue is the one place a request can wait outside <c>Timeout:Total</c> -- both limiters sit outside it,
    /// which is what makes one permit cover a whole logical request -- and <c>QueueLimit</c> may be 1,000, so
    /// "the queue is filling" is a signal an operator needs before the Warning that says it overflowed.
    /// <para>
    /// Behavior is unchanged. Both callers add the limiter as a handler of their own, one per client, so a
    /// single instance is what Polly was already building. That is <i>not</i> true of the limiter slot inside
    /// the platform's standard or hedging handler: those are one per pipeline, which is what makes the
    /// undisplaced backstop per authority under <c>ByAuthority</c>. Supplying an instance there would quietly
    /// convert a per-authority bound into a per-client one, so it is left alone and the gap is documented.
    /// </para>
    /// <para>
    /// Registered as a keyed singleton so the container disposes it: Polly disposes a limiter it created from
    /// <c>DefaultRateLimiterOptions</c> and does <b>not</b> dispose one supplied through
    /// <c>RateLimiterStrategyOptions.RateLimiter</c>, because that one is the caller's. Keyed on
    /// <see cref="RateLimiterKey"/> for the reason on that type, and on the kind as well because a client may
    /// own two limiters.
    /// </para>
    /// </remarks>
    private static void AddOwnedConcurrencyLimiter(
        IHttpClientBuilder builder,
        LimiterKind kind,
        Func<HttpResilience.NET.Options.ConcurrencyLimiterOptions, ConcurrencyLimiterOptions> shape)
    {
        string optionsName = builder.Name;

        builder.Services.AddKeyedSingleton<RateLimiter>(
            new RateLimiterKey(optionsName, kind),
            (serviceProvider, key) =>
            {
                // Live, like every other value: this factory runs when the pipeline is first built, after
                // every Configure and PostConfigure.
                RateLimiter limiter = new ConcurrencyLimiter(
                    shape(Live(serviceProvider, optionsName).ConcurrencyLimiter));

                serviceProvider.GetService<HttpResilienceMetrics>()
                    ?.Track(((RateLimiterKey)key!).ClientName, kind, limiter);

                return limiter;
            });
    }

    private static void AddConcurrencyLimiterIfEnabled(
        IHttpClientBuilder builder,
        HttpResilienceOptions options,
        string scope)
    {
        if (!options.ConcurrencyLimiter.Enabled)
        {
            return;
        }

        string clientName = builder.Name;
        string limitPath = $"{scope}:ConcurrencyLimiter:Limit";

        // Registered before the standard handler. IHttpClientFactory composes the first additional handler as
        // the outermost one, so this holds a single slot for the whole logical request rather than
        // re-acquiring one for every retry attempt.
        //
        // AddRateLimiter with a concurrency limiter rather than AddConcurrencyLimiter: the shorthand has no
        // OnRejected hook, and a saturated bulkhead an operator cannot see is the thing this reports.
        AddOwnedConcurrencyLimiter(builder, LimiterKind.Concurrency, concurrency => new ConcurrencyLimiterOptions
        {
            PermitLimit = concurrency.Limit!.Value,
            QueueLimit = concurrency.QueueLimit
        });

        builder.AddResilienceHandler(ConcurrencyLimiterHandlerName, (pipeline, context) =>
        {
            HttpResilience.NET.Options.ConcurrencyLimiterOptions concurrency =
                Live(context.ServiceProvider, clientName).ConcurrencyLimiter;
            int limit = concurrency.Limit!.Value;
            int queueLimit = concurrency.QueueLimit;
            ILogger? logger = Logger(context.ServiceProvider);
            RateLimiter limiter = context.ServiceProvider.GetRequiredKeyedService<RateLimiter>(
                new RateLimiterKey(clientName, LimiterKind.Concurrency));

            pipeline.AddRateLimiter(new RateLimiterStrategyOptions
            {
                RateLimiter = args => limiter.AcquireAsync(1, args.Context.CancellationToken),
                OnRejected = logger is null ? null : _ =>
                {
                    HttpResilienceLogging.ConcurrencyLimiterRejected(
                        logger, clientName, limit, queueLimit, limitPath);
                    return default;
                }
            });
        });
    }

    private static ILogger? Logger(IServiceProvider serviceProvider) =>
        serviceProvider.GetService<ILoggerFactory>()?.CreateLogger("HttpResilience");

    /// <summary>
    /// Adds the rate limiter as its own handler, for the hedging pipeline only.
    /// </summary>
    /// <remarks>
    /// The standard handler carries a rate limiter of its own, placed outermost within it, so the standard
    /// path uses that. The hedging handler has no equivalent, and its per-endpoint limiter would charge a
    /// permit per hedged attempt -- which means a rejected supplementary attempt surfaces a
    /// <c>RateLimiterRejectedException</c> to the caller in place of the real outcome. Adding the limiter
    /// outside the hedging handler instead keeps one permit meaning one logical request on both pipelines.
    /// </remarks>
    private static void AddRateLimiterIfEnabled(
        IHttpClientBuilder builder,
        HttpResilienceOptions options,
        string scope)
    {
        if (!options.RateLimiter.Enabled)
        {
            return;
        }

        string clientName = builder.Name;

        builder.AddResilienceHandler(RateLimiterHandlerName, (pipeline, context) =>
        {
            string permitPath = $"{scope}:{Live(context.ServiceProvider, clientName).RateLimiter.PermitKey}";
            RateLimiter limiter =
                context.ServiceProvider.GetRequiredKeyedService<RateLimiter>(new RateLimiterKey(clientName));
            ILogger? logger = Logger(context.ServiceProvider);
            pipeline.AddRateLimiter(new RateLimiterStrategyOptions
            {
                RateLimiter = args => limiter.AcquireAsync(1, args.Context.CancellationToken),
                OnRejected = logger is null ? null : _ =>
                {
                    HttpResilienceLogging.RateLimiterRejected(logger, clientName, permitPath);
                    return default;
                }
            });
        });
    }

    /// <summary>
    /// Fills <paramref name="target"/> with this client's effective configuration: the root section, the
    /// client's own section on top of it, the caller's <c>configure</c> delegate, and the redirect bound the
    /// pipeline implies.
    /// </summary>
    /// <remarks>
    /// Run twice, deliberately, against two different instances: once at registration to decide which
    /// handlers to add and to fail fast on bad values, and once as the named options' <c>Configure</c>
    /// action. It is the same function over the same inputs, so the registration's view and the registered
    /// options cannot drift -- which is what a hand-written copier had to be tested by reflection to
    /// guarantee.
    /// </remarks>
    private static void BindEffective(
        IConfigurationSection root,
        string sectionName,
        Action<HttpResilienceOptions>? configure,
        PipelineKind kind,
        HttpResilienceOptions target)
    {
        Bind(root, sectionName, configure, target);

        // Recorded before the line below, because resolving destroys the difference between "nobody stated
        // this" and "somebody asked for the runtime default" -- and that difference is the whole of what stops
        // SocketsHttpHandlerFactory overwriting the redirect setting on a handler the consumer owns. It is
        // read nowhere else. See ConnectionOptions.AllowAutoRedirectStated.
        target.Connection.AllowAutoRedirectStated = target.Connection.AllowAutoRedirect.HasValue;

        // Resolved here rather than re-derived by each reader, so the validator, the handler filter and the
        // primary handler all see one concrete value. The hedging pipeline rejects requests to unlisted
        // authorities, so it is the one that must not follow a redirect around its own list.
        // See ConnectionOptions.AllowAutoRedirect.
        target.Connection.AllowAutoRedirect =
            target.Connection.FollowsRedirects(enforcesAllowList: kind is PipelineKind.Hedging);
    }

    private static void Bind(
        IConfigurationSection root,
        string sectionName,
        Action<HttpResilienceOptions>? configure,
        HttpResilienceOptions options)
    {
        root.Bind(options);

        // Binding only assigns keys that are present, so a client section states just what it changes.
        if (!string.IsNullOrEmpty(sectionName))
        {
            IConfigurationSection client = root
                .GetSection(HttpResilienceConfigurationKeys.ClientsSection)
                .GetSection(sectionName);

            ResetListsStatedBy(client, options);
            client.Bind(options);
        }

        configure?.Invoke(options);
    }

    /// <summary>
    /// Clears the list-valued properties this client's section states, so binding replaces them instead of
    /// adding to what the root left behind.
    /// </summary>
    /// <remarks>
    /// The configuration binder <b>adds to</b> a non-null collection rather than replacing it, so binding the
    /// root and then the client section onto one instance unions every list. Scalars override and lists
    /// accumulate, which means a client could widen an inherited list but never narrow one -- and both lists
    /// in this schema are allow-lists, so widening is the unsafe direction. A client stating
    /// <c>Retry:RetryableMethods: ["GET"]</c> under a root that also names POST kept retrying POST bodies,
    /// and a hedged client stating its own <c>PipelineSelection:Authorities</c> silently kept every authority
    /// the root listed as a destination it may reach.
    /// <para>
    /// Only a list the client actually states is cleared, so a client that says nothing still inherits the
    /// root's -- which is what makes a fleet-wide allow-list expressible in one place. Pinned by
    /// <c>ConfigurationInheritanceTests</c>.
    /// </para>
    /// </remarks>
    private static void ResetListsStatedBy(IConfigurationSection client, HttpResilienceOptions options)
    {
        if (client.GetSection("Retry:RetryableMethods").Exists())
        {
            options.Retry.RetryableMethods = null;
        }

        if (client.GetSection("PipelineSelection:Authorities").Exists())
        {
            options.PipelineSelection.Authorities = null;
        }
    }

    private static HttpResilienceRegistration ResolveRegistration(IHttpClientBuilder builder)
    {
        if (HttpResilienceRegistration.Find(builder.Services) is { } registration)
        {
            return registration;
        }

        throw new InvalidOperationException(
            $"Call services.AddHttpResilience(configuration) before adding resilience to client " +
            $"'{builder.Name}'. It registers the '{HttpResilienceConfigurationKeys.RootSection}' " +
            "configuration section that per-client registrations build on.");
    }
}
