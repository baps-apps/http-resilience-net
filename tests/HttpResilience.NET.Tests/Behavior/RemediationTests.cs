using System.Diagnostics.Metrics;
using System.Net;
using HttpResilience.NET.Options;
using HttpResilience.NET.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;

namespace HttpResilience.NET.Tests.Behavior;

/// <summary>
/// Every defect found by the second architecture review, pinned by the behavior that exposed it.
/// </summary>
/// <remarks>
/// All of these were reproduced by execution rather than inspection, because each lives in a seam between
/// this package and the platform: registration order, the hedging handler's own pipeline registry, the
/// options pipeline, and the metric names Polly actually publishes.
/// </remarks>
public class RemediationTests
{
    private static IConfigurationRoot Configuration(Settings settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings.Build()).Build();

    /// <summary>
    /// Two pipelines nest rather than merge, so retries multiply: three attempts each became nine origin
    /// calls, with nothing thrown and nothing logged.
    /// </summary>
    [Fact]
    public void RegisteringResilienceTwice_FailsWithAnActionableMessage()
    {
        var services = new ServiceCollection();
        services.AddHttpResilience(Configuration(Settings.Enabled()));

        IHttpClientBuilder builder = services.AddHttpClient("orders").AddHttpResilience();

        InvalidOperationException exception =
            Assert.Throws<InvalidOperationException>(() => builder.AddHttpResilience());

        Assert.Contains("already configured", exception.Message, StringComparison.Ordinal);
        Assert.Contains("orders", exception.Message, StringComparison.Ordinal);
        Assert.Contains("AddResilienceHandler", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RegisteringResilienceTwice_AcrossSeparateBuilders_AlsoFails()
    {
        var services = new ServiceCollection();
        services.AddHttpResilience(Configuration(Settings.Enabled()));

        // The realistic shape: a shared registration extension adds resilience, and the application adds it
        // again on its own builder for the same client name.
        services.AddHttpClient("orders").AddHttpResilience();

        Assert.Throws<InvalidOperationException>(() => services.AddHttpClient("orders").AddHttpResilience());
    }

    [Fact]
    public async Task ClientsWithDistinctNames_AreUnaffectedByTheGuard()
    {
        var services = new ServiceCollection();
        services.AddHttpResilience(Configuration(Settings.Enabled()));

        var origin = new RecordingHandler();
        services.AddHttpClient("a").AddHttpResilience().ConfigurePrimaryHttpMessageHandler(() => origin);
        services.AddHttpClient("b").AddHttpResilience().ConfigurePrimaryHttpMessageHandler(() => origin);

        await using ServiceProvider provider = services.BuildServiceProvider();
        IHttpClientFactory factory = provider.GetRequiredService<IHttpClientFactory>();

        await factory.CreateClient("a").GetAsync("http://origin.test/x");
        Assert.Equal(3, origin.Count);
    }

    /// <summary>
    /// A client reads the section named after it. Repeating the name was the only way to pick up an override
    /// section, and forgetting it left the client silently on root defaults.
    /// </summary>
    [Fact]
    public async Task SectionName_DefaultsToTheClientName()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled()
                .Set("Retry:MaxRetries", "2")
                .ForClient("orders", "Retry:MaxRetries", "4")
                .ForClient("orders", "Timeout:Total", "00:01:00"),
            clientName: "orders");

        await harness.GetAsync();

        Assert.Equal(5, harness.Origin.Count);
    }

