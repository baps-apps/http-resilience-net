using HttpResilience.NET.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;

namespace HttpResilience.NET.Tests.Behavior;

/// <summary>
/// What happens when a <i>consumer</i> also does something to a client this package configured.
/// </summary>
/// <remarks>
/// Guards are written against this package's API, so these tests add a consumer to the same client and
/// assert the outcome at the origin or on the constructed client -- not that registration threw.
/// </remarks>
public class ConsumerBoundaryTests
{
    private static IConfigurationSection Configuration(Settings settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings.Build()).Build()
            .GetSection("HttpResilience");

    private static (ServiceCollection Services, IHttpClientBuilder Builder, RecordingHandler Origin) Client(
        Settings settings,
        bool hedged = false,
        string name = "test",
        ListLoggerProvider? logs = null)
    {
        var services = new ServiceCollection();
        services.AddHttpResilience(Configuration(settings));

        if (logs is not null)
        {
            services.AddLogging(logging => logging.AddProvider(logs));
        }

        var origin = new RecordingHandler();
        IHttpClientBuilder builder = services.AddHttpClient(name);
        _ = hedged ? builder.AddHttpResilienceHedgingHandler() : builder.AddHttpResilienceHandler();
        builder.ConfigurePrimaryHttpMessageHandler(() => origin);

        return (services, builder, origin);
    }

    private static HttpClient Create(ServiceProvider provider, string name = "test") =>
        provider.GetRequiredService<IHttpClientFactory>().CreateClient(name);

    /// <summary>
    /// A consumer adding the platform's own standard handler to a client this package configured nests two
    /// pipelines, and the package says so.
    /// </summary>
    /// <remarks>
    /// The package cannot prevent this, so the test asserts the damage as well as the notice: one GET makes
    /// <b>nine</b> origin calls. The supported <c>AddResilienceHandler</c> adds a handler to the same chain,
    /// so the excess is not attributable through public API (see <c>ResilienceHandlerCountFilter</c>).
    /// <para>
    /// If the arithmetic changes, update the number quoted in README.md, ARCHITECTURE.md, OPERATIONS.md and
    /// RUNBOOK.md.
    /// </para>
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task AConsumersOwnStandardHandler_NestsTwoPipelines_AndSaysSo()
    {
        var logs = new ListLoggerProvider();
        (ServiceCollection services, IHttpClientBuilder builder, RecordingHandler origin) =
            Client(Settings.Enabled(), logs: logs);
        builder.AddStandardResilienceHandler();

        await using ServiceProvider provider = services.BuildServiceProvider();
        HttpClient client = Create(provider);

        (await client.GetAsync("http://origin.test/x", TestContext.Current.CancellationToken)).Dispose();

        // Three configured attempts, retried three times by the outer pipeline. This is the damage.
        Assert.Equal(9, origin.Count);

        string notice = Assert.Single(logs.Records, r => r.Contains("resilience handlers", StringComparison.Ordinal));
        Assert.Contains("[Information]", notice, StringComparison.Ordinal);
        Assert.Contains("NESTED", notice, StringComparison.Ordinal);
        Assert.Contains("nine origin calls", notice, StringComparison.Ordinal);
    }

    /// <summary>
    /// The nested pipelines share the client's platform options, so the damage grows as the square of the
    /// configured attempts: at the largest <c>Retry:MaxRetries</c> the schema accepts, 11 × 11 = <b>121</b>
    /// origin calls for one failing GET.
    /// </summary>
    /// <remarks>
    /// The figure OPERATIONS.md and RUNBOOK.md quote as the worst case. It assumes failures and backoff fit inside
    /// <c>Timeout:Total</c>, as they do here (instant failures, no backoff).
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task AConsumersOwnStandardHandler_AtTheMaximumRetries_Makes121OriginCalls()
    {
        (ServiceCollection services, IHttpClientBuilder builder, RecordingHandler origin) =
            Client(Settings.Enabled().Set("Retry:MaxRetries", "10"));
        builder.AddStandardResilienceHandler();

        await using ServiceProvider provider = services.BuildServiceProvider();
        HttpClient client = Create(provider);

        (await client.GetAsync("http://origin.test/x", TestContext.Current.CancellationToken)).Dispose();

        Assert.Equal(121, origin.Count);
    }

    /// <summary>
    /// .NET Aspire's ServiceDefaults add the platform's standard handler to every client through
    /// <c>ConfigureHttpClientDefaults</c>. This package replaces it on the clients it configures, with no call
    /// from the consumer, whichever is registered first.
    /// </summary>
    /// <remarks>
    /// Without the replacement the defaults' pipeline is outermost and its four attempts each run this package's
    /// three: twelve origin calls. Fails if the defaults' handler survives (twelve calls, or four for hedging),
    /// if the finite client timeout is lost, or if the replacement is not reported once.
    /// </remarks>
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task AnAspireStyleDefaultHandler_IsReplaced_WithNoCallFromTheConsumer(bool defaultsFirst, bool hedged)
    {
        var logs = new ListLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(logs));
        services.AddHttpResilience(Configuration(Settings.Hedged().Set("Hedging:Delay", "00:00:10")));

        // The platform's defaults (three retries), with no delay so a surviving handler would not stall the test.
        void AddDefaults() => services.ConfigureHttpClientDefaults(defaults => defaults
            .AddStandardResilienceHandler(options => options.Retry.Delay = TimeSpan.Zero));

        if (defaultsFirst)
        {
            AddDefaults();
        }

        var origin = new RecordingHandler();
        IHttpClientBuilder builder = services.AddHttpClient("test");
        _ = hedged ? builder.AddHttpResilienceHedgingHandler() : builder.AddHttpResilienceHandler();
        builder.ConfigurePrimaryHttpMessageHandler(() => origin);

        if (!defaultsFirst)
        {
            AddDefaults();
        }

        await using ServiceProvider provider = services.BuildServiceProvider();
        HttpClient client = Create(provider);
        (await client.GetAsync("http://origin.test/x", TestContext.Current.CancellationToken)).Dispose();
        RebuildHandlerChain(provider, "test");

        // Standard: three attempts. Hedged: the primary and one hedged attempt after the 500.
        Assert.Equal(hedged ? 2 : 3, origin.Count);
        Assert.Equal(TimeSpan.FromSeconds(30) + TimeSpan.FromSeconds(30), client.Timeout);
        Assert.DoesNotContain(logs.Records, r => r.Contains("resilience handlers;", StringComparison.Ordinal));

