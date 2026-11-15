using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HttpResilience.NET.Tests.Infrastructure;

/// <summary>
/// Records every request that reaches the primary handler.
/// </summary>
/// <remarks>
/// Origin call count is what distinguishes a correctly ordered pipeline from an inverted one. Prefer this over
/// a test server except where real HTTP semantics matter.
/// </remarks>
internal sealed class RecordingHandler : HttpMessageHandler
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim _arrived = new(0);
    private readonly Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> _respond;
    private readonly Lock _gate = new();
    private int _count;
    private int _concurrent;
    private int _maxConcurrent;

    public RecordingHandler(HttpStatusCode status = HttpStatusCode.InternalServerError)
        : this((request, _, _) => Task.FromResult(new HttpResponseMessage(status) { RequestMessage = request }))
    {
    }

    public RecordingHandler(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> respond) =>
        _respond = respond;

    /// <summary>Number of requests that reached the origin.</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>
    /// Completes once at least <paramref name="count"/> requests have reached the origin. Fails after ten
    /// seconds rather than hanging the run.
    /// </summary>
    public async Task WaitForCountAsync(int count)
    {
        using var timeout = new CancellationTokenSource(WaitLimit);
        while (Count < count)
        {
            try
            {
                await _arrived.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                Assert.Fail($"Expected {count} requests at the origin within {WaitLimit}, saw {Count}.");
            }
        }
    }

    /// <summary>Highest number of requests observed in flight at the same time.</summary>
    public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);

    /// <summary>Bodies delivered to the origin, in arrival order.</summary>
    public List<string> Bodies { get; } = [];

    /// <summary>Methods delivered to the origin, in arrival order.</summary>
    public List<string> Methods { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        int attempt = Interlocked.Increment(ref _count);
        int inFlight = Interlocked.Increment(ref _concurrent);

        lock (_gate)
        {
            _maxConcurrent = Math.Max(_maxConcurrent, inFlight);
            Methods.Add(request.Method.Method);
        }

        _arrived.Release();

        try
        {
            if (request.Content is not null)
            {
                string body = await request.Content.ReadAsStringAsync(cancellationToken);
                lock (_gate)
                {
                    Bodies.Add(body);
                }
            }

            return await _respond(request, attempt, cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _concurrent);
        }
    }

    /// <summary>
    /// The synchronous path <c>HttpClient.Send</c> takes. The base implementation throws
    /// <see cref="NotSupportedException"/>, which would let a test expecting a refusal pass for the wrong reason.
    /// </summary>
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
        SendAsync(request, cancellationToken).GetAwaiter().GetResult();
}

/// <summary>
/// Builds a fully wired client through the library's public API, so tests exercise real registration.
/// </summary>
internal sealed class ResilienceHarness : IAsyncDisposable
{
    private readonly ServiceProvider _provider;

    private ResilienceHarness(ServiceProvider provider, HttpClient client, RecordingHandler origin)
    {
        _provider = provider;
        Client = client;
        Origin = origin;
    }

    public HttpClient Client { get; }

    public RecordingHandler Origin { get; }

    public IServiceProvider Services => _provider;

    public static ResilienceHarness Create(
        Settings settings,
        RecordingHandler? origin = null,
        string clientName = "test",
        string? sectionName = null,
        bool hedged = false,
        Action<HttpResilienceOptions>? configure = null,
        Action<HttpResilienceOptions>? postConfigure = null,
        TimeProvider? timeProvider = null,
        ListLoggerProvider? logs = null)
    {
        origin ??= new RecordingHandler();
        ServiceProvider provider = BuildProvider(
            settings, origin, clientName, sectionName, hedged, configure, postConfigure, timeProvider, logs);
        HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(clientName);
        return new ResilienceHarness(provider, client, origin);
    }

