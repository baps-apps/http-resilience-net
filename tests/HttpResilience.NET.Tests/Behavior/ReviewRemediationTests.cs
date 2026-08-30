using System.Net;
using System.Net.Http.Headers;
using System.Threading.RateLimiting;
using HttpResilience.NET.Internal;
using HttpResilience.NET.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly.RateLimiting;
using Polly.Timeout;

// Both the schema's limiter options and the BCL type a consumer would collide with are used here.
using BclConcurrencyLimiterOptions = System.Threading.RateLimiting.ConcurrencyLimiterOptions;

namespace HttpResilience.NET.Tests.Behavior;

/// <summary>
/// Every defect found by the third architecture review, pinned by the behavior that exposed it.
/// </summary>
/// <remarks>
/// Two of these were reproduced against the built package before any fix existed, not reasoned about: a
/// consumer's keyed <see cref="RateLimiter"/> silently replacing the configured one in the pipeline, and a
/// single root-level key making every client in the process repeat POST bodies with nothing logged.
/// <para>
/// Each test names, in its own remarks, the production change that makes it fail. A safety test that would
/// pass against the broken code proves nothing, which is the lesson the hedging suite already learned.
/// </para>
/// </remarks>
public class ReviewRemediationTests
{
    private static IConfigurationRoot Configuration(Settings settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings.Build()).Build();

    // ---------------------------------------------------------------------------------------------------
    // HR-02  A consumer's keyed RateLimiter must not become the one the pipeline enforces.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Keyed service keys share one namespace per service type, and <see cref="RateLimiter"/> is a BCL type
    /// an application is likely to register under a domain name of its own.
    /// </summary>
    /// <remarks>
    /// Fails if the limiter goes back to being keyed on the bare client name. Measured against the built
    /// package before the fix: the pipeline enforced the consumer's concurrency limiter of 1 in place of the
    /// configured budget, with no exception, no log and no validation failure -- and the configured
    /// <c>PermitLimit</c> became dead configuration that <c>UnusedClientSectionValidator</c> cannot see,
    /// because the section is read.
    /// <para>
    /// Behavioural rather than a type assertion, because the type assertion could not have failed before the
    /// fix: <c>RateLimiterKey</c> did not exist to assert against. What is asserted is how many requests the
    /// pipeline actually admits at once.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AConsumersKeyedRateLimiter_DoesNotReplaceTheOneThePipelineEnforces()
    {
        var gate = new TaskCompletionSource();
        var origin = new RecordingHandler(async (request, _, cancellationToken) =>
        {
            await gate.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };
        });

        var services = new ServiceCollection();
        services.AddHttpResilience(Configuration(Settings.Enabled()
            .Set("Retry:Enabled", "false")
            .Set("RateLimiter:Enabled", "true")
            .Set("RateLimiter:PermitLimit", "5")
            .Set("RateLimiter:Window", "00:01:00")));

        services.AddHttpClient("Search")
            .AddHttpResilience()
            .ConfigurePrimaryHttpMessageHandler(() => origin);

        // The collision. An inbound rate-limit policy or a domain limiter keyed by the same name is an
        // ordinary shape, and AddKeyedSingleton is not TryAdd, so the last registration wins.
        services.AddKeyedSingleton<RateLimiter>("Search", (_, _) =>
            new ConcurrencyLimiter(new BclConcurrencyLimiterOptions { PermitLimit = 1, QueueLimit = 0 }));

        await using ServiceProvider provider = services.BuildServiceProvider();
        using HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("Search");

        Task<HttpResponseMessage>[] inFlight =
            [.. Enumerable.Range(0, 3).Select(_ => client.GetAsync("http://origin.test/x"))];

        while (origin.Count < 3 && !inFlight.Any(t => t.IsFaulted))
        {
            await Task.Yield();
        }

        gate.SetResult();
        foreach (HttpResponseMessage response in await Task.WhenAll(inFlight))
        {
            response.Dispose();
        }

        // Three requests against a budget of five. Under the consumer's limiter of one, two would have been
        // rejected with RateLimiterRejectedException before ever reaching the origin.
        Assert.Equal(3, origin.Count);
        Assert.Equal(3, origin.MaxConcurrent);

        // The other half, and the one that matters as much: the *configured* limiter is genuinely in force.
        // The assertion above alone would also pass against a pipeline carrying no rate limiter at all --
        // found by running the reproduction probe again after the fix rather than by re-reading the test.
        // Two more requests exhaust the window of five; the third is refused.
        for (int i = 0; i < 2; i++)
        {
            (await client.GetAsync("http://origin.test/x")).Dispose();
        }