        LogEntry notice = Assert.Single(logs.WithEvent(HttpResilience.NET.Internal.HttpResilienceLogging.ConfigurationCategory, 17));
        Assert.Equal(LogLevel.Information, notice.Level);
        Assert.Contains("'test'", notice.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// .NET Aspire's ServiceDefaults add service discovery <i>inside</i> the standard handler they add. This
    /// package's pipeline takes the replaced handler's place, so the resolver stays inside it: every attempt is
    /// resolved again, and the allow-list sees the logical authority the caller used.
    /// </summary>
    /// <remarks>
    /// Defaults actions run before a named client's, so appending this package's handlers put the resolver
    /// outside them. Fails if it is: the standard client's three attempts all reach one endpoint, and the hedged
    /// client's allow-list sees <c>http://10.0.0.x:8080</c> and refuses the request.
    /// </remarks>
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task AspireServiceDefaults_KeepServiceDiscoveryInsideThePipeline(bool defaultsFirst, bool hedged)
    {
        (ServiceCollection services, List<string> reached) = ServiceDiscoveryClient(
            hedged,
            defaults =>
            {
                // The ServiceDefaults template's two calls, in its order.
                defaults.AddStandardResilienceHandler(options => options.Retry.Delay = TimeSpan.Zero);
                defaults.AddServiceDiscovery();
            },
            defaultsFirst);

        await using ServiceProvider provider = services.BuildServiceProvider();
        (await Create(provider, "catalog").GetAsync("http://catalog/x", TestContext.Current.CancellationToken))
            .Dispose();

        // Standard: three attempts. Hedged: the primary and one hedged attempt after the 500.
        Assert.Equal(hedged ? 2 : 3, reached.Count);
        Assert.All(reached, authority => Assert.StartsWith("http://10.0.0.", authority, StringComparison.Ordinal));

        // Resolved per attempt, so the round-robin selector spreads them over both endpoints.
        Assert.Equal(2, reached.Distinct(StringComparer.Ordinal).Count());

        List<string> chain = HandlerChain(provider, "catalog");
        Assert.True(
            chain.IndexOf("ResolvingHttpDelegatingHandler") > chain.IndexOf(nameof(Microsoft.Extensions.Http.Resilience.ResilienceHandler)),
            $"The resolver must sit inside this package's pipeline: {string.Join(" > ", chain)}");
    }

    /// <summary>
    /// Damage the package cannot prevent: ServiceDefaults that keep service discovery but drop the standard
    /// handler leave nothing for this package to replace, so the resolver stays outermost, where it was put.
    /// </summary>
    /// <remarks>
    /// A defaults handler and a client's own cannot be told apart through public API, so the rule stays
    /// positional. All three attempts reach the one endpoint resolved before the pipeline ran. The documentation
    /// says to keep <c>AddStandardResilienceHandler</c> in ServiceDefaults for this reason.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ServiceDiscoveryWithoutADefaultsHandler_StaysOutermost_AndPinsRetriesToOneEndpoint()
    {
        (ServiceCollection services, List<string> reached) = ServiceDiscoveryClient(
            hedged: false,
            defaults => defaults.AddServiceDiscovery(),
            defaultsFirst: true);

        await using ServiceProvider provider = services.BuildServiceProvider();
        (await Create(provider, "catalog").GetAsync("http://catalog/x", TestContext.Current.CancellationToken))
            .Dispose();

        Assert.Equal(3, reached.Count);
        Assert.Single(reached.Distinct(StringComparer.Ordinal));
    }

    /// <summary>
    /// A client named <c>catalog</c> whose logical authority service discovery resolves, from configuration, to
    /// two endpoints. Records the authority each attempt reached.
    /// </summary>
    private static (ServiceCollection Services, List<string> Reached) ServiceDiscoveryClient(
        bool hedged,
        Action<IHttpClientBuilder> addDefaults,
        bool defaultsFirst)
    {
        Dictionary<string, string?> values = Settings.Enabled()
            .Set("PipelineSelection:Authorities:0", "http://catalog")
            .Set("Hedging:Delay", "00:00:10")
            .Build();
        values["services:catalog:http:0"] = "http://10.0.0.1:8080";
        values["services:catalog:http:1"] = "http://10.0.0.2:8080";
        IConfigurationRoot configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddServiceDiscovery();
        services.AddHttpResilience(configuration);

        var reached = new List<string>();
        var origin = new RecordingHandler((request, _, _) =>
        {
            lock (reached)
            {
                reached.Add(request.RequestUri!.GetLeftPart(UriPartial.Authority));
            }

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError));
        });

        if (defaultsFirst)
        {
            services.ConfigureHttpClientDefaults(addDefaults);
        }

        IHttpClientBuilder builder = services.AddHttpClient("catalog");
        _ = hedged ? builder.AddHttpResilienceHedgingHandler() : builder.AddHttpResilienceHandler();
        builder.ConfigurePrimaryHttpMessageHandler(() => origin);

        if (!defaultsFirst)
        {
            services.ConfigureHttpClientDefaults(addDefaults);
        }

