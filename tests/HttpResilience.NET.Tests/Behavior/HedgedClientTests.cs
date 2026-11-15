using System.Diagnostics.Metrics;
using System.Net.Http.Headers;
using System.Net;
using System.Threading.RateLimiting;
using BclConcurrencyLimiterOptions = System.Threading.RateLimiting.ConcurrencyLimiterOptions;
using HttpResilience.NET.Internal;
using HttpResilience.NET.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;
using Polly.RateLimiting;
using Polly.Timeout;

namespace HttpResilience.NET.Tests.Behavior;

/// <summary>
/// A hedged client is bounded to its authority allow-list.
/// </summary>
public class HedgedClientTests
{
    private static IConfigurationRoot Configuration(Settings settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings.Build()).Build();

    private const string PipelineKeySelectorSharedKey = "shared";

    /// <summary>
    /// The hedging handler keeps a circuit breaker, a limiter and a metric series per authority for the life
    /// of the process. Without an allow-list, a destination influenced by request data exhausts memory.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public void HedgedClient_WithoutAnAuthorityAllowList_FailsAtStartup()
    {
        var services = new ServiceCollection();
        services.AddHttpResilience(Configuration(Settings.Enabled()));

        OptionsValidationException exception = StartupAssert.Fails(services, () => services.AddHttpClient("search").AddHttpResilienceHedgingHandler());

        Assert.Contains("PipelineSelection:Authorities", exception.Message, StringComparison.Ordinal);
        Assert.Contains("AddHttpResilienceHedgingHandler", exception.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task HedgedClient_RejectsAnUnlistedAuthority_BeforeAnythingIsAllocated()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Hedged().Set("Hedging:Delay", "00:00:00"),
            hedged: true);

        // InvalidOperationException, as HttpClient throws for a request it cannot send: the refusal is a
        // configuration error that no retry can fix, and every HttpRequestException counts as transient.
        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.GetAsync("http://elsewhere.test/x", TestContext.Current.CancellationToken));

        Assert.Contains("PipelineSelection:Authorities", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, harness.Origin.Count);
    }

    /// <summary>
    /// A consumer's own retry around the client does not repeat the refusal. The platform's default retry
    /// predicate (<c>HttpClientResiliencePredicates.IsTransient</c>, behind every <c>HttpRetryStrategyOptions</c>)
    /// treats every <see cref="HttpRequestException"/> as transient, so refusing with one turned a fixed
    /// configuration error into retry traffic.
    /// </summary>
    /// <remarks>Fails if the allow-list refuses with an <see cref="HttpRequestException"/>: three retries.</remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task AnUnlistedAuthority_IsNotRetriedByAConsumersOwnRetry()
    {
        var services = new ServiceCollection();
        services.AddHttpResilience(Configuration(Settings.Hedged()));

        int retries = 0;
        services.AddHttpClient("test")
            .AddHttpResilienceHedgingHandler()
            .ConfigurePrimaryHttpMessageHandler(() => new RecordingHandler(HttpStatusCode.OK));

        // The consumer's retry around the client call, with the platform's default predicate. (A resilience
        // handler registered on the client before this package's would fail client creation instead.)
        ResiliencePipeline<HttpResponseMessage> consumerRetry = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = 3,
                Delay = TimeSpan.Zero,
                OnRetry = _ =>
                {
                    Interlocked.Increment(ref retries);
                    return default;
                }
            })
            .Build();

        await using ServiceProvider provider = services.BuildServiceProvider();
        HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("test");

        Exception? thrown = await Record.ExceptionAsync(async () =>
            await consumerRetry.ExecuteAsync(
                async token => await client.GetAsync("http://elsewhere.test/x", token),
                TestContext.Current.CancellationToken));

        Assert.Equal(0, retries);
        Assert.IsType<InvalidOperationException>(thrown);
    }

    /// <summary>
    /// <c>HttpClient.Send</c> reaches a handler through <c>DelegatingHandler.Send</c>, not <c>SendAsync</c>.
    /// </summary>
    /// <remarks>
    /// Fails if the allow-list handler overrides only <c>SendAsync</c>: the base <c>Send</c> forwards to the
    /// inner handler unchecked, and the unlisted request reaches the origin.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task HedgedClient_RejectsAnUnlistedAuthority_OnTheSynchronousPath()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Hedged().Set("Hedging:Delay", "00:00:00"),
            hedged: true);

        using var request = new HttpRequestMessage(HttpMethod.Get, "http://elsewhere.test/x");
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => harness.Client.Send(request, TestContext.Current.CancellationToken));

        Assert.Contains("PipelineSelection:Authorities", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, harness.Origin.Count);
    }

    /// <summary>
    /// The synchronous path still delivers a request to a listed authority.
    /// </summary>
    /// <remarks>
    /// Fails if the allow-list handler's <c>Send</c> refuses or does not forward.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task HedgedClient_SendsToAListedAuthority_OnTheSynchronousPath()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Hedged(),
            new RecordingHandler(HttpStatusCode.OK),
            hedged: true);

        using var request = new HttpRequestMessage(HttpMethod.Get, "http://origin.test/x");
        using HttpResponseMessage response = harness.Client.Send(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, harness.Origin.Count);
    }

    /// <summary>
    /// Breakers on the hedging pipeline are per authority whatever the selection mode, so tracking them
    /// under one key let the last transition to fire overwrite the rest -- one host recovering masked
    /// another still open.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
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
                await harness.GetAsync("http://bad.test/x", TestContext.Current.CancellationToken);
            }
            catch (BrokenCircuitException)
            {
                // Expected once its breaker opens. What matters is whose state is recorded.
            }
        }

        // The healthy host must not be able to write over the failing host's state.
        for (int i = 0; i < 3; i++)
        {
            await harness.GetAsync("http://good.test/x", TestContext.Current.CancellationToken);
        }

        IReadOnlyList<string> notClosed = BreakerState.NotClosed(harness.Services);

        Assert.Contains(notClosed, key => key.Contains("bad.test", StringComparison.Ordinal));
        Assert.DoesNotContain(notClosed, key => key.Contains(PipelineKeySelectorSharedKey, StringComparison.Ordinal));
    }
}
