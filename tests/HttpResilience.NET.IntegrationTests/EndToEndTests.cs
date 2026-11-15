using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;

namespace HttpResilience.NET.IntegrationTests;

/// <summary>
/// End-to-end coverage against a real server, exercising the documented registration exactly as a consumer
/// would write it.
/// </summary>
public class EndToEndTests
{
    private static ServiceProvider BuildProvider(
        TestServerFixture server, Dictionary<string, string?> overrides, bool hedged = false)
    {
        var settings = new Dictionary<string, string?>
        {
            ["HttpResilience:Enabled"] = "true",
            ["HttpResilience:Timeout:Total"] = "00:00:30",
            ["HttpResilience:Timeout:Attempt"] = "00:00:10",
            ["HttpResilience:Retry:MaxRetries"] = "2",
            ["HttpResilience:Retry:BaseDelay"] = "00:00:00",
            ["HttpResilience:Retry:BackoffType"] = "Constant",
            ["HttpResilience:CircuitBreaker:MinimumThroughput"] = "1000"
        };

        foreach ((string key, string? value) in overrides)
        {
            settings[key] = value;
        }

        IConfigurationRoot configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddHttpResilience(configuration);
        services.AddHttpResilienceTelemetry();

        IHttpClientBuilder builder = services.AddHttpClient("api");
        _ = hedged ? builder.AddHttpResilienceHedgingHandler() : builder.AddHttpResilienceHandler();
        builder.ConfigurePrimaryHttpMessageHandler(server.CreateHandler);

        return services.BuildServiceProvider();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task SuccessfulRequest_PassesThrough()
    {
        await using TestServerFixture server = await TestServerFixture.StartAsync();
        await using ServiceProvider provider = BuildProvider(server, []);
        HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("api");

        HttpResponseMessage response = await client.GetAsync($"{server.BaseAddress}ok", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task TransientFailures_AreRetriedAgainstARealServer()
    {
        await using TestServerFixture server = await TestServerFixture.StartAsync();
        await using ServiceProvider provider = BuildProvider(server, []);
        HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("api");

        HttpResponseMessage response = await client.GetAsync($"{server.BaseAddress}flaky", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// The end-to-end version of the guarantee that matters most: a real POST, over real HTTP, arrives once.
    /// </summary>
    /// <remarks>
    /// Fails if POST is treated as a safe method: the 500 would be retried and the server would count three
    /// deliveries.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task PostToAFailingEndpoint_IsDeliveredOnce()
    {
        await using TestServerFixture server = await TestServerFixture.StartAsync();
        await using ServiceProvider provider = BuildProvider(server, []);
        HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("api");

        using var content = new StringContent("""{"amount":100}""", Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await client.PostAsync($"{server.BaseAddress}orders", content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(1, server.OrderDeliveries);
        Assert.Equal(["""{"amount":100}"""], server.OrderBodies);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ParallelRequests_AllSucceed()
    {
        await using TestServerFixture server = await TestServerFixture.StartAsync();
        await using ServiceProvider provider = BuildProvider(server, []);
        IHttpClientFactory factory = provider.GetRequiredService<IHttpClientFactory>();

        HttpResponseMessage[] responses = await Task.WhenAll(
            Enumerable.Range(0, 50).Select(_ => factory.CreateClient("api").GetAsync($"{server.BaseAddress}ok")));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
    }

    /// <summary>
    /// The concurrency cap holds over real HTTP: while the server holds every request, it sees exactly the
    /// limit in flight and the rest wait in the limiter's queue; released, all of them complete.
    /// </summary>
    /// <remarks>Fails without the limiter: all 50 reach the server at once.</remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ParallelRequests_UnderAConcurrencyCap_AreHeldToTheCap_AndAllComplete()
    {
        await using TestServerFixture server = await TestServerFixture.StartAsync();
        await using ServiceProvider provider = BuildProvider(server, new Dictionary<string, string?>
        {
            ["HttpResilience:ConcurrencyLimiter:Enabled"] = "true",
            ["HttpResilience:ConcurrencyLimiter:Limit"] = "4",
            ["HttpResilience:ConcurrencyLimiter:QueueLimit"] = "100"
        });
        IHttpClientFactory factory = provider.GetRequiredService<IHttpClientFactory>();
        CancellationToken token = TestContext.Current.CancellationToken;

        Task<HttpResponseMessage>[] requests =
            [.. Enumerable.Range(0, 50).Select(_ => factory.CreateClient("api").GetAsync($"{server.BaseAddress}held", token))];

        try
        {
            // Every request is either held at the server or queued by the limiter, and stays there until released.
            await Eventually.UntilAsync(
                () => server.HeldInFlight + LimiterGauges.Queued(provider) == 50,
                () => $"{server.HeldInFlight} held at the server and {LimiterGauges.Queued(provider)} queued, of 50");

            Assert.Equal(4, server.MaxHeldInFlight);
        }
        finally
        {
            // Released on failure too, or the unanswered requests outlive the test.
            server.ReleaseHeld();
        }

        HttpResponseMessage[] responses = await Task.WhenAll(requests);
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(4, server.MaxHeldInFlight);
    }

    /// <summary>
    /// The rate limiter holds requests beyond its permits in its queue instead of sending them, and every
    /// queued request still completes once permits replenish.
    /// </summary>
    /// <remarks>Fails without the limiter: nothing is ever queued.</remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ParallelRequests_UnderARateLimit_AreQueued_AndAllComplete()
    {
        await using TestServerFixture server = await TestServerFixture.StartAsync();
        await using ServiceProvider provider = BuildProvider(server, new Dictionary<string, string?>
        {
            ["HttpResilience:RateLimiter:Enabled"] = "true",
            ["HttpResilience:RateLimiter:PermitLimit"] = "10",
            ["HttpResilience:RateLimiter:Window"] = "00:00:01",
            ["HttpResilience:RateLimiter:QueueLimit"] = "100"
        });
        IHttpClientFactory factory = provider.GetRequiredService<IHttpClientFactory>();
        CancellationToken token = TestContext.Current.CancellationToken;

        Task<HttpResponseMessage>[] requests =
            [.. Enumerable.Range(0, 30).Select(_ => factory.CreateClient("api").GetAsync($"{server.BaseAddress}ok", token))];

        // 30 requests against 10 permits a second: at least 10 wait in the queue for a full window.
        await Eventually.UntilAsync(
            () => LimiterGauges.Queued(provider) > 0,
            () => "the rate limiter to queue requests beyond its 10 permits");

        HttpResponseMessage[] responses = await Task.WhenAll(requests);
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
    }

    /// <summary>
    /// A caller's cancellation reaches the server: the in-flight request is aborted there, not left running.
    /// </summary>
    /// <remarks>
    /// Cancels only after the request has arrived, so the server side is what is asserted. Fails if the
    /// pipeline swallows the caller's token, for example by sending each attempt with its own.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task CallerCancellation_PropagatesToTheServer()
    {
        await using TestServerFixture server = await TestServerFixture.StartAsync();
        await using ServiceProvider provider = BuildProvider(server, []);
        HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("api");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Task<HttpResponseMessage> request = client.GetAsync($"{server.BaseAddress}slow", cts.Token);

        await server.SlowArrived.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await server.SlowAborted.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The guarantee that matters most, over real HTTP: a hedged client racing a <b>slow</b> mutating
    /// endpoint must still deliver the request once. The stub-handler version of this test proves the
    /// pipeline; this one proves the body actually crossed a wire only once.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task HedgedClient_DoesNotDuplicateASlowPost()
    {
        await using TestServerFixture server = await TestServerFixture.StartAsync();
        await using ServiceProvider provider = BuildProvider(server, new Dictionary<string, string?>
        {
            ["HttpResilience:Hedging:Delay"] = "00:00:00.200",
            ["HttpResilience:Hedging:MaxHedgedAttempts"] = "3",
            ["HttpResilience:PipelineSelection:Authorities:0"] = new Uri(server.BaseAddress).GetLeftPart(UriPartial.Authority)
        }, hedged: true);

        HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("api");

        using var content = new StringContent("""{"amount":100}""", Encoding.UTF8, "application/json");
        HttpResponseMessage response = await client.PostAsync($"{server.BaseAddress}slow-orders", content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, server.SlowOrderDeliveries);
        Assert.Equal(["""{"amount":100}"""], server.SlowOrderBodies);
    }

    /// <summary>
    /// The unsafe-method guard must not stop hedging the requests hedging exists for: a slow GET.
    /// </summary>
    /// <remarks>
    /// The first attempt stalls for 5 seconds; the hedge starts after 200 ms and answers at once. Fails if GET
    /// is not hedged: the caller would wait for the stalled attempt and read "primary".
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task HedgedClient_StillRacesASlowGet()
    {
        await using TestServerFixture server = await TestServerFixture.StartAsync();
        await using ServiceProvider provider = BuildProvider(server, new Dictionary<string, string?>
        {
            ["HttpResilience:Hedging:Delay"] = "00:00:00.200",
            ["HttpResilience:Hedging:MaxHedgedAttempts"] = "2",
            ["HttpResilience:PipelineSelection:Authorities:0"] = new Uri(server.BaseAddress).GetLeftPart(UriPartial.Authority)
        }, hedged: true);

        HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("api");

        using HttpResponseMessage response = await client.GetAsync($"{server.BaseAddress}slow-first", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("hedge", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.True(server.SlowFirstArrivals >= 2, $"expected a hedged attempt; the server saw {server.SlowFirstArrivals}");
    }

    /// <summary>
    /// A real host emits the disabled-client warning while starting, before it serves anything.
    /// </summary>
    /// <remarks>
    /// The unit tests run the startup steps by hand. This runs an actual <c>IHost</c> and asserts the warning
    /// is present the moment <c>StartAsync</c> returns. Fails if the host stops calling
    /// <c>IHostedLifecycleService.StartingAsync</c>, which the startup report rests on.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ARealHost_LogsTheDisabledClientWarning_WhileStarting()
    {
        var sink = new ListSink();

        IHost host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.SetMinimumLevel(LogLevel.Trace);
                logging.AddProvider(sink);
            })
            .ConfigureServices(services =>
            {
                IConfigurationRoot configuration = new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["HttpResilience:Enabled"] = "false"
                    })
                    .Build();

                services.AddHttpResilience(configuration);
                services.AddHttpClient("orders").AddHttpResilienceHandler();
            })
            .Build();

        Assert.Empty(sink.Matching("is disabled for client"));

        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            string[] warnings = sink.Matching("is disabled for client");

            Assert.Single(warnings);
            Assert.StartsWith("[Warning]", warnings[0], StringComparison.Ordinal);
            Assert.Contains("'HttpResilience:Enabled'", warnings[0], StringComparison.Ordinal);
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
            host.Dispose();
        }
    }

    /// <summary>
    /// A real host validates options before the startup report runs: invalid configuration fails
    /// <c>StartAsync</c> with no client notice logged first.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ARealHost_WithInvalidConfiguration_FailsBeforeAnyNotice()
    {
        var sink = new ListSink();

        using IHost host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.SetMinimumLevel(LogLevel.Trace);
                logging.AddProvider(sink);
            })
            .ConfigureServices(services =>
            {
                IConfigurationRoot configuration = new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["HttpResilience:Clients:orders:Retry:RetryableMethods:0"] = "POST",
                        ["HttpResilience:Clients:orders:Retry:MaxRetries"] = "50"
                    })
                    .Build();

                services.AddHttpResilience(configuration);
                services.AddHttpClient("orders").AddHttpResilienceHandler();
            })
            .Build();

        // The platform validates its own options at start too, and they read these, so the failure may arrive
        // wrapped in an AggregateException.
        Exception failure = await Assert.ThrowsAnyAsync<Exception>(() => host.StartAsync(TestContext.Current.CancellationToken));
        Assert.Contains("Retry:MaxRetries", failure.Message, StringComparison.Ordinal);

        Assert.Empty(sink.Matching("HttpResilience.NET.Configuration"));
    }

    private sealed class ListSink : ILoggerProvider
    {
        private readonly List<string> _records = [];

        public string[] Matching(string fragment)
        {
            lock (_records)
            {
                return [.. _records.Where(record => record.Contains(fragment, StringComparison.Ordinal))];
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

        private sealed class Recorder(ListSink sink, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                sink.Add($"[{logLevel}] {category}: {formatter(state, exception)}");
        }
    }
}