    [Fact]
    public async Task ExplicitEmptySectionName_UsesRootValuesOnly()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled()
                .Set("Retry:MaxRetries", "2")
                .ForClient("orders", "Retry:MaxRetries", "4")
                .ForClient("orders", "Timeout:Total", "00:01:00"),
            clientName: "orders",
            sectionName: string.Empty);

        await harness.GetAsync();

        Assert.Equal(3, harness.Origin.Count);
    }

    /// <summary>
    /// What the options report and what the pipeline runs are the same number, whoever set it.
    /// </summary>
    /// <remarks>
    /// This used to be held by comparing a registration snapshot against the registered options and failing
    /// startup on any difference, which cost a hand-maintained mirror of the whole options graph and refused
    /// a standard extension point. It is now held by construction: the pipeline reads
    /// <see cref="IOptionsMonitor{TOptions}"/> when it is built, which is after every <c>Configure</c> and
    /// <c>PostConfigure</c>, so the values it runs on are the object a consumer reads back.
    /// <para>
    /// Fails if the pipeline goes back to a captured snapshot -- the options would report one attempt while
    /// the origin saw three.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task PostRegistrationConfigure_ReachesTheRunningPipeline()
    {
        var services = new ServiceCollection();
        services.AddHttpResilience(Configuration(Settings.Enabled().Set("Retry:MaxRetries", "2")));

        var origin = new RecordingHandler();
        services.AddHttpClient("t").AddHttpResilience().ConfigurePrimaryHttpMessageHandler(() => origin);
        services.Configure<HttpResilienceOptions>("t", options => options.Retry.MaxRetries = 1);

        await using ServiceProvider provider = services.BuildServiceProvider();
        (await provider.GetRequiredService<IHttpClientFactory>().CreateClient("t")
            .GetAsync("http://origin.test/x")).Dispose();

        HttpResilienceOptions effective =
            provider.GetRequiredService<IOptionsMonitor<HttpResilienceOptions>>().Get("t");

        Assert.Equal(1, effective.Retry.MaxRetries);
        Assert.Equal(2, origin.Count);
    }

    /// <summary>
    /// A change to a setting that decides <b>which handlers exist</b> is still refused at startup, because
    /// that is the one kind a late change cannot reach.
    /// </summary>
    /// <remarks>
    /// Values compose the way the options pattern says they should. Handler composition cannot: a client's
    /// handler chain is built from the registrations made while the service collection was being built, so
    /// turning the rate limiter on here would be reported and not be in effect -- and worse, the pipeline
    /// would then resolve a keyed limiter that was never registered and fail on the first request instead of
    /// at startup. Fails if the structural comparison is removed.
    /// </remarks>
    [Fact]
    public void PostConfiguringHandlerComposition_StillFailsAtStartup()
    {
        var services = new ServiceCollection();
        services.AddHttpResilience(Configuration(Settings.Enabled()));

        services.AddHttpClient("t").AddHttpResilience();
        services.PostConfigure<HttpResilienceOptions>("t", options =>
        {
            options.RateLimiter.Enabled = true;
            options.RateLimiter.PermitLimit = 10;
        });

        using ServiceProvider provider = services.BuildServiceProvider();

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptionsMonitor<HttpResilienceOptions>>().Get("t"));

        string message = string.Join(" ", exception.Failures);
        Assert.Contains("RateLimiter:Enabled", message, StringComparison.Ordinal);
        Assert.Contains("which handlers", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The supported way to adjust values in code, and the one that does reach the pipeline.
    /// </summary>
    [Fact]
    public async Task ConfigureParameter_ReachesBothThePipelineAndTheOptions()
    {
        var services = new ServiceCollection();
        services.AddHttpResilience(Configuration(Settings.Enabled().Set("Retry:MaxRetries", "2")));

        var origin = new RecordingHandler();
        services.AddHttpClient("t")
            .AddHttpResilience(configure: options => options.Retry.MaxRetries = 1)
            .ConfigurePrimaryHttpMessageHandler(() => origin);

        await using ServiceProvider provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IHttpClientFactory>().CreateClient("t").GetAsync("http://origin.test/x");

        Assert.Equal(1, provider.GetRequiredService<IOptionsMonitor<HttpResilienceOptions>>()
            .Get("t").Retry.MaxRetries);
        Assert.Equal(2, origin.Count);
    }

    /// <summary>
    /// Registering the package and leaving it off produces the same state as forgetting the configuration
    /// key, so it must not be silent -- and it must be said at startup, at a level a deployment check sees.
    /// </summary>
    /// <remarks>
    /// Fails if the notice goes back to hanging off <c>ConfigureHttpClient</c>: no client is created here, so
    /// a notice that waits for one never fires. <c>Enabled</c> defaults to <see langword="false"/>, so the
    /// only thing standing between a service that forgot the key and a service with no resilience at all is
    /// this line appearing in the deployment's logs. At Information a production pipeline may well drop it;
    /// on first client use it may not appear for hours after the deploy that caused it.
    /// </remarks>
    [Fact]
    public void DisabledClient_WarnsAtStartup_BeforeAnyClientIsCreated()
    {
        var sink = new ListLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddProvider(sink);
        });
        services.AddHttpResilience(Configuration(Settings.Empty().Set("Enabled", "false")));
        services.AddHttpClient("orders").AddHttpResilience();

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Empty(Notices(sink));

        // Exactly what the host runs before it accepts traffic. No client is ever created.
        foreach (IStartupValidator validator in provider.GetServices<IStartupValidator>())
        {
            validator.Validate();
        }

        string[] notices = Notices(sink);
        Assert.Single(notices);
        Assert.StartsWith("[Warning]", notices[0], StringComparison.Ordinal);
        Assert.Contains("orders", notices[0], StringComparison.Ordinal);
        Assert.Contains("HttpResilience:Clients:orders:Enabled", notices[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// Once per client, however many times its options are read or its clients created.
    /// </summary>
    [Fact]
    public async Task DisabledClient_SaysSoOnce()
    {
        var sink = new ListLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddProvider(sink);
        });
        services.AddHttpResilience(Configuration(Settings.Empty().Set("Enabled", "false")));

        var origin = new RecordingHandler();
        services.AddHttpClient("orders").AddHttpResilience().ConfigurePrimaryHttpMessageHandler(() => origin);

        await using ServiceProvider provider = services.BuildServiceProvider();
        foreach (IStartupValidator validator in provider.GetServices<IStartupValidator>())
        {
            validator.Validate();
        }

        IHttpClientFactory factory = provider.GetRequiredService<IHttpClientFactory>();
        await factory.CreateClient("orders").GetAsync("http://origin.test/x");
        await factory.CreateClient("orders").GetAsync("http://origin.test/x");
        provider.GetRequiredService<IOptionsMonitor<HttpResilienceOptions>>().Get("orders");

        Assert.Single(Notices(sink));
    }

    /// <summary>
    /// A container with no logging registered must not fail because the notice had nowhere to write.
    /// </summary>
    [Fact]
    public void DisabledClient_WithoutLogging_StillStarts()
    {
        var services = new ServiceCollection();
        services.AddHttpResilience(Configuration(Settings.Empty().Set("Enabled", "false")));
        services.AddHttpClient("orders").AddHttpResilience();

        using ServiceProvider provider = services.BuildServiceProvider();

        foreach (IStartupValidator validator in provider.GetServices<IStartupValidator>())
        {
            validator.Validate();
        }
    }

    private static string[] Notices(ListLoggerProvider sink) =>
        [.. sink.Records.Where(record => record.Contains("registered but disabled", StringComparison.Ordinal))];

    /// <summary>
    /// The hedging handler keeps a circuit breaker, a limiter and a metric series per authority for the life
    /// of the process. Without an allow-list, a destination influenced by request data exhausts memory.
    /// </summary>
    [Fact]
    public void HedgedClient_WithoutAnAuthorityAllowList_FailsAtRegistration()
    {
        var services = new ServiceCollection();
        services.AddHttpResilience(Configuration(Settings.Enabled()));

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(
            () => services.AddHttpClient("search").AddHedgedHttpResilience());

        Assert.Contains("PipelineSelection.Authorities", exception.Message, StringComparison.Ordinal);
        Assert.Contains("AddHedgedHttpResilience", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HedgedClient_RejectsAnUnlistedAuthority_BeforeAnythingIsAllocated()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Hedged().Set("Hedging:Delay", "00:00:00"),
            hedged: true);

        // HttpRequestException, not InvalidOperationException: the condition is the request's own URI, so
        // callers that already wrap outbound calls in `catch (HttpRequestException)` see it as one more
        // failed request rather than as a programming error that escapes to the top of the process.
        HttpRequestException exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => harness.GetAsync("http://elsewhere.test/x"));

        Assert.Contains("PipelineSelection:Authorities", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, harness.Origin.Count);
    }

    /// <summary>
    /// Breakers on the hedging pipeline are per authority whatever the selection mode, so tracking them
    /// under one key let the last transition to fire overwrite the rest -- one host recovering masked
    /// another still open.
    /// </summary>
    [Fact]
    public async Task HedgedClient_TracksOneBreakerPerAuthority()
    {
        var origin = new RecordingHandler((request, _, _) => Task.FromResult(
            new HttpResponseMessage(request.RequestUri!.Host == "bad.test"
                ? HttpStatusCode.InternalServerError
                : HttpStatusCode.OK)
            { RequestMessage = request }));

        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled()
                .Set("Retry:Enabled", "false")
                .Set("Hedging:Delay", "00:00:00")
                .Set("Hedging:MaxHedgedAttempts", "1")
                .Set("CircuitBreaker:MinimumThroughput", "2")
                .Set("CircuitBreaker:FailureRatio", "0.1")
                .Set("CircuitBreaker:SamplingDuration", "00:00:30")
                .Set("CircuitBreaker:BreakDuration", "00:00:30")
                .Set("PipelineSelection:Authorities:0", "http://bad.test")
                .Set("PipelineSelection:Authorities:1", "http://good.test"),
            origin,
            hedged: true);

        for (int i = 0; i < 3; i++)
        {
            try
            {
                await harness.GetAsync("http://bad.test/x");
            }
            catch (BrokenCircuitException)
            {
                // Expected once its breaker opens. What matters is whose state is recorded.
            }
        }

        // The healthy host must not be able to write over the failing host's state.
        for (int i = 0; i < 3; i++)
        {
            await harness.GetAsync("http://good.test/x");
        }

        IReadOnlyList<string> notClosed = HealthState.NotClosed(harness.Services);

        Assert.Contains(notClosed, key => key.Contains("bad.test", StringComparison.Ordinal));
        Assert.DoesNotContain(notClosed, key => key.Contains(PipelineKeySelectorSharedKey, StringComparison.Ordinal));
    }

    /// <summary>
    /// The one tag this package adds, asserted on the instrument Polly actually publishes. The metric names
    /// in the operations guide were wrong, so every alert built from them was permanently silent -- and the
    /// tag itself was being added twice for response outcomes, because the platform already supplies it.
    /// </summary>
    [Theory]
    [InlineData(false, "503")]
    [InlineData(true, "System.Net.Http.HttpRequestException")]
    public async Task ErrorTypeTag_LandsExactlyOnce_OnThePollyInstrumentThatExists(
        bool originThrows,
        string expectedErrorType)
    {
        var instruments = new HashSet<string>(StringComparer.Ordinal);
        var errorTypes = new HashSet<string>(StringComparer.Ordinal);
        var duplicated = new List<string>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == HttpResilienceTelemetryExtensions.PollyMeterName)
            {
                lock (instruments)
                {
                    instruments.Add(instrument.Name);
                }

                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<int>((instrument, _, tags, _) =>
        {
            int seen = 0;
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                if (tag.Key != "error.type" || tag.Value is not string value)
                {
                    continue;
                }

                seen++;
                lock (errorTypes)
                {
                    errorTypes.Add($"{instrument.Name}|{value}");
                }
            }

            if (seen > 1)
            {
                lock (duplicated)
                {
                    duplicated.Add($"{instrument.Name} carried error.type {seen} times");
                }
            }
        });
        listener.Start();

        var services = new ServiceCollection();
        services.AddHttpResilience(Configuration(Settings.Enabled()));
        services.AddHttpResilienceTelemetry();
        RecordingHandler origin = originThrows
            ? new RecordingHandler((_, _, _) =>
                Task.FromException<HttpResponseMessage>(new HttpRequestException("connection refused")))
            : new RecordingHandler(HttpStatusCode.ServiceUnavailable);

        services.AddHttpClient("t").AddHttpResilience().ConfigurePrimaryHttpMessageHandler(() => origin);

        await using (ServiceProvider provider = services.BuildServiceProvider())
        {
            try
            {
                await provider.GetRequiredService<IHttpClientFactory>().CreateClient("t")
                    .GetAsync("http://origin.test/x");
            }
            catch (HttpRequestException)
            {
                // The exhausted-retries outcome for the exception case; the measurements are the assertion.
            }
        }

        listener.Dispose();

        Assert.Contains("resilience.polly.strategy.events", instruments);
        Assert.Contains("resilience.polly.pipeline.duration", instruments);
        Assert.Contains("resilience.polly.strategy.attempt.duration", instruments);
        Assert.Contains($"resilience.polly.strategy.events|{expectedErrorType}", errorTypes);

        // The platform tags response outcomes itself, so adding the key here too put it on twice.
        Assert.Empty(duplicated);
    }

    /// <summary>
    /// The root section is not a pipeline. Holding it to the standard pipeline's retry-budget rule failed
    /// startup for applications whose clients are all hedged and never run a retry at all.
    /// </summary>
    [Fact]
    public async Task RootSection_IsNotHeldToTheStandardRetryBudget()
    {
        // 3 attempts of 2s plus 1.5s of backoff needs 7.5s, which does not fit the 6s root budget -- but no
        // client on this application uses the retry strategy.
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled()
                .Set("Timeout:Total", "00:00:06")
                .Set("Timeout:Attempt", "00:00:02")
                .Set("Retry:MaxRetries", "2")
                .Set("Retry:BaseDelay", "00:00:00.500")
                .Set("Retry:BackoffType", "Exponential")
                .Set("CircuitBreaker:SamplingDuration", "00:00:30")
                .Set("Hedging:Delay", "00:00:00.500")
                .Set("PipelineSelection:Authorities:0", "http://origin.test"),
            hedged: true);

        HttpResponseMessage response = await harness.GetAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    private const string PipelineKeySelectorSharedKey = "shared";
}

/// <summary>
/// Captures formatted log records, so a test can assert what an operator would see.
/// </summary>
internal sealed class ListLoggerProvider : ILoggerProvider
{
    private readonly List<string> _records = [];

    public IReadOnlyList<string> Records
    {
        get
        {
            lock (_records)
            {
                return [.. _records];
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new Recorder(this, categoryName);

    public void Dispose()
    {
    }

    private void Add(string record)
    {
        lock (_records)
        {
            _records.Add(record);
        }
    }

    private sealed class Recorder(ListLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            provider.Add($"[{logLevel}] {category}: {formatter(state, exception)}");
    }
}
