using System.Collections.Concurrent;
using System.Net;
using HttpResilience.NET;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// What the Native AOT smoke binary asserts: configuration values reached the options and the pipeline, and a
/// real host's start ran validation, the startup client check and the startup notices.
/// </summary>
/// <remarks>
/// Trimming a reflection-based binder does not fail loudly: it leaves a client on defaults it never configured.
/// So every value checked here differs from its default, and every behavior is read back at run time. Compiled
/// into the AOT binary and into the integration tests, so <c>dotnet test</c> runs the same checks under JIT.
/// </remarks>
internal static class SmokeScenarios
{
    public static async Task<IReadOnlyList<string>> RunAsync()
    {
        var failures = new List<string>();
        void Check(bool condition, string what)
        {
            if (!condition)
            {
                failures.Add(what);
            }
        }

        await ConfiguredHostAsync(Check);
        await HostRefusesAnUnconfigurableClientAsync(Check);
        await HostRefusesInvalidConfigurationAsync(Check);
        await CodeOnlyAsync(Check);

        return failures;
    }

    /// <summary>A standard and a hedged client on a real host, bound from configuration.</summary>
    private static async Task ConfiguredHostAsync(Action<bool, string> check)
    {
        var logs = new CapturingLoggerProvider();
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Logging.AddProvider(logs);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["HttpResilience:Timeout:Total"] = "00:00:12",
            ["HttpResilience:Timeout:Attempt"] = "00:00:04",
            ["HttpResilience:Retry:BaseDelay"] = "00:00:00",
            ["HttpResilience:Retry:BackoffType"] = "Constant",
            ["HttpResilience:Retry:UseJitter"] = "false",
            ["HttpResilience:CircuitBreaker:MinimumThroughput"] = "1000",
            ["HttpResilience:ConcurrencyLimiter:Backstop"] = "250",
            ["HttpResilience:Clients:Orders:Retry:MaxRetries"] = "3",
            ["HttpResilience:Clients:Orders:PipelineSelection:Mode"] = "ByAuthority",
            ["HttpResilience:Clients:Orders:PipelineSelection:Authorities:0"] = "http://origin.test",
            ["HttpResilience:Clients:Orders:Connection:Enabled"] = "true",
            ["HttpResilience:Clients:Orders:Connection:PooledConnectionLifetime"] = "00:01:30",
            ["HttpResilience:Clients:Search:Hedging:Delay"] = "00:00:00.050",
            ["HttpResilience:Clients:Search:PipelineSelection:Authorities:0"] = "http://origin.test"
        });

        builder.Services.AddHttpResilience(builder.Configuration);
        builder.Services.AddHttpResilienceTelemetry();

        var ordersCalls = new Counter();
        builder.Services.AddHttpClient("Orders")
            .AddHttpResilienceHandler()
            .AddHttpMessageHandler(() => new RespondingHandler(ordersCalls, (_, _) =>
                Task.FromResult(HttpStatusCode.InternalServerError)));

        // The first attempt stalls, so only a hedged attempt can answer in time.
        var searchCalls = new Counter();
        builder.Services.AddHttpClient("Search")
            .AddHttpResilienceHedgingHandler()
            .AddHttpMessageHandler(() => new RespondingHandler(searchCalls, async (call, cancellationToken) =>
            {
                if (call == 1)
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                }

                return HttpStatusCode.OK;
            }));

        using IHost host = builder.Build();
        await host.StartAsync();
        try
        {
            check(logs.Has(15, "'Orders'") && logs.Has(15, "'Search'"),
                "startup notices logged while the host started (event 15 for both clients)");

            IOptionsMonitor<HttpResilienceOptions> monitor =
                host.Services.GetRequiredService<IOptionsMonitor<HttpResilienceOptions>>();

            HttpResilienceOptions orders = monitor.Get("Orders");
            check(orders.Timeout.Total == TimeSpan.FromSeconds(12), "TimeSpan bound from the root");
            check(orders.Retry.MaxRetries == 3, "per-client override bound");
            check(orders.Retry.BackoffType == HttpResilienceRetryBackoffType.Constant, "enum bound by name");
            check(!orders.Retry.UseJitter, "bool bound against its default");
            check(orders.ConcurrencyLimiter.Backstop == 250, "backstop inherited from the root");
            check(orders.PipelineSelection.Mode == HttpResiliencePipelineSelectionMode.ByAuthority, "nested enum bound");
            check(orders.PipelineSelection.Authorities is ["http://origin.test"], "string list bound");
            check(orders.Connection.Enabled, "Connection:Enabled bound");

            // Connection:Enabled is applied to the real primary handler, after every registration.
            HttpMessageHandler handler =
                host.Services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler("Orders");
            while (handler is DelegatingHandler { InnerHandler: { } inner })
            {
                handler = inner;
            }

            check(handler is SocketsHttpHandler { PooledConnectionLifetime.TotalSeconds: 90 },
                $"connection settings applied to the primary handler (found {handler.GetType().Name})");

            using HttpClient ordersClient = host.Services.GetRequiredService<IHttpClientFactory>().CreateClient("Orders");
            using HttpResponseMessage ordersResponse = await ordersClient.GetAsync("http://origin.test/x");
            check(ordersResponse.StatusCode == HttpStatusCode.InternalServerError, "request completed through the pipeline");
            check(ordersCalls.Value == 4, $"retried to the configured count (saw {ordersCalls.Value})");

            HttpResilienceOptions search = monitor.Get("Search");
            check(search.Hedging.Delay == TimeSpan.FromMilliseconds(50), "hedging delay bound");

            using HttpClient searchClient = host.Services.GetRequiredService<IHttpClientFactory>().CreateClient("Search");
            using HttpResponseMessage searchResponse = await searchClient.GetAsync("http://origin.test/q");
            check(searchResponse.StatusCode == HttpStatusCode.OK && searchCalls.Value == 2,
                $"a hedged attempt answered for the stalled one (status {(int)searchResponse.StatusCode}, {searchCalls.Value} calls)");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    /// <summary>The startup client check runs as a hosted service: a client that cannot be created stops the host.</summary>
    private static async Task HostRefusesAnUnconfigurableClientAsync(Action<bool, string> check)
    {
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["HttpResilience:Connection:Enabled"] = "true" });
        builder.Services.AddHttpResilience(builder.Configuration);
        builder.Services.AddHttpClient("Broken")
            .AddHttpResilienceHandler()
            .ConfigurePrimaryHttpMessageHandler(() => new NotASocketsHandler());

        using IHost host = builder.Build();
        Exception? failure = await StartAsync(host);
        check(failure is InvalidOperationException { Message: var message } && message.Contains("Connection:Enabled", StringComparison.Ordinal),
            $"the startup client check stopped the host (got {failure?.GetType().Name ?? "a clean start"})");
    }

    /// <summary>Options validation runs at host start: an invalid value stops the host before any request.</summary>
    private static async Task HostRefusesInvalidConfigurationAsync(Action<bool, string> check)
    {
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["HttpResilience:Timeout:Total"] = "00:00:05",
            ["HttpResilience:Timeout:Attempt"] = "00:00:10"
        });
        builder.Services.AddHttpResilience(builder.Configuration);
        builder.Services.AddHttpClient("Invalid").AddHttpResilienceHandler();

        using IHost host = builder.Build();
        Exception? failure = await StartAsync(host);
        // The host reports every startup validator's failures together, as an AggregateException.
        check(Flatten(failure).OfType<OptionsValidationException>()
                .Any(validation => validation.Message.Contains("Timeout:Attempt", StringComparison.Ordinal)),
            $"validation stopped the host (got {failure?.GetType().Name ?? "a clean start"})");
    }

    /// <summary>
    /// Code only: root values from <c>AddHttpResilience(configure)</c> and an empty root section, which is built
    /// with <c>ConfigurationBuilder</c> and bound like a real one, so it has to survive trimming too.
    /// </summary>
    private static async Task CodeOnlyAsync(Action<bool, string> check)
    {
        var services = new ServiceCollection();
        services.AddHttpResilience(root =>
        {
            root.Timeout.Total = TimeSpan.FromSeconds(15);
            root.Retry.MaxRetries = 4;
            root.Retry.BaseDelay = TimeSpan.Zero;
        });

        var calls = new Counter();
        services.AddHttpClient("Reports")
            .AddHttpResilienceHandler()
            .AddHttpMessageHandler(() => new RespondingHandler(calls, (_, _) =>
                Task.FromResult(HttpStatusCode.InternalServerError)));

        await using ServiceProvider provider = services.BuildServiceProvider();
        HttpResilienceOptions reports = provider.GetRequiredService<IOptionsMonitor<HttpResilienceOptions>>().Get("Reports");
        check(reports.Timeout.Total == TimeSpan.FromSeconds(15), "code-only root value applied");

        using HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("Reports");
        (await client.GetAsync("http://origin.test/x")).Dispose();
        check(calls.Value == 5, $"code-only retry count reached the pipeline (saw {calls.Value})");
    }

    private static async Task<Exception?> StartAsync(IHost host)
    {
        try
        {
            await host.StartAsync();
            await host.StopAsync();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static IEnumerable<Exception> Flatten(Exception? exception) => exception switch
    {
        null => [],
        AggregateException aggregate => aggregate.Flatten().InnerExceptions,
        _ => [exception]
    };

    private sealed class Counter
    {
        private int _value;

        public int Value => Volatile.Read(ref _value);

        public int Increment() => Interlocked.Increment(ref _value);
    }

    /// <summary>The innermost delegating handler: answers in process, so the primary handler stays the package's.</summary>
    private sealed class RespondingHandler(Counter calls, Func<int, CancellationToken, Task<HttpStatusCode>> respond)
        : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpStatusCode status = await respond(calls.Increment(), cancellationToken);
            return new HttpResponseMessage(status) { RequestMessage = request };
        }
    }

    private sealed class NotASocketsHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request });
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<(int EventId, string Message)> _entries = new();

        public bool Has(int eventId, string text) =>
            _entries.Any(entry => entry.EventId == eventId && entry.Message.Contains(text, StringComparison.Ordinal));

        public ILogger CreateLogger(string categoryName) => new Logger(_entries);

        public void Dispose()
        {
        }

        private sealed class Logger(ConcurrentQueue<(int, string)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                entries.Enqueue((eventId.Id, formatter(state, exception)));
        }
    }
}
