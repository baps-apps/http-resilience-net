using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
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
        services.AddHttpResilienceHealthChecks();

        IHttpClientBuilder builder = services.AddHttpClient("api");
        _ = hedged ? builder.AddHedgedHttpResilience() : builder.AddHttpResilience();
        builder.ConfigurePrimaryHttpMessageHandler(server.CreateHandler);

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task SuccessfulRequest_PassesThrough()
    {
        await using TestServerFixture server = await TestServerFixture.StartAsync();
        await using ServiceProvider provider = BuildProvider(server, []);
        HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("api");

        HttpResponseMessage response = await client.GetAsync($"{server.BaseAddress}ok");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HealthStatus.Healthy,
            (await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync()).Status);
    }

    [Fact]
    public async Task TransientFailures_AreRetriedAgainstARealServer()
    {
        await using TestServerFixture server = await TestServerFixture.StartAsync();
        await using ServiceProvider provider = BuildProvider(server, []);
        HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("api");

        HttpResponseMessage response = await client.GetAsync($"{server.BaseAddress}flaky");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// The end-to-end version of the guarantee that matters most: a real POST, over real HTTP, arrives once.
    /// </summary>
    [Fact]
    public async Task PostToAFailingEndpoint_IsDeliveredOnce()
    {
        await using TestServerFixture server = await TestServerFixture.StartAsync();
        await using ServiceProvider provider = BuildProvider(server, []);
        HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("api");

        using var content = new StringContent("""{"amount":100}""", Encoding.UTF8, "application/json");
        HttpResponseMessage response = await client.PostAsync($"{server.BaseAddress}orders", content);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task ParallelRequests_AllSucceed()
    {
        await using TestServerFixture server = await TestServerFixture.StartAsync();
        await using ServiceProvider provider = BuildProvider(server, []);
        IHttpClientFactory factory = provider.GetRequiredService<IHttpClientFactory>();

        HttpResponseMessage[] responses = await Task.WhenAll(
            Enumerable.Range(0, 50).Select(_ => factory.CreateClient("api").GetAsync($"{server.BaseAddress}ok")));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
    }

    [Fact]
    public async Task ParallelRequests_UnderAConcurrencyCap_DoNotDeadlock()
    {
        await using TestServerFixture server = await TestServerFixture.StartAsync();
        await using ServiceProvider provider = BuildProvider(server, new Dictionary<string, string?>
        {
            ["HttpResilience:ConcurrencyLimiter:Enabled"] = "true",
            ["HttpResilience:ConcurrencyLimiter:Limit"] = "4",
            ["HttpResilience:ConcurrencyLimiter:QueueLimit"] = "100"
        });
        IHttpClientFactory factory = provider.GetRequiredService<IHttpClientFactory>();

        HttpResponseMessage[] responses = await Task.WhenAll(
            Enumerable.Range(0, 50).Select(_ => factory.CreateClient("api").GetAsync($"{server.BaseAddress}ok")));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
    }

    [Fact]
    public async Task ParallelRequests_UnderARateLimit_DoNotDeadlock()
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

        HttpResponseMessage[] responses = await Task.WhenAll(
            Enumerable.Range(0, 30).Select(_ => factory.CreateClient("api").GetAsync($"{server.BaseAddress}ok")));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
    }

    [Fact]
    public async Task CallerCancellation_PropagatesToTheServer()
    {
        await using TestServerFixture server = await TestServerFixture.StartAsync();
        await using ServiceProvider provider = BuildProvider(server, []);
        HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("api");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetAsync($"{server.BaseAddress}slow", cts.Token));
    }

    /// <summary>
    /// The guarantee that matters most, over real HTTP: a hedged client racing a <b>slow</b> mutating
    /// endpoint must still deliver the request once. The stub-handler version of this test proves the
    /// pipeline; this one proves the body actually crossed a wire only once.
    /// </summary>
    [Fact]
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
        HttpResponseMessage response = await client.PostAsync($"{server.BaseAddress}slow-orders", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, server.SlowOrderDeliveries);
        Assert.Equal(["""{"amount":100}"""], server.SlowOrderBodies);
    }

    [Fact]
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

        HttpResponseMessage response = await client.GetAsync($"{server.BaseAddress}ok");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// A retried request re-sends the same <see cref="HttpRequestMessage"/>, so its content has to be
    /// replayable. A buffered content type is; a single-pass stream is not.
    /// </summary>
    /// <remarks>
    /// This is a characterization test, and it is the only way to see the behavior at all: the unit harness
    /// reads every request body itself, which buffers the content before the origin sees it, so a stream that
    /// could only be read once looks identical to one that could. Against a real server the second attempt
    /// shows what actually reaches the endpoint. It pins whatever the platform does today so a package
    /// upgrade cannot change it unnoticed, and it is the evidence behind the documented rule that an opted-in
    /// retry needs replayable content.
    /// </remarks>
    [Fact]
    public async Task RetryingANonSeekableStreamBody_IsPinnedByWhatTheServerReceives()
    {
        await using TestServerFixture server = await TestServerFixture.StartAsync();
        await using ServiceProvider provider = BuildProvider(server, new Dictionary<string, string?>
        {
            ["HttpResilience:Clients:api:Retry:RetryableMethods:0"] = "POST"
        });

        HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("api");

        using var source = new NonSeekableStream("payload"u8.ToArray());
        using var content = new StreamContent(source);
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"{server.BaseAddress}echo-orders")
        { Content = content };

        await client.SendAsync(request);

        // Measured, not assumed: no exception, three attempts, and the two retries deliver nothing. A
        // buffered content type would deliver "payload" three times. If a platform upgrade starts buffering
        // request content for retries, this fails and the documented rule in RetryOptions.RetryableMethods
        // and docs/RECIPES.md should be relaxed to match.
        Assert.Equal(["payload", string.Empty, string.Empty], server.EchoedOrderBodies);
    }

    /// <summary>A stream that can only be read once, like a network or pipe stream.</summary>
    private sealed class NonSeekableStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override void Flush() => _inner.Flush();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// A real host emits the disabled-client warning while starting, before it serves anything.
    /// </summary>
    /// <remarks>
    /// The unit tests invoke <c>IStartupValidator</c> by hand, which proves the notice fires when options are
    /// materialized but not that a host materializes them. This runs an actual <c>IHost</c> and asserts the
    /// warning is present the moment <c>StartAsync</c> returns, with no client ever created. Fails if the
    /// notice goes back to hanging off client creation, and fails if the host stops running startup
    /// validation -- which is the assumption the whole mechanism rests on.
    /// </remarks>
    [Fact]
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
                services.AddHttpClient("orders").AddHttpResilience();
            })
            .Build();

        Assert.Empty(sink.Matching("registered but disabled"));

        await host.StartAsync();
        try
        {
            string[] warnings = sink.Matching("registered but disabled");

            Assert.Single(warnings);
            Assert.StartsWith("[Warning]", warnings[0], StringComparison.Ordinal);
            Assert.Contains("HttpResilience:Clients:orders:Enabled", warnings[0], StringComparison.Ordinal);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
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
