using HttpResilience.NET.Options;
using HttpResilience.NET.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;

namespace HttpResilience.NET.Tests.Behavior;

/// <summary>
/// What happens when a <i>consumer</i> also does something to a client this package configured.
/// </summary>
/// <remarks>
/// A standing test axis rather than a set of one-off cases, for the same reason "the origin is slow" became
/// one in <c>HedgingSafetyTests</c>. Every guard in this package was written against its own API, and a
/// review found four defects in a row at this boundary instead: a consumer calling the platform's
/// <c>AddStandardResilienceHandler</c> on the same client, a consumer setting <c>HttpClient.Timeout</c> in
/// code, a consumer's <c>PostConfigure</c> reaching a value the package had captured. All four passed a
/// 319-test suite, because no test had a consumer in it.
/// <para>
/// The rule for adding here: name what a consumer plausibly writes, then assert the outcome at the origin or
/// on the constructed client -- not that registration threw.
/// </para>
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
        _ = hedged ? builder.AddHedgedHttpResilience() : builder.AddHttpResilience();
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
    /// This test pins a defect the package cannot prevent, which is why it asserts the damage as well as the
    /// notice. Measured: one GET makes <b>nine</b> origin calls -- three configured attempts, each retried
    /// three times by the outer pipeline -- and the total timeout is applied twice. Nothing throws.
    /// <para>
    /// It is not prevented because the excess is not attributable through public API: the escape hatch this
    /// package documents, <c>AddResilienceHandler</c>, adds a handler to the same chain and is correct. See
    /// <c>ResilienceHandlerCountFilter</c> for the alternatives that were measured and rejected. So the guard
    /// reports at Information and the docs carry the warning.
    /// </para>
    /// <para>
    /// Fails if the notice stops being emitted, or if the nesting arithmetic changes -- in which case the
    /// number quoted in README.md and ARCHITECTURE.md is wrong and should be re-measured here first.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AConsumersOwnStandardHandler_NestsTwoPipelines_AndSaysSo()
    {
        var logs = new ListLoggerProvider();
        (ServiceCollection services, IHttpClientBuilder builder, RecordingHandler origin) =
            Client(Settings.Enabled(), logs: logs);
        builder.AddStandardResilienceHandler();

        await using ServiceProvider provider = services.BuildServiceProvider();
        HttpClient client = Create(provider);

        (await client.GetAsync("http://origin.test/x")).Dispose();

        // Three configured attempts, retried three times by the outer pipeline. This is the damage.
        Assert.Equal(9, origin.Count);

        string notice = Assert.Single(logs.Records, r => r.Contains("resilience handlers", StringComparison.Ordinal));
        Assert.Contains("[Information]", notice, StringComparison.Ordinal);
        Assert.Contains("NESTED", notice, StringComparison.Ordinal);
        Assert.Contains("nine origin calls", notice, StringComparison.Ordinal);
    }

    /// <summary>
    /// The one symptom of that nesting the package <i>can</i> prevent: the finite client timeout survives it.
    /// </summary>
    /// <remarks>
    /// The platform's resilience handler sets <see cref="HttpClient.Timeout"/> to
    /// <see cref="Timeout.InfiniteTimeSpan"/> so its own total timeout is authoritative. This package puts a
    /// finite bound back, because <c>Timeout:Total</c> stops applying at response headers and nothing else
    /// bounds the response <i>body</i>. Measured before the fix: a second platform handler put the timeout
    /// back to <c>-00:00:00.001</c>, so an origin could hold a connection and a buffer open indefinitely by
    /// trickling a body, and the pipeline reported a fast successful attempt.
    /// <para>
    /// Fails if the client timeout goes back to being applied with <c>ConfigureHttpClient</c>, which is
    /// last-wins and loses to any later registration.
    /// </para>
    /// </remarks>
    [Fact]
    public void ASecondPlatformHandler_DoesNotTakeAwayTheFiniteClientTimeout()
    {
        (ServiceCollection services, IHttpClientBuilder builder, _) = Client(Settings.Enabled());
        builder.AddStandardResilienceHandler();

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Equal(TimeSpan.FromSeconds(30) + TimeSpan.FromSeconds(30), Create(provider).Timeout);
    }

    /// <summary>
    /// The same on the hedging pipeline, where the nesting multiplies fan-out rather than adding to it.
    /// </summary>
    [Fact]
    public async Task AConsumersOwnHedgingHandler_IsReportedToo()
    {
        var logs = new ListLoggerProvider();
        (ServiceCollection services, IHttpClientBuilder builder, _) =
            Client(Settings.Hedged(), hedged: true, logs: logs);
        builder.AddStandardHedgingHandler();

        await using ServiceProvider provider = services.BuildServiceProvider();
        Create(provider).Dispose();

        Assert.Single(logs.Records, r => r.Contains("resilience handlers", StringComparison.Ordinal));
    }

    /// <summary>
    /// The escape hatch the documentation actually recommends still composes, and must not trip the guard.
    /// </summary>
    /// <remarks>
    /// This is the half that matters for the guard being usable: <c>AddResilienceHandler</c> adds one more
    /// <c>ResilienceHandler</c> to the chain, exactly as a second standard handler does, and the difference is
    /// that it neither nests a retry loop nor resets <c>HttpClient.Timeout</c>. Measured: with it, the origin
    /// still sees three calls and the client timeout is still finite.
    /// <para>
    /// That indistinguishability is why the guard reports instead of failing. The first version of it threw,
    /// and this test is what caught it rejecting the pattern the README recommends. The notice naming both
    /// possibilities is the accepted cost; a Warning here would have been worse.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheSanctionedEscapeHatch_StillComposes()
    {
        (ServiceCollection services, IHttpClientBuilder builder, RecordingHandler origin) =
            Client(Settings.Enabled());
        builder.AddResilienceHandler("legacy-quirk", pipeline => pipeline.AddTimeout(TimeSpan.FromSeconds(9)));

        await using ServiceProvider provider = services.BuildServiceProvider();
        HttpClient client = Create(provider);

        (await client.GetAsync("http://origin.test/x")).Dispose();

        Assert.Equal(3, origin.Count);
        Assert.Equal(TimeSpan.FromSeconds(30) + TimeSpan.FromSeconds(30), client.Timeout);
    }

    /// <summary>
    /// A client with resilience switched off adds no pipeline, so a consumer may add the platform's handler
    /// itself -- which is the supported way to migrate a client onto this package one step at a time.
    /// </summary>
    /// <remarks>
    /// Fails if the guard starts counting handlers for clients this package registered no pipeline for. That
    /// would turn <c>Enabled: false</c> from "adding the package changes nothing" into "adding the package
    /// breaks a client that had its own resilience", which is the opposite of the flag's whole purpose.
    /// </remarks>
    [Fact]
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
    /// <c>ValidateTimeouts</c> refuses <c>Timeout:Client</c> at or below <c>Timeout:Total</c>, and the reason
    /// is good: at that point it truncates the pipeline instead of backing it up, and does so with a bare
    /// <see cref="TaskCanceledException"/> carrying none of the pipeline's context. Measured before the fix:
    /// the identical value written as <c>ConfigureHttpClient(c =&gt; c.Timeout = 2s)</c> against a 30-second
    /// total budget produced no failure and no warning, because it is not an options value and the validator
    /// cannot see it.
    /// <para>
    /// <c>ConfigureHttpClient</c> actions run in registration order and last wins, so the fix is the one
    /// already used for the primary handler: apply from a phase that runs after every registration. Fails if
    /// the client timeout goes back to being applied with <c>ConfigureHttpClient</c>.
    /// </para>
    /// </remarks>
    [Fact]
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
    /// This is the limit of <c>ApplyClientTimeout</c>, pinned rather than implied away, in the same spirit as
    /// <see cref="AConsumersOwnStandardHandler_NestsTwoPipelines_AndSaysSo"/>. Every phase this package can
    /// reach -- <c>ConfigureHttpClient</c>, <c>IPostConfigureOptions&lt;HttpClientFactoryOptions&gt;</c> --
    /// runs while <see cref="IHttpClientFactory"/> is building the client. A typed client's constructor runs
    /// <i>after</i> that, on the instance the factory has already finished with, so the last write is the
    /// consumer's and no options validator, filter or post-configure exists that could observe it.
    /// <para>
    /// The failure it produces is exactly the one <c>ValidateTimeouts</c> refuses when the same value is
    /// written as <c>Timeout:Client</c>: a 30-second pipeline truncated to one second, one origin call, and a
    /// bare <see cref="TaskCanceledException"/> carrying none of the pipeline's context. Asserted at the
    /// origin because the count is what says the pipeline was cut short rather than merely reported on.
    /// </para>
    /// <para>
    /// Documented in README.md, docs/ARCHITECTURE.md and docs/PRODUCTION-CHECKLIST.md, all of which said the
    /// guard covered "setting HttpClient.Timeout in code" without qualification until this test was written.
    /// If a future change does make this reachable -- an <c>IHttpClientFactory</c> phase that runs after
    /// typed-client activation would be the only way -- this test failing is the signal to correct those
    /// three documents, not to delete it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ATypedClientsConstructorTimeout_TruncatesThePipeline_AndNoGuardCanSeeIt()
    {
        var services = new ServiceCollection();
        services.AddHttpResilience(Configuration(Settings.Enabled()));

        var origin = new RecordingHandler(async (_, _, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        });

        services.AddHttpClient<TruncatingTypedClient>("test")
            .AddHttpResilience()
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
    /// This test asserted the opposite until the fourth review, and the reasoning it carried was wrong. It
    /// said no non-colliding sentinel exists for a <see cref="TimeSpan"/> whose unset value is a real
    /// duration -- true, and beside the point, because the ambiguity was never in the value. It was in the
    /// moment of reading. <c>ApplyClientTimeout</c> now registers an action at index <b>0</b> of
    /// <c>HttpClientActions</c>, which runs before every <c>ConfigureHttpClient</c> and before the platform
    /// handler's own assignment, and normalises the timeout to infinite. Anything finite that survives to
    /// the last action is therefore a consumer statement by construction, and 100 seconds stops being a
    /// special case.
    /// <para>
    /// Production change that would make this fail: removing the index-0 normalizing action, or restoring
    /// the 100-second branch. Either one silently swallows a deliberate assignment again.
    /// </para>
    /// </remarks>
    [Fact]
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
    /// The failure direction of the change above: normalizing to infinite before the consumer's actions is
    /// only safe while the last action still reads infinite as "nobody stated one". Production change that
    /// would make this fail: treating infinite as a consumer statement.
    /// </remarks>
    [Fact]
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
    /// The point of the failure above is that there is a right place to say this. If the schema could not
    /// express it, the guard would be an obstruction rather than a redirection.
    /// </remarks>
    [Fact]
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
    /// Setting a default request header is the ordinary use of that method, and it must keep working. Fails
    /// if the guard starts rejecting any <c>ConfigureHttpClient</c> registration rather than a conflicting
    /// timeout.
    /// </remarks>
    [Fact]
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
    /// Both new guards run from container-wide extension points -- a handler-builder filter and a
    /// post-configure on <c>HttpClientFactoryOptions</c> -- so the blast radius if they misjudge which
    /// clients they own is every client in the process. Fails if either starts asserting on a stranger.
    /// </remarks>
    [Fact]
    public void AnUnrelatedClient_IsNotExaminedByEitherGuard()
    {
        var services = new ServiceCollection();
        services.AddHttpResilience(Configuration(Settings.Enabled()));
        services.AddHttpClient("test").AddHttpResilience();

        IHttpClientBuilder stranger = services.AddHttpClient("stranger");
        stranger.AddStandardResilienceHandler();
        stranger.ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(2));

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Equal(TimeSpan.FromSeconds(2), Create(provider, "stranger").Timeout);
    }

    /// <summary>
    /// Every pipeline shape accounts for exactly the resilience handlers it adds, so the notice fires on a
    /// nested pipeline and stays silent on a correct one.
    /// </summary>
    /// <remarks>
    /// The tally recorded at registration is arithmetic over the same conditions that decide which handlers to
    /// add, and that duplication is the risk. It is not theoretical: the first version assumed the platform's
    /// hedging handler contributed one handler where it contributes <b>two</b> -- a routing handler around the
    /// hedging one -- and reported every hedged client in the suite as nested.
    /// <para>
    /// Both halves are asserted per shape, because each catches a different drift. A tally that is too low
    /// fails the silent half; a tally that is too high fails the reporting half. Fails if the platform changes
    /// how many handlers either of its handlers adds -- which is the point: this names the cause instead of
    /// leaving thirty unrelated failures.
    /// </para>
    /// </remarks>
    [Theory]
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
        if (hedged)
        {
            builder.AddStandardHedgingHandler();
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
    /// <see cref="IHttpClientFactory"/> rebuilds a client's handler chain every time the handler lifetime
    /// expires -- every two minutes by default -- and every
    /// <see cref="Microsoft.Extensions.Http.IHttpMessageHandlerBuilderFilter"/> runs again each time. Without
    /// deduplication this notice would repeat for the life of the process, which turns a diagnostic into noise
    /// and is exactly why every other notice here reports once.
    /// <para>
    /// The filter is driven directly rather than by waiting for a rotation, because the alternative is a test
    /// that sleeps -- and a timing-dependent assertion is not one this suite accepts. Fails if the
    /// deduplicating set is removed.
    /// </para>
    /// </remarks>
    [Fact]
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
    /// The package used to assign <c>AllowAutoRedirect</c> unconditionally from the resolved value, which for
    /// a standard client that stated nothing is the runtime default of <see langword="true"/> -- so the one
    /// property on a consumer's handler that is a security control was reversed by a connection-pool switch,
    /// while TROUBLESHOOTING.md told its owner the handler's other settings were preserved. The runtime strips
    /// <c>Authorization</c> across a redirect and re-sends <c>X-Api-Key</c> and every other custom credential
    /// header verbatim, so the reversal is a credential-disclosure path, not a preference.
    /// <para>
    /// Production change that would make this fail: dropping the <c>AllowAutoRedirectStated</c> check in
    /// <c>SocketsHttpHandlerFactory.ApplyRedirectBound</c>.
    /// </para>
    /// </remarks>
    [Fact]
    public void AConsumersOwnRedirectBound_SurvivesConnectionTuning()
    {
        var services = new ServiceCollection();
        services.AddHttpResilience(Configuration(Settings.Enabled().Set("Connection:Enabled", "true")));

        var hardened = new SocketsHttpHandler { AllowAutoRedirect = false, MaxConnectionsPerServer = 7 };
        services.AddHttpClient("test")
            .ConfigurePrimaryHttpMessageHandler(() => hardened)
            .AddHttpResilience();

        using ServiceProvider provider = services.BuildServiceProvider();
        Create(provider).Dispose();

        Assert.False(
            hardened.AllowAutoRedirect,
            "a redirect bound the consumer set on its own handler was reversed by Connection:Enabled.");

        // The properties the schema always states are still applied, so this is not a blanket "stop touching
        // the handler" -- only the one the schema had no opinion about.
        Assert.Equal(TimeSpan.FromSeconds(3), hardened.ConnectTimeout);
        Assert.Equal(TimeSpan.FromMinutes(2), hardened.PooledConnectionLifetime);
        Assert.Equal(7, hardened.MaxConnectionsPerServer);
    }

    /// <summary>
    /// The other direction: a schema that <i>does</i> state the value still wins over the consumer's handler.
    /// </summary>
    /// <remarks>
    /// Without this, the fix above would be indistinguishable from "never write AllowAutoRedirect", which
    /// would silently drop a stated <c>Connection:AllowAutoRedirect</c> and leave a hedged client following
    /// redirects around its own allow-list.
    /// </remarks>
    [Theory]
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
        _ = hedged ? builder.AddHedgedHttpResilience() : builder.AddHttpResilience();

        using ServiceProvider provider = services.BuildServiceProvider();
        Create(provider).Dispose();

        Assert.Equal(expected, consumers.AllowAutoRedirect);
    }

    /// <summary>
    /// A hedged client whose primary handler has no redirect switch at all. The bound cannot be applied, and
    /// that used to be silent.
    /// </summary>
    /// <remarks>
    /// Reported rather than thrown: this shape is overwhelmingly a test stub, which resolves no redirects and
    /// cannot breach the bound, and the way out of a throw would be to state
    /// <c>Connection:AllowAutoRedirect</c> true -- switching a security bound off to make a test compile. The
    /// shape that is a genuine hazard, a handler wrapping a <see cref="SocketsHttpHandler"/> of its own, is
    /// indistinguishable from it here. So the gap gets a Warning naming the client, and this test is what
    /// stops it going back to silence.
    /// </remarks>
    [Fact]
    public void AHedgedClientWhoseHandlerHasNoRedirectSwitch_SaysTheBoundWasNotApplied()
    {
        var logs = new ListLoggerProvider();

        // The helper's primary handler is a RecordingHandler: neither SocketsHttpHandler nor HttpClientHandler.
        (ServiceCollection services, _, _) = Client(Settings.Hedged(), hedged: true, logs: logs);

        using ServiceProvider provider = services.BuildServiceProvider();
        Create(provider).Dispose();
        RebuildHandlerChain(provider, "test");

        string record = Assert.Single(
            logs.Records, r => r.Contains("could not apply the redirect bound", StringComparison.Ordinal));

        Assert.StartsWith("[Warning]", record, StringComparison.Ordinal);
        Assert.Contains("RecordingHandler", record, StringComparison.Ordinal);
        Assert.Contains("'test'", record, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same client with a handler that <i>does</i> carry the switch says nothing, and the bound holds.
    /// </summary>
    [Fact]
    public void AHedgedClientWithASocketsHandler_AppliesTheBoundAndSaysNothing()
    {
        var logs = new ListLoggerProvider();
        var services = new ServiceCollection();
        services.AddHttpResilience(Configuration(Settings.Hedged()));
        services.AddLogging(logging => logging.AddProvider(logs));

        var handler = new SocketsHttpHandler { AllowAutoRedirect = true };
        services.AddHttpClient("test")
            .ConfigurePrimaryHttpMessageHandler(() => handler)
            .AddHedgedHttpResilience();

        using ServiceProvider provider = services.BuildServiceProvider();
        Create(provider).Dispose();

        Assert.False(handler.AllowAutoRedirect);
        Assert.DoesNotContain(
            logs.Records, r => r.Contains("could not apply the redirect bound", StringComparison.Ordinal));
    }

    /// <summary>
    /// Runs every handler-builder filter over a fresh builder for one client, the way
    /// <see cref="IHttpClientFactory"/> does when a handler expires.
    /// </summary>
    private static void RebuildHandlerChain(IServiceProvider provider, string clientName)
    {
        HttpClientFactoryOptions options =
            provider.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>().Get(clientName);

        // The innermost action is what adds the handlers, exactly as DefaultHttpClientFactory composes it --
        // so the filters that wrap it see the same chain a real rotation would produce. Running the filters
        // over an empty builder would have made this test pass with the deduplication removed.
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
    /// The limiter was built from the <see cref="HttpResilienceOptions"/> instance captured at registration,
    /// while <see cref="Microsoft.Extensions.Options.IOptionsMonitor{TOptions}"/> handed back a different
    /// one. Measured before the fix: the monitor reported a permit budget of 50 and the pipeline enforced 2,
    /// with startup validation clean -- so a consumer lowering a budget in code got the higher registration
    /// value, and every dashboard showed the lower one.
    /// <para>
    /// This is the one value that was not live, and nothing caught it: the rate limiter's shape is not in
    /// <c>StructuralDecisions</c>, so <c>NamedPipelineOptionsValidator</c> had nothing to compare. Fails if
    /// the limiter goes back to being built from a registration-time snapshot.
    /// </para>
    /// </remarks>
    [Fact]
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
                (await client.GetAsync("http://origin.test/x")).Dispose();
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
}