        return (services, reached);
    }

    /// <summary>The client's delegating handlers by type name, outermost first.</summary>
    private static List<string> HandlerChain(ServiceProvider provider, string name)
    {
        var chain = new List<string>();
        HttpMessageHandler handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(name);
        for (HttpMessageHandler? current = handler; current is DelegatingHandler delegating; current = delegating.InnerHandler)
        {
            chain.Add(current.GetType().Name);
        }

        return chain;
    }

    /// <summary>
    /// The kill switch must not hand a client the defaults' pipeline: the platform's standard handler (.NET
    /// Aspire's) retries every method, so a failing POST would reach the origin four times. A disabled client
    /// loses the defaults' resilience handlers, says so once (event 19), and sends each request once.
    /// </summary>
    /// <remarks>
    /// Fails if the defaults' handler is kept (four deliveries each), for a standard or hedged client switched
    /// off by its own section or by the root kill switch.
    /// </remarks>
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(false, "Enabled")]
    [InlineData(false, "Clients:test:Enabled")]
    [InlineData(true, "Enabled")]
    public async Task ADisabledClient_LosesTheDefaultsHandler_SoAPostIsSentOnce(bool hedged, string switchKey)
    {
        var services = new ServiceCollection();
        services.ConfigureHttpClientDefaults(defaults => defaults
            .AddStandardResilienceHandler(options => options.Retry.Delay = TimeSpan.Zero));
        var logs = new ListLoggerProvider();
        services.AddLogging(logging => logging.AddProvider(logs));
        Settings settings = Settings.Enabled().Set(switchKey, "false");
        if (hedged)
        {
            settings.Set("PipelineSelection:Authorities:0", "http://origin.test");
        }

        services.AddHttpResilience(Configuration(settings));

        var origin = new RecordingHandler();
        IHttpClientBuilder builder = services.AddHttpClient("test");
        _ = hedged ? builder.AddHttpResilienceHedgingHandler() : builder.AddHttpResilienceHandler();
        builder.ConfigurePrimaryHttpMessageHandler(() => origin);

        await using ServiceProvider provider = services.BuildServiceProvider();
        using HttpClient client = Create(provider);
        using (var content = new StringContent("{}"))
        {
            (await client.PostAsync("http://origin.test/x", content, TestContext.Current.CancellationToken)).Dispose();
        }

        (await client.GetAsync("http://origin.test/x", TestContext.Current.CancellationToken)).Dispose();

        Assert.Equal(2, origin.Count);
        Assert.Single(logs.WithEvent(HttpResilience.NET.Internal.HttpResilienceLogging.ConfigurationCategory, 19));
    }

    /// <summary>
    /// A disabled client keeps the handlers registered for it in code, a resilience handler included: only what
    /// <c>ConfigureHttpClientDefaults</c> added is removed.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ADisabledClient_KeepsItsOwnResilienceHandler()
    {
        var services = new ServiceCollection();
        services.ConfigureHttpClientDefaults(defaults => defaults
            .AddStandardResilienceHandler(options => options.Retry.Delay = TimeSpan.Zero));
        services.AddHttpResilience(Configuration(Settings.Enabled().Set("Enabled", "false")));

        var origin = new RecordingHandler();
        IHttpClientBuilder builder = services.AddHttpClient("test");
        builder.AddResilienceHandler("own", pipeline => pipeline.AddRetry(new HttpRetryStrategyOptions
        {
            MaxRetryAttempts = 1,
            Delay = TimeSpan.Zero
        }));
        builder.AddHttpResilienceHandler().ConfigurePrimaryHttpMessageHandler(() => origin);

        await using ServiceProvider provider = services.BuildServiceProvider();
        (await Create(provider).GetAsync("http://origin.test/x", TestContext.Current.CancellationToken)).Dispose();

        // The client's own retry (one attempt, one retry); without the defaults' pipeline around it (4 × 2).
        Assert.Equal(2, origin.Count);
    }

    /// <summary>
    /// A resilience handler the consumer adds to <i>this client</i> before this package's conflicts with it, so
    /// client creation fails naming the fix, as a conflicting <c>HttpClient.Timeout</c> does. Deleting code the
    /// consumer wrote, with an Information entry, is what this replaced.
    /// </summary>
    /// <remarks>
    /// Told apart from a <c>ConfigureHttpClientDefaults</c> handler, which is replaced quietly, by a marker this
    /// package places before the client's first earlier <c>Configure</c>. Fails if the consumer's handler is
    /// replaced instead (the client is created) or if the defaults' handler is refused too.
    /// </remarks>
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AConsumersOwnResilienceHandler_AddedBefore_FailsClientCreation(bool standard, bool withAspireDefaults)
    {
        var services = new ServiceCollection();
        if (withAspireDefaults)
        {
            services.ConfigureHttpClientDefaults(defaults => defaults.AddStandardResilienceHandler());
        }

        services.AddHttpResilience(Configuration(Settings.Enabled()));

        IHttpClientBuilder builder = services.AddHttpClient("test");
        if (standard)
        {
            builder.AddStandardResilienceHandler();
        }
        else
        {
            builder.AddResilienceHandler("mine", pipeline => pipeline.AddTimeout(TimeSpan.FromSeconds(9)));
        }

        builder.AddHttpResilienceHandler().ConfigurePrimaryHttpMessageHandler(() => new RecordingHandler());

        await using ServiceProvider provider = services.BuildServiceProvider();
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => Create(provider));

        Assert.Contains("'test'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("after AddHttpResilienceHandler", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same for a handler added through a registration whose client cannot be read without running it:
    /// <c>AddOptions&lt;HttpClientFactoryOptions&gt;(name).Configure&lt;TDep&gt;</c> registers a factory, not a
    /// <c>ConfigureNamedOptions</c> instance.
    /// </summary>
    /// <remarks>
    /// Fails if only <c>ConfigureNamedOptions</c> instances place the marker: the consumer's handler is then taken
    /// for a defaults one and deleted, and the client is created.
    /// </remarks>
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AConsumersResilienceHandler_FromAFactoryRegistration_AddedBefore_FailsClientCreation(
        bool withAspireDefaults)
    {
        var services = new ServiceCollection();
        if (withAspireDefaults)
        {
            services.ConfigureHttpClientDefaults(defaults => defaults.AddStandardResilienceHandler());
        }

        services.AddHttpResilience(Configuration(Settings.Enabled()));
        services.AddOptions<HttpClientFactoryOptions>("test").Configure<IServiceProvider>((options, _) =>
            options.HttpMessageHandlerBuilderActions.Add(handlerBuilder => handlerBuilder.AdditionalHandlers.Add(
                new ResilienceHandler(new ResiliencePipelineBuilder<HttpResponseMessage>()
                    .AddTimeout(TimeSpan.FromSeconds(9))
                    .Build()))));

        services.AddHttpClient("test").AddHttpResilienceHandler()
            .ConfigurePrimaryHttpMessageHandler(() => new RecordingHandler());

        await using ServiceProvider provider = services.BuildServiceProvider();
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => Create(provider));

        Assert.Contains("'test'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("after AddHttpResilienceHandler", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A factory registration nobody wrote for this client, before or after the platform's own, must not make the
    /// defaults' handler look like the client's: it is still replaced and the client is created.
    /// </summary>
    /// <remarks>
    /// Fails if a factory registration ahead of the platform's moves the marker before the defaults (every client
    /// with .NET Aspire defaults would then fail creation), or if the defaults' handler survives (twelve calls).
    /// </remarks>
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnUnrelatedFactoryRegistration_KeepsTheDefaultsHandlerReplaceable(bool registeredFirst)
    {
        var services = new ServiceCollection();
        void AddUnrelated() => services.AddTransient<IConfigureOptions<HttpClientFactoryOptions>>(
            _ => new ConfigureNamedOptions<HttpClientFactoryOptions>("unrelated", _ => { }));

        if (registeredFirst)
        {
            AddUnrelated();
        }

        services.ConfigureHttpClientDefaults(defaults =>
            defaults.AddStandardResilienceHandler(options => options.Retry.Delay = TimeSpan.Zero));
        services.AddHttpResilience(Configuration(Settings.Enabled()));
        services.AddHttpClient("other");

        if (!registeredFirst)
        {
            AddUnrelated();
        }

        var origin = new RecordingHandler();
        services.AddHttpClient("test").AddHttpResilienceHandler().ConfigurePrimaryHttpMessageHandler(() => origin);

        await using ServiceProvider provider = services.BuildServiceProvider();
        (await Create(provider).GetAsync("http://origin.test/x", TestContext.Current.CancellationToken)).Dispose();

        Assert.Equal(3, origin.Count);
    }

    /// <summary>
    /// Code the consumer wrote for the client before this package's call that is not a resilience handler (a
    /// primary handler, a header) keeps the defaults' handler replaceable: only resilience handlers conflict.
    /// </summary>
    /// <remarks>
    /// Both orders: the defaults' actions run before every named client's whenever they are registered, which is
    /// what lets the marker separate the two. Fails if the defaults' handler is refused as the consumer's.
    /// </remarks>
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OtherConfigurationAddedBefore_StillLetsTheDefaultsHandlerBeReplaced(bool defaultsFirst)
    {
        var services = new ServiceCollection();
        void AddDefaults() => services.ConfigureHttpClientDefaults(defaults => defaults
            .AddStandardResilienceHandler(options => options.Retry.Delay = TimeSpan.Zero));

        if (defaultsFirst)
        {
            AddDefaults();
        }

        services.AddHttpResilience(Configuration(Settings.Enabled()));

        var origin = new RecordingHandler();
        services.AddHttpClient("test")
            .ConfigurePrimaryHttpMessageHandler(() => origin)
            .ConfigureHttpClient(client => client.DefaultRequestHeaders.Add("X-Caller", "orders"))
            .AddHttpResilienceHandler();

        if (!defaultsFirst)
        {
            AddDefaults();
        }

        await using ServiceProvider provider = services.BuildServiceProvider();
        (await Create(provider).GetAsync("http://origin.test/x", TestContext.Current.CancellationToken)).Dispose();

        // This package's three attempts; the defaults' four would make twelve if they nested.
        Assert.Equal(3, origin.Count);
    }

    /// <summary>
    /// A resilience handler added after this package's is the supported way to add a strategy, and is kept.
    /// </summary>
    /// <remarks>Fails if the replacement reaches past this package's handlers.</remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task AHandlerAddedAfter_IsKept()
    {
        var services = new ServiceCollection();
        services.AddHttpResilience(Configuration(Settings.Enabled()));

        var origin = new RecordingHandler();
        IHttpClientBuilder builder = services.AddHttpClient("test");
        builder.AddHttpResilienceHandler();
        builder.AddResilienceHandler("after", pipeline => pipeline.AddTimeout(TimeSpan.FromSeconds(9)));
        builder.ConfigurePrimaryHttpMessageHandler(() => origin);

        await using ServiceProvider provider = services.BuildServiceProvider();
        HttpMessageHandler handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler("test");
        (await Create(provider).GetAsync("http://origin.test/x", TestContext.Current.CancellationToken)).Dispose();

        Assert.Equal(3, origin.Count);

        int resilienceHandlers = 0;
        for (HttpMessageHandler? current = handler; current is DelegatingHandler delegating; current = delegating.InnerHandler)
        {
            resilienceHandlers += current is Microsoft.Extensions.Http.Resilience.ResilienceHandler ? 1 : 0;
        }

        // This package's standard handler and the consumer's "after" handler.
        Assert.Equal(2, resilienceHandlers);
    }

    /// <summary>
    /// Under <c>Connection:Enabled</c> the package turns handler rotation off (the pool's
    /// <c>PooledConnectionLifetime</c> bounds connection age instead), so a finite <c>SetHandlerLifetime</c> on the
    /// client is a conflicting value and fails client creation, as a conflicting <c>HttpClient.Timeout</c> does,
    /// whichever is registered first.
    /// </summary>
    /// <remarks>Fails if the stated lifetime is silently overwritten (the client is created).</remarks>
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AFiniteHandlerLifetime_UnderConnectionEnabled_FailsClientCreation(bool lifetimeFirst)
    {
        var services = new ServiceCollection();
        services.AddHttpResilience(Configuration(Settings.Enabled().Set("Connection:Enabled", "true")));

        IHttpClientBuilder builder = services.AddHttpClient("test");
        if (lifetimeFirst)
        {
            builder.SetHandlerLifetime(TimeSpan.FromMinutes(5));
        }

        builder.AddHttpResilienceHandler();
        if (!lifetimeFirst)
        {
            builder.SetHandlerLifetime(TimeSpan.FromMinutes(5));
        }

        await using ServiceProvider provider = services.BuildServiceProvider();
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => Create(provider));

        Assert.Contains("'test'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("SetHandlerLifetime", exception.Message, StringComparison.Ordinal);
        Assert.Contains("00:05:00", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The lifetime is the consumer's whenever it does not conflict: an infinite one agrees with
    /// <c>Connection:Enabled</c>, and without it the package leaves rotation alone.
    /// </summary>
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(true, -1, -1)]
    [InlineData(false, 300, 300)]
    [InlineData(false, -1, -1)]
    public async Task ANonConflictingHandlerLifetime_IsKept(bool connectionEnabled, int statedSeconds, int expectedSeconds)
    {
        static TimeSpan Seconds(int value) => value < 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(value);

        var services = new ServiceCollection();
        services.AddHttpResilience(Configuration(
            Settings.Enabled().Set("Connection:Enabled", connectionEnabled ? "true" : "false")));
        services.AddHttpClient("test").SetHandlerLifetime(Seconds(statedSeconds)).AddHttpResilienceHandler();

        await using ServiceProvider provider = services.BuildServiceProvider();
        Create(provider).Dispose();

        Assert.Equal(
            Seconds(expectedSeconds),
            provider.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>().Get("test").HandlerLifetime);
    }

    /// <summary>
    /// The platform's own <c>RemoveAllResilienceHandlers()</c> before this package's handler, which earlier
    /// versions recommended, is no longer needed but still works: nothing is left for this package to replace.
    /// </summary>
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoveAllResilienceHandlers_BeforeThisPackage_UndoesTheDefaultsNesting(bool hedged)
    {
        var logs = new ListLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(logs));
        services.ConfigureHttpClientDefaults(defaults => defaults
            .AddStandardResilienceHandler(options => options.Retry.Delay = TimeSpan.Zero));
        services.AddHttpResilience(Configuration(Settings.Hedged().Set("Hedging:Delay", "00:00:10")));

        var origin = new RecordingHandler();

        // Experimental in the platform package (EXTEXP0001); the documentation says so.
#pragma warning disable EXTEXP0001
        IHttpClientBuilder builder = services.AddHttpClient("test").RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
        _ = hedged ? builder.AddHttpResilienceHedgingHandler() : builder.AddHttpResilienceHandler();
        builder.ConfigurePrimaryHttpMessageHandler(() => origin);

        await using ServiceProvider provider = services.BuildServiceProvider();
        HttpClient client = Create(provider);
        (await client.GetAsync("http://origin.test/x", TestContext.Current.CancellationToken)).Dispose();

        // Standard: three attempts. Hedged: the primary and one hedged attempt after the 500.
        Assert.Equal(hedged ? 2 : 3, origin.Count);
        Assert.Equal(TimeSpan.FromSeconds(30) + TimeSpan.FromSeconds(30), client.Timeout);
        Assert.DoesNotContain(logs.Records, r => r.Contains("resilience handlers", StringComparison.Ordinal));
    }

    /// <summary>
    /// The one symptom of that nesting the package <i>can</i> prevent: the finite client timeout survives it.
    /// </summary>
    /// <remarks>
    /// The platform handler sets <see cref="HttpClient.Timeout"/> to infinite; this package restores a finite
    /// bound for the response body. Fails if the client timeout is applied with <c>ConfigureHttpClient</c>,
    /// which is last-wins and loses to any later registration.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public void ASecondPlatformHandler_DoesNotTakeAwayTheFiniteClientTimeout()
    {
        (ServiceCollection services, IHttpClientBuilder builder, _) = Client(Settings.Enabled());
        builder.AddStandardResilienceHandler();

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Equal(TimeSpan.FromSeconds(30) + TimeSpan.FromSeconds(30), Create(provider).Timeout);
    }

    /// <summary>
    /// The escape hatch the documentation actually recommends still composes, and must not trip the guard.
    /// </summary>
    /// <remarks>
    /// <c>AddResilienceHandler</c> adds a <c>ResilienceHandler</c> just as a second standard handler does,
    /// but neither nests a retry loop nor resets <c>HttpClient.Timeout</c>: the origin still sees three
    /// calls. That indistinguishability is why the guard reports instead of failing.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task TheSanctionedEscapeHatch_StillComposes()
    {
        (ServiceCollection services, IHttpClientBuilder builder, RecordingHandler origin) =
            Client(Settings.Enabled());
        builder.AddResilienceHandler("legacy-quirk", pipeline => pipeline.AddTimeout(TimeSpan.FromSeconds(9)));

        await using ServiceProvider provider = services.BuildServiceProvider();
        HttpClient client = Create(provider);

        (await client.GetAsync("http://origin.test/x", TestContext.Current.CancellationToken)).Dispose();

        Assert.Equal(3, origin.Count);
        Assert.Equal(TimeSpan.FromSeconds(30) + TimeSpan.FromSeconds(30), client.Timeout);
    }

    /// <summary>
    /// A client with resilience switched off adds no pipeline, so a consumer may add the platform's handler
    /// itself -- which is the supported way to migrate a client onto this package one step at a time.
    /// </summary>
    /// <remarks>
    /// Fails if the guard counts handlers for clients with no pipeline from this package, which would make
    /// <c>Enabled: false</c> break a client that has its own resilience.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public void ADisabledClient_MayStillAddThePlatformHandlerItself()
    {
        (ServiceCollection services, IHttpClientBuilder builder, _) = Client(Settings.Empty());
        builder.AddStandardResilienceHandler();

        using ServiceProvider provider = services.BuildServiceProvider();

        Create(provider).Dispose();
    }

    /// <summary>
    /// A consumer setting <c>HttpClient.Timeout</c> in code cannot silently truncate the pipeline below
    /// <c>Timeout:Total</c>.
    /// </summary>
    /// <remarks>
    /// The same value as <c>Timeout:Client</c> would be refused by <c>ValidateTimeouts</c>. Fails if the
    /// client timeout is applied with <c>ConfigureHttpClient</c> rather than from a post-configure that runs
    /// after every registration.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public void AConsumersClientTimeout_FailsRatherThanTruncatingThePipeline()
    {
        (ServiceCollection services, IHttpClientBuilder builder, _) = Client(Settings.Enabled());
        builder.ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(2));

        using ServiceProvider provider = services.BuildServiceProvider();

        InvalidOperationException failure =
            Assert.Throws<InvalidOperationException>(() => Create(provider));

        Assert.Contains("00:00:02", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Timeout:Client", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A typed client assigning <see cref="HttpClient.Timeout"/> in its <b>constructor</b> truncates the
    /// pipeline, and the guard above cannot see it.
    /// </summary>
    /// <remarks>
    /// Every phase this package can reach runs while <see cref="IHttpClientFactory"/> builds the client; a
    /// typed client's constructor runs afterwards, so nothing can observe it. Asserted at the origin: a
    /// 30-second pipeline truncated to one second, one call, and a bare <see cref="TaskCanceledException"/>.
    /// <para>
    /// Documented in README.md, docs/ARCHITECTURE.md and docs/PRODUCTION-CHECKLIST.md. If this ever becomes
    /// detectable and the test fails, update those documents.
    /// </para>
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ATypedClientsConstructorTimeout_TruncatesThePipeline_AndNoGuardCanSeeIt()
    {
        var services = new ServiceCollection();
        services.AddHttpResilience(Configuration(Settings.Enabled()));

        var origin = new RecordingHandler(async (_, _, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        });

        services.AddHttpClient<TruncatingTypedClient>("test")
            .AddHttpResilienceHandler()
            .ConfigurePrimaryHttpMessageHandler(() => origin);

        using ServiceProvider provider = services.BuildServiceProvider();

        // No failure at client creation: the assignment has not happened yet.
        TruncatingTypedClient typed = provider.GetRequiredService<TruncatingTypedClient>();

        await Assert.ThrowsAsync<TaskCanceledException>(() => typed.GetAsync());

        // Timeout:Total is 30 seconds and Timeout:Client resolves to 00:01:30. Neither bounded this.
        Assert.Equal(TimeSpan.FromSeconds(1), typed.Timeout);
        Assert.Equal(1, origin.Count);
    }

    /// <summary>
    /// A consumer writing the framework's own default of 100 seconds is refused like any other conflicting
    /// value, because "nothing assigned one" is established before the consumer runs rather than inferred
    /// from the value afterwards.
    /// </summary>
    /// <remarks>
    /// <c>ApplyClientTimeout</c> normalises the timeout to infinite with an action at index <b>0</b> of
    /// <c>HttpClientActions</c>, so any finite value at the last action was assigned by the consumer.
    /// Fails if that action is removed or 100 seconds is special-cased.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public void AConsumersHundredSecondTimeout_IsRefusedLikeAnyOtherConflictingValue()
    {
        (ServiceCollection services, IHttpClientBuilder builder, _) = Client(Settings.Enabled());

        // The framework default for HttpClient.Timeout, written out deliberately.
        builder.ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(100));

        using ServiceProvider provider = services.BuildServiceProvider();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => Create(provider));

        Assert.Contains("Timeout:Client", exception.Message, StringComparison.Ordinal);
        Assert.Contains("00:01:40", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A client nothing assigned a timeout to still gets the resolved one.
    /// </summary>
    /// <remarks>
    /// The other direction: infinite at the last action means nobody stated a timeout. Fails if infinite is
    /// treated as a consumer statement.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public void AClientNothingAssignedATimeoutTo_StillGetsTheResolvedOne()
    {
        (ServiceCollection services, _, _) = Client(Settings.Enabled());

        using ServiceProvider provider = services.BuildServiceProvider();

        // Timeout:Total is 30s, so Timeout:Client resolves to 30s + the 30-second body allowance.
        Assert.Equal(TimeSpan.FromSeconds(60), Create(provider).Timeout);
    }

    /// <summary>
    /// A typed client that assigns <see cref="HttpClient.Timeout"/> in its constructor, which is the
    /// documented .NET idiom for configuring a typed client and the one shape no guard here reaches.
    /// </summary>
    private sealed class TruncatingTypedClient
    {
        private readonly HttpClient _client;

        public TruncatingTypedClient(HttpClient client)
        {
            client.Timeout = TimeSpan.FromSeconds(1);
            _client = client;
        }

        public TimeSpan Timeout => _client.Timeout;

        public Task<HttpResponseMessage> GetAsync() => _client.GetAsync("http://origin.test/x");
    }

    /// <summary>
    /// The same value stated the supported way is accepted, and reaches the client.
    /// </summary>
    /// <remarks>
    /// The guard redirects to a supported setting; it must not be an obstruction.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public void TheSameBoundStatedAsTimeoutClient_ReachesTheClient()
    {
        (ServiceCollection services, _, _) =
            Client(Settings.Enabled().Set("Timeout:Client", "00:02:00"));

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Equal(TimeSpan.FromMinutes(2), Create(provider).Timeout);
    }

    /// <summary>
    /// A consumer's <c>ConfigureHttpClient</c> that does not touch the timeout is left alone.
    /// </summary>
    /// <remarks>
    /// Fails if the guard rejects any <c>ConfigureHttpClient</c> rather than a conflicting timeout.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public void AConsumersOtherClientConfiguration_IsUntouched()
    {
        (ServiceCollection services, IHttpClientBuilder builder, _) = Client(Settings.Enabled());
        builder.ConfigureHttpClient(client =>
            client.DefaultRequestHeaders.Add("X-Trace-Source", "orders"));

        using ServiceProvider provider = services.BuildServiceProvider();
        HttpClient client = Create(provider);

        Assert.Equal("orders", Assert.Single(client.DefaultRequestHeaders.GetValues("X-Trace-Source")));
        Assert.Equal(TimeSpan.FromSeconds(30) + TimeSpan.FromSeconds(30), client.Timeout);
    }

    /// <summary>
    /// A client this package never registered is not examined at all.
    /// </summary>
    /// <remarks>
    /// Both guards run from container-wide extension points (a handler-builder filter and a post-configure
    /// on <c>HttpClientFactoryOptions</c>), so a misjudgement would affect every client. Fails if either
    /// acts on a client this package did not register.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public void AnUnrelatedClient_IsNotExaminedByEitherGuard()
    {
        var services = new ServiceCollection();
        services.AddHttpResilience(Configuration(Settings.Enabled()));
        services.AddHttpClient("test").AddHttpResilienceHandler();

        IHttpClientBuilder stranger = services.AddHttpClient("stranger");
        stranger.AddStandardResilienceHandler();
        stranger.ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(2));

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Equal(TimeSpan.FromSeconds(2), Create(provider, "stranger").Timeout);
    }

    /// <summary>
    /// Root <c>Connection</c> settings reach only clients registered with this package, never one the
    /// application did not opt in, including the unnamed default client behind <c>CreateClient()</c> and an
    /// injected <see cref="HttpClient"/>.
    /// </summary>
    /// <remarks>
    /// The connection filter is container-wide, and the unnamed client shares its name with the root options,
    /// so reading options by client name alone handed it the root values. Fails if the filter acts on a client
    /// the registration does not list.
    /// </remarks>
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData("")]
    [InlineData("stranger")]
    public void RootConnectionSettings_DoNotReachAClientThatNeverOptedIn(string name)
    {
        var services = new ServiceCollection();
        services.AddHttpResilience(Configuration(Settings.Enabled()
            .Set("Connection:Enabled", "true")
            .Set("Connection:AllowAutoRedirect", "false")));
        services.AddHttpClient("test").AddHttpResilienceHandler();

        var sockets = new SocketsHttpHandler { AllowAutoRedirect = true, ConnectTimeout = TimeSpan.FromSeconds(42) };
        services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => sockets);

        using ServiceProvider provider = services.BuildServiceProvider();
        Create(provider, name).Dispose();

        Assert.True(sockets.AllowAutoRedirect, "root Connection:AllowAutoRedirect reached a client that never opted in.");
        Assert.Equal(TimeSpan.FromSeconds(42), sockets.ConnectTimeout);
        Assert.Equal(Timeout.InfiniteTimeSpan, sockets.PooledConnectionLifetime);
    }

    /// <summary>
    /// The same client with a customised <see cref="HttpClientHandler"/> is still created: the refusal that
    /// protects a registered client's settings does not apply to a client this package does not configure.
    /// </summary>
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData("")]
    [InlineData("stranger")]
    public void RootConnectionSettings_DoNotFailAClientThatNeverOptedIn(string name)
    {
        var services = new ServiceCollection();
        services.AddHttpResilience(Configuration(Settings.Enabled().Set("Connection:Enabled", "true")));
        services.AddHttpClient("test").AddHttpResilienceHandler();

        var handler = new HttpClientHandler { UseProxy = false };
        services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => handler);

        using ServiceProvider provider = services.BuildServiceProvider();
        Create(provider, name).Dispose();

        Assert.False(handler.UseProxy);
    }

    /// <summary>
    /// Every pipeline shape accounts for exactly the resilience handlers it adds, so the notice fires on a
    /// nested pipeline and stays silent on a correct one.
    /// </summary>
    /// <remarks>
    /// The registration tally duplicates the conditions that decide which handlers are added, so it can
    /// drift. The platform's hedging handler contributes <b>two</b> handlers (routing and hedging). A tally
    /// too low fails the silent half; too high fails the reporting half.
    /// </remarks>
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public void EveryPipelineShape_TalliesItsOwnHandlers(bool hedged, bool rateLimiter, bool concurrencyLimiter)
    {
        Settings Shape()
        {
            Settings settings = hedged ? Settings.Hedged() : Settings.Enabled();
            if (rateLimiter)
            {
                settings.Set("RateLimiter:Enabled", "true").Set("RateLimiter:PermitLimit", "10");
            }

            if (concurrencyLimiter)
            {
                settings.Set("ConcurrencyLimiter:Enabled", "true").Set("ConcurrencyLimiter:Limit", "10");
            }

            return settings;
        }

        // Too low, and a correctly configured client is reported as nested.
        var clean = new ListLoggerProvider();
        (ServiceCollection valid, _, _) = Client(Shape(), hedged, logs: clean);
        using (ServiceProvider provider = valid.BuildServiceProvider())
        {
            Create(provider).Dispose();
        }

        Assert.DoesNotContain(clean.Records, r => r.Contains("resilience handlers", StringComparison.Ordinal));

        // Too high, and a second pipeline goes unreported.
        var nestedLogs = new ListLoggerProvider();
        (ServiceCollection nested, IHttpClientBuilder builder, _) = Client(Shape(), hedged, logs: nestedLogs);
        // A second platform hedging handler fails client creation (PlatformOptionsOwnershipTests), so the
        // hedged shape is tallied against the escape hatch, which the count cannot tell apart from it.
        if (hedged)
        {
            builder.AddResilienceHandler("extra", pipeline => pipeline.AddTimeout(TimeSpan.FromSeconds(9)));
        }
        else
        {
            builder.AddStandardResilienceHandler();
        }

        using (ServiceProvider provider = nested.BuildServiceProvider())
        {
            Create(provider).Dispose();
        }

        Assert.Single(nestedLogs.Records, r => r.Contains("resilience handlers", StringComparison.Ordinal));
    }

    /// <summary>
    /// The notice is emitted once per client, not once per handler construction.
    /// </summary>
    /// <remarks>
    /// <see cref="IHttpClientFactory"/> rebuilds the handler chain on every rotation (every two minutes by
    /// default) and reruns every filter. The filter is driven directly rather than by waiting for a rotation.
    /// Fails if the deduplicating set is removed.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public void TheNestingNotice_IsEmittedOncePerClient_NotOncePerHandlerConstruction()
    {
        var logs = new ListLoggerProvider();
        (ServiceCollection services, IHttpClientBuilder builder, _) = Client(Settings.Enabled(), logs: logs);
        builder.AddStandardResilienceHandler();

        using ServiceProvider provider = services.BuildServiceProvider();

        // Two constructions of the same client's chain, which is what a handler rotation is.
        Create(provider).Dispose();
        RebuildHandlerChain(provider, "test");

        Assert.Single(logs.Records, r => r.Contains("resilience handlers", StringComparison.Ordinal));
    }

    /// <summary>
    /// A consumer hardened their own <see cref="SocketsHttpHandler"/> against redirects and then switched
    /// <c>Connection:Enabled</c> on for the pool settings.
    /// </summary>
    /// <remarks>
    /// An unstated <c>AllowAutoRedirect</c> resolves to <see langword="true"/> for a standard client, and must
    /// not overwrite a consumer's <see langword="false"/>. The runtime re-sends custom credential headers such
    /// as <c>X-Api-Key</c> across redirects. Fails if the <c>OptionsComposition.IsRedirectStated</c> check in
    /// <c>SocketsHttpHandlerFactory.ApplyRedirectBound</c> is dropped.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public void AConsumersOwnRedirectBound_SurvivesConnectionTuning()
    {
        var services = new ServiceCollection();
        services.AddHttpResilience(Configuration(Settings.Enabled().Set("Connection:Enabled", "true")));

        var hardened = new SocketsHttpHandler { AllowAutoRedirect = false, MaxConnectionsPerServer = 7 };
        services.AddHttpClient("test")
            .ConfigurePrimaryHttpMessageHandler(() => hardened)
            .AddHttpResilienceHandler();

        using ServiceProvider provider = services.BuildServiceProvider();
        Create(provider).Dispose();

        Assert.False(
            hardened.AllowAutoRedirect,
            "a redirect bound the consumer set on its own handler was reversed by Connection:Enabled.");

        // The properties the schema always states are still applied; only the unstated one is left alone.
        Assert.Equal(TimeSpan.FromSeconds(3), hardened.ConnectTimeout);
        Assert.Equal(TimeSpan.FromMinutes(2), hardened.PooledConnectionLifetime);
        Assert.Equal(7, hardened.MaxConnectionsPerServer);
    }

    /// <summary>
    /// The other direction: a schema that <i>does</i> state the value still wins over the consumer's handler.
    /// </summary>
    /// <remarks>
    /// Guards against "never write AllowAutoRedirect", which would let a hedged client follow redirects
    /// around its allow-list.
    /// </remarks>
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(false, "true", true)]
    [InlineData(false, "false", false)]
    [InlineData(true, null, false)]
    public void AStatedRedirectBound_StillReachesAConsumersHandler(
        bool hedged,
        string? stated,
        bool expected)
    {
        Settings settings = (hedged ? Settings.Hedged() : Settings.Enabled()).Set("Connection:Enabled", "true");
        if (stated is not null)
        {
            settings = settings.Set("Connection:AllowAutoRedirect", stated);
        }

        var services = new ServiceCollection();
        services.AddHttpResilience(Configuration(settings));

        // Constructed with the opposite of what is expected, so a no-op would fail rather than pass.
        var consumers = new SocketsHttpHandler { AllowAutoRedirect = !expected };
        IHttpClientBuilder builder = services.AddHttpClient("test")
            .ConfigurePrimaryHttpMessageHandler(() => consumers);
        _ = hedged ? builder.AddHttpResilienceHedgingHandler() : builder.AddHttpResilienceHandler();

        using ServiceProvider provider = services.BuildServiceProvider();
        Create(provider).Dispose();

        Assert.Equal(expected, consumers.AllowAutoRedirect);
    }

    /// <summary>
    /// A hedged client whose primary handler has no redirect switch at all: the bound cannot be applied, and a
    /// Warning says so.
    /// </summary>
    /// <remarks>
    /// Reported rather than thrown, because this is usually a test stub that resolves no redirects. A handler
    /// wrapping its own <see cref="SocketsHttpHandler"/> looks the same here, so the Warning names the client.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public void AHedgedClientWhoseHandlerHasNoRedirectSwitch_SaysTheBoundWasNotApplied()
    {
        var logs = new ListLoggerProvider();

        // The helper's primary handler is a RecordingHandler: neither SocketsHttpHandler nor HttpClientHandler.
        (ServiceCollection services, _, _) = Client(Settings.Hedged(), hedged: true, logs: logs);

        using ServiceProvider provider = services.BuildServiceProvider();
        Create(provider).Dispose();
        RebuildHandlerChain(provider, "test");

        string record = Assert.Single(
            logs.Records, r => r.Contains("could not disable redirects", StringComparison.Ordinal));

        Assert.StartsWith("[Warning]", record, StringComparison.Ordinal);
        Assert.Contains("RecordingHandler", record, StringComparison.Ordinal);
        Assert.Contains("'test'", record, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same client with a handler that <i>does</i> carry the switch says nothing, and the bound holds.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public void AHedgedClientWithASocketsHandler_AppliesTheBoundAndSaysNothing()
    {
        var logs = new ListLoggerProvider();
        var services = new ServiceCollection();
        services.AddHttpResilience(Configuration(Settings.Hedged()));
        services.AddLogging(logging => logging.AddProvider(logs));

        var handler = new SocketsHttpHandler { AllowAutoRedirect = true };
        services.AddHttpClient("test")
            .ConfigurePrimaryHttpMessageHandler(() => handler)
            .AddHttpResilienceHedgingHandler();

        using ServiceProvider provider = services.BuildServiceProvider();
        Create(provider).Dispose();

        Assert.False(handler.AllowAutoRedirect);
        Assert.DoesNotContain(
            logs.Records, r => r.Contains("could not disable redirects", StringComparison.Ordinal));
    }

    /// <summary>
    /// Runs every handler-builder filter over a fresh builder for one client, the way
    /// <see cref="IHttpClientFactory"/> does when a handler expires.
    /// </summary>
    private static void RebuildHandlerChain(IServiceProvider provider, string clientName)
    {
        HttpClientFactoryOptions options =
            provider.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>().Get(clientName);

        // The innermost action adds the handlers, as DefaultHttpClientFactory composes it, so the filters see
        // the chain a real rotation produces.
        Action<HttpMessageHandlerBuilder> configure = handlerBuilder =>
        {
            foreach (Action<HttpMessageHandlerBuilder> action in options.HttpMessageHandlerBuilderActions)
            {
                action(handlerBuilder);
            }
        };

        IHttpMessageHandlerBuilderFilter[] filters =
            [.. provider.GetServices<IHttpMessageHandlerBuilderFilter>()];

        for (int i = filters.Length - 1; i >= 0; i--)
        {
            configure = filters[i].Configure(configure);
        }

        configure(new ProbeHandlerBuilder(provider) { Name = clientName });
    }

    private sealed class ProbeHandlerBuilder : HttpMessageHandlerBuilder
    {
        public override string? Name { get; set; }

        public override HttpMessageHandler PrimaryHandler { get; set; } = new SocketsHttpHandler();

        public override IList<DelegatingHandler> AdditionalHandlers { get; } = [];

        // The platform's own builder actions resolve services through this, so a builder without it throws
        // before any filter runs.
        public override IServiceProvider Services { get; }

        public ProbeHandlerBuilder(IServiceProvider services) => Services = services;

        public override HttpMessageHandler Build() => PrimaryHandler;
    }

    /// <summary>
    /// A consumer's <c>PostConfigure</c> reaches the rate limiter the client actually runs on.
    /// </summary>
    /// <remarks>
    /// Every limiter is built from live options inside its keyed factory. Fails if the limiter is built from
    /// a registration-time snapshot, which <c>NamedPipelineOptionsValidator</c> would not detect.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task PostConfigure_ReachesTheRateLimiterTheClientRunsOn()
    {
        (ServiceCollection services, _, RecordingHandler origin) = Client(
            Settings.Enabled()
                .Set("Retry:Enabled", "false")
                .Set("RateLimiter:Enabled", "true")
                .Set("RateLimiter:PermitLimit", "2")
                .Set("RateLimiter:Window", "01:00:00"));

        services.PostConfigure<HttpResilienceOptions>("test", options => options.RateLimiter.PermitLimit = 5);

        await using ServiceProvider provider = services.BuildServiceProvider();
        HttpClient client = Create(provider);

        int admitted = 0;
        for (int i = 0; i < 6; i++)
        {
            try
            {
                (await client.GetAsync("http://origin.test/x", TestContext.Current.CancellationToken)).Dispose();
                admitted++;
            }
            catch (Polly.RateLimiting.RateLimiterRejectedException)
            {
                break;
            }
        }

        // The post-configured budget, not the one in the configuration file.
        Assert.Equal(5, admitted);
        Assert.Equal(5, origin.Count);
    }

    /// <summary>
    /// <c>ConfigureHttpClientDefaults</c> passes a builder with no name, so there is no section to read.
    /// The failure must say so rather than surface as an <see cref="ArgumentNullException"/> from inside.
    /// </summary>
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(false)]
    [InlineData(true)]
    public void ConfigureHttpClientDefaults_FailsWithAMessageNamingTheAlternative(bool hedged)
    {
        var services = new ServiceCollection();
        services.AddHttpResilience(Configuration(Settings.Hedged()));

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            services.ConfigureHttpClientDefaults(builder =>
                _ = hedged ? builder.AddHttpResilienceHedgingHandler() : builder.AddHttpResilienceHandler()));

        Assert.Contains("ConfigureHttpClientDefaults", exception.Message, StringComparison.Ordinal);
        Assert.Contains("AddHttpClient", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Aspire", exception.Message, StringComparison.Ordinal);
        Assert.Contains("replaced", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("RemoveAllResilienceHandlers", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A consumer that clears the factory's <c>HttpClientActions</c> also removes the markers this package uses
    /// to find each handler group's client actions. Nothing of the package's is left to remove, so client
    /// creation proceeds, with the pipeline and the client timeout intact.
    /// </summary>
    /// <remarks>
    /// Fails if a missing pair of markers throws again: every <c>CreateClient</c> would fail with a message
    /// about markers the consumer has never heard of. Run with a handler group active and inactive.
    /// </remarks>
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AConsumerClearingHttpClientActions_KeepsThePipelineAndTheClientTimeout(bool concurrencyLimiter)
    {
        Settings settings = Settings.Enabled();
        if (concurrencyLimiter)
        {
            settings.Set("ConcurrencyLimiter:Enabled", "true").Set("ConcurrencyLimiter:Limit", "5");
        }

        (ServiceCollection services, _, RecordingHandler origin) = Client(settings);
        services.Configure<HttpClientFactoryOptions>("test", options => options.HttpClientActions.Clear());

        await using ServiceProvider provider = services.BuildServiceProvider();
        using HttpClient client = Create(provider);

        (await client.GetAsync("http://origin.test/x", TestContext.Current.CancellationToken)).Dispose();

        Assert.Equal(3, origin.Count);
        Assert.Equal(TimeSpan.FromSeconds(60), client.Timeout); // Timeout:Total 30 s + the 30 s allowance
    }

    /// <summary>
    /// The handler groups depend on <c>HttpMessageHandlerBuilderActions</c> running in registration order. A
    /// consumer that reorders that list breaks them, and client creation fails with a message naming the list
    /// and the cause, not an internal marker.
    /// </summary>
    /// <remarks>Fails if the message stops naming the list or what the consumer did to it.</remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public void AConsumerReorderingHandlerBuilderActions_FailsClientCreation_NamingTheCause()
    {
        (ServiceCollection services, _, _) = Client(Settings.Enabled());
        services.Configure<HttpClientFactoryOptions>("test", options =>
        {
            Action<HttpMessageHandlerBuilder>[] reversed = [.. options.HttpMessageHandlerBuilderActions];
            Array.Reverse(reversed);
            options.HttpMessageHandlerBuilderActions.Clear();
            foreach (Action<HttpMessageHandlerBuilder> action in reversed)
            {
                options.HttpMessageHandlerBuilderActions.Add(action);
            }
        });

        using ServiceProvider provider = services.BuildServiceProvider();
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => Create(provider));

        Assert.Contains("HttpMessageHandlerBuilderActions", exception.Message, StringComparison.Ordinal);
        Assert.Contains("removed or reordered", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A consumer that strips this package's handlers after registering it leaves the client with no resilience
    /// at all. That is warned (event 16), naming the client, and the damage is asserted: one origin call where
    /// the pipeline would make three.
    /// </summary>
    /// <remarks>Fails if the missing-handler check is removed.</remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task RemovingThisPackagesHandlers_AfterRegistration_IsWarned()
    {
        var logs = new ListLoggerProvider();
        (ServiceCollection services, IHttpClientBuilder builder, RecordingHandler origin) =
            Client(Settings.Enabled(), logs: logs);
#pragma warning disable EXTEXP0001
        builder.RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001

        await using ServiceProvider provider = services.BuildServiceProvider();
        (await Create(provider).GetAsync("http://origin.test/x", TestContext.Current.CancellationToken)).Dispose();

        Assert.Equal(1, origin.Count);

        LogEntry warning = Assert.Single(logs.WithEvent(HttpResilience.NET.Internal.HttpResilienceLogging.ConfigurationCategory, 16));
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("'test'", warning.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Replacing this package's handler with one of the consumer's own keeps the handler count the same, so a
    /// count cannot see it. The check follows this package's handlers by identity instead.
    /// </summary>
    /// <remarks>Fails if the check falls back to counting every resilience handler in the chain.</remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ReplacingThisPackagesHandler_WithTheConsumersOwn_IsStillWarned()
    {
        var logs = new ListLoggerProvider();
        (ServiceCollection services, IHttpClientBuilder builder, _) = Client(Settings.Enabled(), logs: logs);
#pragma warning disable EXTEXP0001
        builder.RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
        builder.AddResilienceHandler("mine", pipeline => pipeline.AddTimeout(TimeSpan.FromSeconds(5)));

        await using ServiceProvider provider = services.BuildServiceProvider();
        (await Create(provider).GetAsync("http://origin.test/x", TestContext.Current.CancellationToken)).Dispose();

        Assert.Single(logs.WithEvent(HttpResilience.NET.Internal.HttpResilienceLogging.ConfigurationCategory, 16));
    }

    /// <summary>
    /// The supported setups never warn: an extra handler of the consumer's own, a disabled client, the platform's
    /// handlers removed before this package's (the Aspire fix), and every handler group active.
    /// </summary>
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData("extra handler")]
    [InlineData("disabled client")]
    [InlineData("removed before")]
    [InlineData("every group")]
    public async Task SupportedSetups_DoNotWarnAboutMissingHandlers(string setup)
    {
        var logs = new ListLoggerProvider();
        Settings settings = setup switch
        {
            "disabled client" => Settings.Enabled().Set("Enabled", "false"),
            "every group" => Settings.Enabled()
                .Set("ConcurrencyLimiter:Enabled", "true").Set("ConcurrencyLimiter:Limit", "5")
                .Set("RateLimiter:Enabled", "true").Set("RateLimiter:PermitLimit", "100"),
            _ => Settings.Enabled()
        };

        var services = new ServiceCollection();
        services.AddHttpResilience(Configuration(settings));
        services.AddLogging(logging => logging.AddProvider(logs));

        IHttpClientBuilder builder = services.AddHttpClient("test");
        if (setup == "removed before")
        {
#pragma warning disable EXTEXP0001
            builder.RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
        }

        builder.AddHttpResilienceHandler().ConfigurePrimaryHttpMessageHandler(() => new RecordingHandler());
        if (setup == "extra handler")
        {
            builder.AddResilienceHandler("mine", pipeline => pipeline.AddTimeout(TimeSpan.FromSeconds(5)));
        }

        await using ServiceProvider provider = services.BuildServiceProvider();
        (await Create(provider).GetAsync("http://origin.test/x", TestContext.Current.CancellationToken)).Dispose();

        Assert.Empty(logs.WithEvent(HttpResilience.NET.Internal.HttpResilienceLogging.ConfigurationCategory, 16));
    }
}