        await Assert.ThrowsAsync<RateLimiterRejectedException>(
            () => client.GetAsync("http://origin.test/x"));
        Assert.Equal(5, origin.Count);

        // And the two registrations coexist: the consumer still gets its own.
        Assert.IsType<ConcurrencyLimiter>(provider.GetRequiredKeyedService<RateLimiter>("Search"));
        Assert.IsType<FixedWindowRateLimiter>(
            provider.GetRequiredKeyedService<RateLimiter>(new RateLimiterKey("Search")));
    }

    // ---------------------------------------------------------------------------------------------------
    // HR-01  The safe-method guarantee must not have a silent, fleet-wide off switch.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Neither guard may be switched off at the root, where one key decides it for every client in the
    /// process -- including clients registered after it that state nothing.
    /// </summary>
    /// <remarks>
    /// Enforced at registration rather than only by the options validator, and this test is why. The
    /// validator's copy of the rule runs on the <i>root</i> options, which materialize only when something
    /// invokes <c>IStartupValidator</c>: a generic host does, and a bare <see cref="ServiceCollection"/> plus
    /// <c>BuildServiceProvider</c> -- which is what the sample and this whole test suite use -- does not.
    /// When the rule lived only in the validator, all four existing tests that set these flags at the root
    /// went on passing, which is what showed the guard could be skipped by choice of hosting model.
    /// <para>
    /// Fails if the check moves out of <c>AddHttpResilience</c> and back into the validator alone.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("Retry:DisableForUnsafeHttpMethods")]
    [InlineData("Hedging:DisableForUnsafeHttpMethods")]
    public void ASafeMethodGuardSwitchedOffAtTheRoot_FailsAtRegistration(string key)
    {
        var services = new ServiceCollection();

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(
            () => services.AddHttpResilience(Configuration(Settings.Enabled().Set(key, "false"))));

        string message = string.Join(" ", exception.Failures);
        Assert.Contains($"HttpResilience:{key}", message, StringComparison.Ordinal);
        Assert.Contains("HttpResilience:Clients:{name}", message, StringComparison.Ordinal);
        Assert.Contains("idempotency handling", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// It fails before a single client has been registered, so no client can be built on a root the package
    /// would have refused.
    /// </summary>
    [Fact]
    public void TheRootGuardFails_EvenWithNoClientsAndNoHost()
    {
        var services = new ServiceCollection();

        Assert.Throws<OptionsValidationException>(() => services.AddHttpResilience(
            Configuration(Settings.Empty().Set("Retry:DisableForUnsafeHttpMethods", "false"))));

        // Nothing was left half-registered for a later call to build on.
        Assert.Empty(services);
    }

    /// <summary>
    /// The per-client decision is still expressible, because it is a real one -- an endpoint that
    /// deduplicates on an idempotency key.
    /// </summary>
    [Fact]
    public async Task TheSameGuard_InAClientSection_IsAccepted()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled().ForClient("test", "Retry:DisableForUnsafeHttpMethods", "false"));

        await harness.SendAsync(HttpMethod.Post);

        Assert.Equal(3, harness.Origin.Count);
    }

    /// <summary>
    /// A root-level allow-list may <b>narrow</b> what every client retries. That is what one shared statement
    /// should be able to say, and it is strictly safer than the default.
    /// </summary>
    [Fact]
    public async Task ARootLevelAllowListOfSafeMethods_IsStillAccepted()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled().Set("Retry:RetryableMethods:0", "GET"));

        await harness.GetAsync();
        Assert.Equal(3, harness.Origin.Count);

        // And it is in force rather than decorative: OPTIONS is safe, but it is not on the list.
        await harness.SendAsync(HttpMethod.Options);
        Assert.Equal(4, harness.Origin.Count);
    }

    /// <summary>
    /// A root-level allow-list may not <b>widen</b>. An unsafe entry there reaches every standard client in
    /// the process, including clients registered afterwards that state nothing -- the same fleet-wide
    /// decision <c>Retry:DisableForUnsafeHttpMethods: false</c> is refused for, reached by a different key.
    /// </summary>
    /// <remarks>
    /// Measured before this rule existed: a root list naming POST, two clients that stated nothing, and both
    /// delivering a POST body three times with registration and startup validation both clean. The only
    /// signal was the event-10 warning, which named a key in the client's own section that did not exist.
    /// <para>
    /// Fails at <b>registration</b>, like the two flag guards and for the same reason: the validator's copy
    /// of the rule runs on root options that materialize only when something invokes
    /// <c>IStartupValidator</c>, which a bare <see cref="ServiceCollection"/> never does.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    [InlineData("PURGE")]
    public void ARootLevelAllowListNamingAnUnsafeMethod_FailsAtRegistration(string method)
    {
        var services = new ServiceCollection();

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(
            () => services.AddHttpResilience(
                Configuration(Settings.Enabled().Set("Retry:RetryableMethods:0", method))));

        string message = string.Join(" ", exception.Failures);
        Assert.Contains("HttpResilience:Retry:RetryableMethods", message, StringComparison.Ordinal);
        Assert.Contains(method, message, StringComparison.Ordinal);
        Assert.Contains("HttpResilience:Clients:{name}", message, StringComparison.Ordinal);

        // And nothing was left half-registered for a later call to build on.
        Assert.Empty(services);
    }

    /// <summary>
    /// The same list in a client's own section is the supported opt-in and still works.
    /// </summary>
    [Fact]
    public async Task TheSameAllowList_InAClientSection_IsAccepted()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled().ForClient("test", "Retry:RetryableMethods:0", "POST"));

        await harness.SendAsync(HttpMethod.Post);

        Assert.Equal(3, harness.Origin.Count);
    }

    /// <summary>
    /// A client that writes the safety guard while an allow-list is in force for it has written the statement
    /// that is <i>not</i> obeyed, and startup now says so instead of retrying the POST.
    /// </summary>
    /// <remarks>
    /// The allow-list replaces the guard outright in <c>StandardPipelineConfigurator.ConfigureRetry</c>. The
    /// validator already refused the flag being <c>false</c> beside a list; this is the direction where the
    /// discarded statement is the protective one, which is strictly worse and was silently accepted.
    /// Measured before the fix: a client section containing only
    /// <c>Retry:DisableForUnsafeHttpMethods: true</c>, under a root list naming POST, delivered a POST body
    /// three times with a clean startup.
    /// <para>
    /// Statedness comes from the section rather than the bound value, because the flag defaults to true and
    /// the root is required to leave it that way -- so a bound <c>true</c> cannot be told from silence.
    /// Fails if <c>CollectInertConfiguration</c> stops reading the raw section.
    /// </para>
    /// </remarks>
    [Fact]
    public void AClientGuardBesideAnAllowListInForce_FailsAtRegistration()
    {
        var services = new ServiceCollection();
        services.AddHttpResilience(Configuration(Settings.Enabled()
            .ForClient("orders", "Retry:RetryableMethods:0", "POST")
            .ForClient("orders", "Retry:DisableForUnsafeHttpMethods", "true")));

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(
            () => services.AddHttpClient("orders").AddHttpResilience());

        string message = string.Join(" ", exception.Failures);
        Assert.Contains(
            "HttpResilience:Clients:orders -- Retry:DisableForUnsafeHttpMethods", message, StringComparison.Ordinal);
        Assert.Contains("bound and never read", message, StringComparison.Ordinal);
        Assert.Contains("the methods actually retried are POST", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the guard on its own, with no list in force, is left alone -- the overwhelmingly common case.
    /// </summary>
    [Fact]
    public async Task AClientGuardWithNoAllowList_IsAccepted()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled().ForClient("test", "Retry:DisableForUnsafeHttpMethods", "true"));

        await harness.SendAsync(HttpMethod.Post);

        Assert.Equal(1, harness.Origin.Count);
    }

    /// <summary>
    /// A standard client stating hedging keys is the mirror of a hedged client stating retry keys, and the
    /// dangerous entry is identical: a written decision about duplicating mutating requests that the pipeline
    /// has no strategy to read.
    /// </summary>
    [Theory]
    [InlineData("Hedging:MaxHedgedAttempts", "5")]
    [InlineData("Hedging:DisableForUnsafeHttpMethods", "false")]
    public void HedgingConfigurationOnAStandardClient_FailsAtRegistration(string key, string value)
    {
        var services = new ServiceCollection();
        services.AddHttpResilience(Configuration(Settings.Enabled().ForClient("orders", key, value)));

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(
            () => services.AddHttpClient("orders").AddHttpResilience());

        string message = string.Join(" ", exception.Failures);
        Assert.Contains("HttpResilience:Clients:orders -- Hedging", message, StringComparison.Ordinal);
        Assert.Contains("bound and never read", message, StringComparison.Ordinal);
        Assert.Contains("AddHedgedHttpResilience", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Root hedging values are inherited by hedged clients, so a standard client sharing that root must not
    /// be the thing that fails startup -- the same asymmetry the retry half already had.
    /// </summary>
    [Fact]
    public async Task RootHedgingConfiguration_DoesNotFailAStandardClient()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled().Set("Hedging:MaxHedgedAttempts", "2"));

        await harness.GetAsync();

        Assert.Equal(3, harness.Origin.Count);
    }

    /// <summary>
    /// The startup warning has to name the key an operator can actually edit. An inherited list lives at the
    /// root, and naming this client's section sent them looking for a key that is not in the file.
    /// </summary>
    /// <remarks>
    /// This is the only signal that the inherited-list case is happening at all, so a wrong path here is a
    /// wrong path on the one message whose job is to be actionable during an incident.
    /// </remarks>
    [Fact]
    public void AnInheritedAllowList_IsReportedAgainstTheRootSection()
    {
        string[] notices = NoticesFor(Settings.Enabled()
            .Set("Retry:RetryableMethods:0", "GET")
            .ForClient("orders", "Timeout:Total", "00:00:30"));

        // A safe-only root list warns about nothing, so widen it in code the way a consumer would have to.
        Assert.Empty(notices);

        notices = NoticesFor(
            Settings.Enabled().ForClient("orders", "Timeout:Total", "00:00:30"),
            configure: options => options.Retry.RetryableMethods = ["GET", "POST"]);

        Assert.Single(notices);
        Assert.Contains("POST", notices[0], StringComparison.Ordinal);
        Assert.Contains("set in code rather than in configuration", notices[0], StringComparison.Ordinal);
        Assert.DoesNotContain(
            "HttpResilience:Clients:orders:Retry:RetryableMethods", notices[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether a circuit breaker can ever open is arithmetic over two configured values and per-replica
    /// traffic. The package can do that division at startup and an operator usually has not.
    /// </summary>
    /// <remarks>
    /// Information rather than Warning: unlike the disabled-client and unsafe-method notices, the
    /// configuration reported here is frequently correct. What is not visible without it is that a client
    /// with the default thresholds and a few requests a second per replica has a breaker in its
    /// configuration, a breaker in its runbook, and no breaker in effect.
    /// </remarks>
    [Fact]
    public void EveryEnabledClient_StatesTheTrafficItsBreakerNeeds()
    {
        var sink = new ListLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddProvider(sink);
        });

        // The platform defaults: 100 attempts over 30 seconds is 3.3 per second, in ONE replica.
        services.AddHttpResilience(Configuration(Settings.Enabled()
            .Set("CircuitBreaker:MinimumThroughput", "100")
            .Set("CircuitBreaker:SamplingDuration", "00:00:30")));
        services.AddHttpClient("orders").AddHttpResilience(clientName: string.Empty);

        using ServiceProvider provider = services.BuildServiceProvider();
        foreach (IStartupValidator validator in provider.GetServices<IStartupValidator>())
        {
            validator.Validate();
        }

        string[] lines =
            [.. sink.Records.Where(r => r.Contains("needs 3.3 failing", StringComparison.Ordinal))];
        Assert.Single(lines);
        Assert.StartsWith("[Information]", lines[0], StringComparison.Ordinal);
        Assert.Contains("orders", lines[0], StringComparison.Ordinal);

        // Quoted in caller requests as well, because that is the number a service's own dashboards show:
        // the breaker is inside the retry loop, so at MaxRetries 2 one failing request is three observations.
        Assert.Contains("about 1.1 failing caller requests", lines[0], StringComparison.Ordinal);
        Assert.Contains("MinimumThroughput 100", lines[0], StringComparison.Ordinal);
        Assert.Contains("per replica, not fleet-wide", lines[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// A shared platform registration extension and the application that uses it may both ask for the
    /// dependency health check. Every other entry point in this package already survives that; this one
    /// failed the host with a message naming neither the package nor the call to remove.
    /// </summary>
    [Fact]
    public async Task RegisteringTheHealthCheckTwice_IsIdempotent()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpResilience(Configuration(Settings.Enabled()));
        services.AddHttpResilienceHealthChecks();
        services.AddHttpResilienceHealthChecks();

        // The builder-shaped overload composes with the other one rather than duplicating it.
        services.AddHealthChecks().AddHttpResilience();

        await using ServiceProvider provider = services.BuildServiceProvider();
        HealthReport report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync();

        Assert.Equal(HealthStatus.Healthy, report.Status);
        Assert.Single(report.Entries);
        Assert.True(report.Entries.ContainsKey("http-resilience"));
    }

    /// <summary>
    /// A second registration under a <i>different</i> name is a deliberate one and is honoured.
    /// </summary>
    [Fact]
    public async Task RegisteringTheHealthCheckUnderASecondName_AddsASecondCheck()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpResilience(Configuration(Settings.Enabled()));
        services.AddHttpResilienceHealthChecks();
        services.AddHttpResilienceHealthChecks("http-resilience-diagnostic");

        await using ServiceProvider provider = services.BuildServiceProvider();
        HealthReport report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync();

        Assert.Equal(2, report.Entries.Count);
    }

    /// <summary>
    /// Every client that can repeat a mutating request says so once, at startup, before traffic.
    /// </summary>
    /// <remarks>
    /// The counterpart to <c>DisabledClient_WarnsAtStartup_BeforeAnyClientIsCreated</c>, and registered the
    /// same way for the same reason: the state is invisible until an origin is billed twice, and nothing in
    /// the pipeline's own telemetry distinguishes a retried POST from a retried GET. Measured before the
    /// fix: three POST bodies delivered and not one log line mentioning why.
    /// <para>
    /// Fails if <c>UnsafeMethodNotice</c> stops being registered, or stops covering either mechanism.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("Retry:DisableForUnsafeHttpMethods", "false", false, "Retry:DisableForUnsafeHttpMethods")]
    [InlineData("Retry:RetryableMethods:0", "POST", false, "Retry:RetryableMethods")]
    [InlineData("Hedging:DisableForUnsafeHttpMethods", "false", true, "Hedging:DisableForUnsafeHttpMethods")]
    public void AClientThatCanRepeatAMutatingRequest_WarnsAtStartup(
        string key, string value, bool hedged, string expectedPath)
    {
        var sink = new ListLoggerProvider();
        Settings settings = (hedged ? Settings.Hedged() : Settings.Enabled()).ForClient("orders", key, value);

        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddProvider(sink);
        });
        services.AddHttpResilience(Configuration(settings));

        IHttpClientBuilder builder = services.AddHttpClient("orders");
        _ = hedged ? builder.AddHedgedHttpResilience() : builder.AddHttpResilience();

        using ServiceProvider provider = services.BuildServiceProvider();

        // Nothing yet: the notice is a post-configure, so it fires when the host materializes the options.
        Assert.Empty(Repeatable(sink));

        foreach (IStartupValidator validator in provider.GetServices<IStartupValidator>())
        {
            validator.Validate();
        }

        string[] notices = Repeatable(sink);
        Assert.Single(notices);
        Assert.StartsWith("[Warning]", notices[0], StringComparison.Ordinal);
        Assert.Contains("orders", notices[0], StringComparison.Ordinal);
        Assert.Contains($"HttpResilience:Clients:orders:{expectedPath}", notices[0], StringComparison.Ordinal);
        Assert.Contains("idempotency key", notices[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// An allow-list of safe methods changes nothing, so it must not produce a line -- a warning on the
    /// harmless case is how a warning on the dangerous one stops being read.
    /// </summary>
    [Fact]
    public void AnAllowListOfOnlySafeMethods_WarnsAboutNothing()
    {
        string[] notices = NoticesFor(Settings.Enabled()
            .ForClient("orders", "Retry:RetryableMethods:0", "GET")
            .ForClient("orders", "Retry:RetryableMethods:1", "HEAD"));

        Assert.Empty(notices);
    }

    /// <summary>A client with no pipeline repeats nothing, whatever its flags say.</summary>
    [Fact]
    public void ADisabledClient_WarnsAboutNothing()
    {
        string[] notices = NoticesFor(Settings.Empty()
            .Set("Enabled", "false")
            .ForClient("orders", "Retry:DisableForUnsafeHttpMethods", "false"));

        Assert.Empty(notices);
    }

    /// <summary>And the default configuration is silent, which is what makes the warning mean something.</summary>
    [Fact]
    public void TheDefaults_WarnAboutNothing()
    {
        Assert.Empty(NoticesFor(Settings.Enabled()));
    }

    /// <summary>
    /// Turning the guard off <i>after</i> registration is still reported, because the notice reads the
    /// options rather than the registration that created it.
    /// </summary>
    /// <remarks>
    /// Fails if <c>UnsafeMethodNotice</c> goes back to being registered conditionally on the flag, which is
    /// the mistake the hedging guard already made once: a safety notice a later configuration change can
    /// delete by deleting its registration is not a notice.
    /// </remarks>
    [Fact]
    public void DisablingTheGuardAfterRegistration_IsStillReported()
    {
        var sink = new ListLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddProvider(sink);
        });
        services.AddHttpResilience(Configuration(Settings.Enabled()));
        services.AddHttpClient("orders").AddHttpResilience();
        services.Configure<HttpResilience.NET.Options.HttpResilienceOptions>(
            "orders", options => options.Retry.DisableForUnsafeHttpMethods = false);

        using ServiceProvider provider = services.BuildServiceProvider();
        foreach (IStartupValidator validator in provider.GetServices<IStartupValidator>())
        {
            validator.Validate();
        }

        Assert.Single(Repeatable(sink));
    }

    // ---------------------------------------------------------------------------------------------------
    // HR-06  Two written statements about repeating mutating requests, one of which is not in force.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// An allow-list wins outright in the pipeline, so the flag beside it is bound and never read.
    /// </summary>
    /// <remarks>
    /// The same class of inert configuration as an <c>Authorities</c> list under <c>Mode: None</c> or
    /// <c>Retry:*</c> keys on a hedged client, both of which already fail startup. The dangerous direction is
    /// identical: an author has recorded a decision about duplicating mutating requests and it is not the
    /// decision in force.
    /// </remarks>
    [Fact]
    public void AnAllowListBesideTheDisabledGuard_FailsAtRegistration()
    {
        var services = new ServiceCollection();
        services.AddHttpResilience(Configuration(Settings.Enabled()
            .ForClient("orders", "Retry:RetryableMethods:0", "POST")
            .ForClient("orders", "Retry:DisableForUnsafeHttpMethods", "false")));

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(
            () => services.AddHttpClient("orders").AddHttpResilience());

        string message = string.Join(" ", exception.Failures);
        Assert.Contains("Retry.DisableForUnsafeHttpMethods", message, StringComparison.Ordinal);
        Assert.Contains("bound and never read", message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------------
    // HR-07  The backstop stops being per authority when a rate limiter displaces it.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Under <c>ByAuthority</c> the backstop is one limiter per authority -- unless a rate limiter has taken
    /// the handler's limiter slot, which moves the backstop into a handler of its own, outside the
    /// per-authority pipelines and therefore one per client.
    /// </summary>
    /// <remarks>
    /// The counterpart to <c>Backstop_IsPerAuthority_UnderByAuthoritySelection</c>, which asserts the same
    /// configuration <i>without</i> a rate limiter and gets two concurrent requests rather than one. Three
    /// documents said the bound was <c>(N + 1) x Backstop</c> under this mode without qualification; it is
    /// <c>1 x Backstop</c> here, which is tighter than documented and therefore the sort of error that shows
    /// up as backstop rejections on a client whose numbers were computed correctly from the README.
    /// </remarks>
    [Fact]
    public async Task Backstop_IsPerClientNotPerAuthority_WhenARateLimiterHasDisplacedIt()
    {
        var gate = new TaskCompletionSource();
        var origin = new RecordingHandler(async (request, _, cancellationToken) =>
        {
            await gate.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };
        });

        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled()
                .Set("Retry:Enabled", "false")
                .Set("ConcurrencyLimiter:Backstop", "1")
                .Set("RateLimiter:Enabled", "true")
                .Set("RateLimiter:PermitLimit", "100")
                .Set("RateLimiter:Window", "00:01:00")
                .Set("PipelineSelection:Mode", "ByAuthority")
                .Set("PipelineSelection:Authorities:0", "http://a.test")
                .Set("PipelineSelection:Authorities:1", "http://b.test"),
            origin);

        Task<HttpResponseMessage> held = harness.GetAsync("http://a.test/x");
        while (origin.Count < 1)
        {
            await Task.Yield();
        }

        // A different authority, and still rejected: the backstop is outside the per-authority pipelines.
        await Assert.ThrowsAsync<RateLimiterRejectedException>(() => harness.GetAsync("http://b.test/x"));

        gate.SetResult();
        (await held).Dispose();

        Assert.Equal(1, origin.MaxConcurrent);
    }

    // ---------------------------------------------------------------------------------------------------
    // HR-04  A stalled response body is bounded, and invisible to the breaker. Deliberately.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// An origin that answers headers promptly and then stops sending the body degrades every call to the
    /// full <c>Timeout:Client</c> budget without opening the circuit.
    /// </summary>
    /// <remarks>
    /// This pins documented behavior rather than guarding against a regression in it: <c>Timeout:Client</c>
    /// fires as caller cancellation, and caller cancellation must not count as a dependency failure or a
    /// cancelled request would open a circuit. The consequence is worth a test because it is the one failure
    /// mode where every resilience signal stays green while the service is degraded -- see
    /// <c>docs/OPERATIONS.md</c>. If a future change starts counting these, this test fails and the
    /// documentation has to change with it.
    /// <para>
    /// The contrast is <c>CircuitBreakerTests.Opens_AfterTheFailureRatioIsExceeded_AndThenFailsFast</c>: the
    /// same thresholds, with the failure arriving before headers, do open the breaker after two attempts.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AStalledResponseBody_IsBoundedByTheClientTimeout_AndNeverOpensTheCircuit()
    {
        var origin = new RecordingHandler((request, _, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StalledContent()
            }));

        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled()
                .Set("Retry:Enabled", "false")
                .Set("Timeout:Attempt", "00:00:00.200")
                .Set("Timeout:Total", "00:00:00.500")
                .Set("Timeout:Client", "00:00:01")
                .Set("CircuitBreaker:MinimumThroughput", "2")
                .Set("CircuitBreaker:FailureRatio", "0.1")
                .Set("CircuitBreaker:SamplingDuration", "00:00:30")
                .Set("CircuitBreaker:BreakDuration", "00:00:30"),
            origin);

        for (int i = 0; i < 4; i++)
        {
            await Assert.ThrowsAsync<TaskCanceledException>(() => harness.GetAsync());
        }

        // Four requests, every one of them degraded to the full client budget, and the origin saw four
        // attempts -- so nothing was retried and nothing was rejected either.
        Assert.Equal(4, origin.Count);

        // Well past MinimumThroughput at a 10% failure ratio, and the breaker has observed no failure at
        // all: the handler chain returned successfully with headers each time.
        Assert.Empty(HealthState.NotClosed(harness.Services));
        Assert.Equal(HealthStatus.Healthy, HealthState.Status(harness.Services));
    }

    /// <summary>Sends response headers immediately and then never finishes the body.</summary>
    private sealed class StalledContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            await stream.WriteAsync(new byte[] { 1 }, cancellationToken);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    // ---------------------------------------------------------------------------------------------------
    // HR-05  A server-supplied Retry-After can spend the whole budget, and is still bounded by it.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>Retry-After</c> replaces the computed delay, so a schedule the budget validator approved can still
    /// be truncated by the origin -- but only truncated. <c>Timeout:Total</c> is what stops it.
    /// </summary>
    /// <remarks>
    /// The validator's message used to promise that an approved schedule would run; it now says this
    /// explicitly. Fails if <c>Timeout:Total</c> stops wrapping the retry loop, in which case an origin
    /// naming an hour would hold the caller for an hour.
    /// </remarks>
    [Fact]
    public async Task ARetryAfterLongerThanTheTotalBudget_IsBoundedByTheTotalTimeout()
    {
        var origin = new RecordingHandler((request, _, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { RequestMessage = request };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(1));
            return Task.FromResult(response);
        });

        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled()
                .Set("Timeout:Attempt", "00:00:00.500")
                .Set("Timeout:Total", "00:00:02")
                .Set("CircuitBreaker:SamplingDuration", "00:00:30"),
            origin);

        long start = Environment.TickCount64;
        await Assert.ThrowsAsync<TimeoutRejectedException>(() => harness.GetAsync());
        long elapsed = Environment.TickCount64 - start;

        // Bounded by Timeout:Total, not by the hour the origin asked for.
        Assert.True(elapsed < 10_000, $"The total timeout should have bounded the wait, took {elapsed}ms.");

        // And the configured retries never ran: the first wait consumed the whole budget.
        Assert.Equal(1, origin.Count);
    }

    // ---------------------------------------------------------------------------------------------------
    // HR-13  The circuit breaker reach notice must not divide by a retry count the pipeline never runs.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// A hedged client's breaker-reach line quotes the caller-request rate as equal to the attempt rate,
    /// because the hedging pipeline has no retry strategy.
    /// </summary>
    /// <remarks>
    /// The notice divided the attempt rate by <c>Retry:MaxRetries + 1</c> unconditionally. A hedged client's
    /// own section cannot state <c>Retry:*</c> -- <c>CollectInertConfiguration</c> refuses it -- but the
    /// <b>root</b> is inherited by every client and its defaults are <c>Retry:Enabled: true</c> with
    /// <c>MaxRetries: 2</c>, so every hedged client in the fleet reported a third of the traffic its breaker
    /// actually needs.
    /// <para>
    /// Under-reporting is the harmful direction. Event 11 exists to hand an operator a number to check
    /// against known traffic, and a client that cannot open its breaker at 3.3 attempts per second looks as
    /// though it can at 1.1 -- so the reader concludes the breaker is engaged when it is inert, which is the
    /// exact state this notice was added to expose.
    /// </para>
    /// <para>
    /// Fails if <c>CircuitBreakerReachNotice.AttemptsPerRequest</c> goes back to reading <c>Retry</c> for
    /// every pipeline: the two rates diverge and the caller-request figure reads 1.1 instead of 3.3.
    /// </para>
    /// </remarks>
    [Fact]
    public void AHedgedClientsBreakerReach_IsNotDividedByAnInheritedRetryCount()
    {
        // 100 attempts over 30s is 3.3 per second. The root states the retry defaults explicitly, which is
        // what a hedged client inherits and what the notice used to divide by.
        string[] lines = BreakerReachFor(
            Settings.Hedged()
                .Set("CircuitBreaker:MinimumThroughput", "100")
                .Set("CircuitBreaker:SamplingDuration", "00:00:30")
                .Set("Retry:Enabled", "true")
                .Set("Retry:MaxRetries", "2"),
            hedged: true);

        string line = Assert.Single(lines);
        Assert.Contains("needs 3.3 failing attempts per second", line, StringComparison.Ordinal);
        Assert.Contains("about 3.3 failing caller requests per second", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// The standard pipeline still divides, because there the breaker really does sit inside the retry loop.
    /// </summary>
    /// <remarks>
    /// The other half of the same fix: making the hedging figure right must not flatten the standard one,
    /// which is correct and is the reason the division exists.
    /// </remarks>
    [Fact]
    public void AStandardClientsBreakerReach_IsStillDividedByItsRetryCount()
    {
        string[] lines = BreakerReachFor(
            Settings.Enabled()
                .Set("CircuitBreaker:MinimumThroughput", "100")
                .Set("CircuitBreaker:SamplingDuration", "00:00:30")
                .Set("Retry:Enabled", "true")
                .Set("Retry:MaxRetries", "2"),
            hedged: false);

        string line = Assert.Single(lines);
        Assert.Contains("needs 3.3 failing attempts per second", line, StringComparison.Ordinal);
        Assert.Contains("about 1.1 failing caller requests per second", line, StringComparison.Ordinal);
    }

    private static string[] BreakerReachFor(Settings settings, bool hedged)
    {
        var sink = new ListLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddProvider(sink);
        });
        services.AddHttpResilience(Configuration(settings));

        IHttpClientBuilder builder = services.AddHttpClient("orders");
        _ = hedged ? builder.AddHedgedHttpResilience() : builder.AddHttpResilience();

        using ServiceProvider provider = services.BuildServiceProvider();
        foreach (IStartupValidator validator in provider.GetServices<IStartupValidator>())
        {
            validator.Validate();
        }

        return [.. sink.Records.Where(record =>
            record.Contains("circuit breaker for client", StringComparison.Ordinal))];
    }

    // ---------------------------------------------------------------------------------------------------

    private static string[] NoticesFor(
        Settings settings,
        Action<HttpResilience.NET.Options.HttpResilienceOptions>? configure = null)
    {
        var sink = new ListLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddProvider(sink);
        });
        services.AddHttpResilience(Configuration(settings));
        services.AddHttpClient("orders").AddHttpResilience(configure: configure);

        using ServiceProvider provider = services.BuildServiceProvider();
        foreach (IStartupValidator validator in provider.GetServices<IStartupValidator>())
        {
            validator.Validate();
        }

        return Repeatable(sink);
    }

    private static string[] Repeatable(ListLoggerProvider sink) =>
        [.. sink.Records.Where(record =>
            record.Contains("will repeat unsafe HTTP methods", StringComparison.Ordinal))];
}