    /// <summary>Builds the container without creating a client, for startup-validation tests.</summary>
    public static ServiceProvider BuildProvider(
        Settings settings,
        RecordingHandler? origin = null,
        string clientName = "test",
        string? sectionName = null,
        bool hedged = false,
        Action<HttpResilienceOptions>? configure = null,
        Action<HttpResilienceOptions>? postConfigure = null,
        TimeProvider? timeProvider = null,
        ListLoggerProvider? logs = null)
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Build())
            .Build();

        var services = new ServiceCollection();
        if (timeProvider is not null)
        {
            // Polly's timers (timeouts, hedging delay, retry backoff) read the container's TimeProvider.
            services.AddSingleton(timeProvider);
        }

        if (logs is not null)
        {
            services.AddLogging(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Trace);
                logging.AddProvider(logs);
            });
        }

        services.AddHttpResilience(configuration);

        IHttpClientBuilder builder = services.AddHttpClient(clientName);
        // Every argument is required where it appears, so a wrapper picks the overload for what it has.
        _ = (sectionName, configure, hedged) switch
        {
            (null, null, false) => builder.AddHttpResilienceHandler(),
            (null, null, true) => builder.AddHttpResilienceHedgingHandler(),
            ({ } section, null, false) => builder.AddHttpResilienceHandler(section),
            ({ } section, null, true) => builder.AddHttpResilienceHedgingHandler(section),
            (null, { } adjust, false) => builder.AddHttpResilienceHandler(adjust),
            (null, { } adjust, true) => builder.AddHttpResilienceHedgingHandler(adjust),
            ({ } section, { } adjust, false) => builder.AddHttpResilienceHandler(section, adjust),
            ({ } section, { } adjust, true) => builder.AddHttpResilienceHedgingHandler(section, adjust)
        };

        if (origin is not null)
        {
            builder.ConfigurePrimaryHttpMessageHandler(() => origin);
        }

        // Registered after the client, so it runs last.
        if (postConfigure is not null)
        {
            services.PostConfigure(clientName, postConfigure);
        }

        return services.BuildServiceProvider(validateScopes: true);
    }

    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string url = "http://origin.test/x", HttpContent? content = null, CancellationToken cancellationToken = default) =>
        Client.SendAsync(
            new HttpRequestMessage(method, url) { Content = content },
            // The test's token by default, so a test's timeout also cancels the request it abandoned.
            cancellationToken == default ? TestContext.Current.CancellationToken : cancellationToken);

    public Task<HttpResponseMessage> GetAsync(string url = "http://origin.test/x", CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, url, cancellationToken: cancellationToken);

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _provider.DisposeAsync();
    }
}

/// <summary>
/// Fluent builder for the <c>HttpResilience:*</c> configuration keys, so tests read as configuration rather
/// than as string literals.
/// </summary>
internal sealed class Settings
{
    private readonly Dictionary<string, string?> _values = [];

    public static Settings Enabled() => new Settings()
        .Set("Enabled", "true")
        .Set("Timeout:Total", "00:00:30")
        .Set("Timeout:Attempt", "00:00:10")
        .Set("Retry:MaxRetries", "2")
        .Set("Retry:BaseDelay", "00:00:00")
        .Set("Retry:BackoffType", "Constant")
        .Set("CircuitBreaker:MinimumThroughput", "1000");

    /// <summary>
    /// Root settings for a hedged client. Hedging keeps a circuit breaker and a limiter per authority, so
    /// the authorities a hedged client may call have to be listed.
    /// </summary>
    /// <remarks>
    /// Root-level only: the harness client is named 'test', so a client section would be unused.
    /// </remarks>
    public static Settings Hedged() => Enabled()
        .Set("PipelineSelection:Authorities:0", "http://origin.test");

    public static Settings Empty() => new();

    public Settings Set(string key, string? value)
    {
        _values[$"HttpResilience:{key}"] = value;
        return this;
    }

    public Settings ForClient(string client, string key, string? value)
    {
        _values[$"HttpResilience:Clients:{client}:{key}"] = value;
        return this;
    }

    public Dictionary<string, string?> Build() => new(_values);
}
