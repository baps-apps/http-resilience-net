using System.Diagnostics.Metrics;
using System.Net;
using HttpResilience.NET.Internal;
using HttpResilience.NET.Tests.Infrastructure;
using HttpResilience.NET.Tests.Internal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HttpResilience.NET.Tests.Behavior;

/// <summary>
/// What the shared state does under real parallel traffic, rather than under one request at a time.
/// </summary>
/// <remarks>
/// Covers the shared mutable state: limiter instances, breaker tracker, metrics dictionaries and
/// per-authority pipeline keys. Assertions are deterministic (a concurrency ceiling, a permit balance, a
/// bounded key set). They can catch a common race, not prove there is none.
/// </remarks>
public class SustainedLoadTests
{
    private const string Origin = "http://origin.test";

    private static IConfigurationSection Configuration(Settings settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings.Build()).Build()
            .GetSection("HttpResilience");

    /// <summary>
    /// The concurrency bound holds under 64-way parallelism, and every permit taken is given back.
    /// </summary>
    /// <remarks>
    /// Checks three things: the cap was actually reached, so the load really pressed against it; concurrency at
    /// the origin (<see cref="RecordingHandler.MaxConcurrent"/>) never exceeds it, even with retries; and after
    /// the load drains, all permits are back and the queue is empty, so no lease leaked on a retry. A quarter of
    /// responses fail, so retries run. Parallelism is set explicitly: the default is the processor count, which
    /// on a small CI agent is below the cap and cannot exceed it.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ParallelLoad_HoldsTheConcurrencyBound_AndReturnsEveryPermit()
    {
        const int Limit = 8;
        const int Requests = 500;
        const int Parallelism = 64;

        var origin = new RecordingHandler(async (_, n, cancellationToken) =>
        {
            await Task.Delay(5, cancellationToken);
            return new HttpResponseMessage(
                n % 4 == 0 ? HttpStatusCode.InternalServerError : HttpStatusCode.OK);
        });

        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled()
                // High enough that the breaker never opens; this test is about the limiters.
                .Set("CircuitBreaker:MinimumThroughput", "100000")
                .Set("ConcurrencyLimiter:Enabled", "true")
                .Set("ConcurrencyLimiter:Limit", "8")
                .Set("ConcurrencyLimiter:QueueLimit", "1000")
                .Set("RateLimiter:Enabled", "true")
                .Set("RateLimiter:PermitLimit", "5000")
                .Set("RateLimiter:Window", "00:01:00")
                .Set("RateLimiter:QueueLimit", "1000"),
            origin);

        int completed = 0;
        await Parallel.ForEachAsync(
            Enumerable.Range(0, Requests),
            new ParallelOptions { MaxDegreeOfParallelism = Parallelism },
            async (_, cancellationToken) =>
            {
                using HttpResponseMessage response = await harness.Client.GetAsync(
                    $"{Origin}/x", cancellationToken);
                Interlocked.Increment(ref completed);
            });

        Assert.Equal(Requests, completed);
        Assert.True(
            origin.MaxConcurrent <= Limit,
            $"concurrency bound of {Limit} was exceeded: {origin.MaxConcurrent} requests were in flight");
        Assert.True(
            origin.MaxConcurrent == Limit,
            $"the load never reached the bound of {Limit} (peak {origin.MaxConcurrent}), so it proves nothing");

        // Retries happened, so permit accounting was exercised on the retry path too.
        Assert.True(origin.Count > Requests, $"expected retries; origin saw only {origin.Count}");

        LimiterReading concurrency = ReadLimiter(harness.Services, "concurrency");
        Assert.Equal(Limit, concurrency.AvailablePermits);
        Assert.Equal(0, concurrency.QueuedRequests);

        LimiterReading rate = ReadLimiter(harness.Services, "rate");
        Assert.Equal(0, rate.QueuedRequests);
    }

    /// <summary>
    /// A hedged client's per-authority state stays bounded by the allow-list however many distinct
    /// authorities are asked for.
    /// </summary>
    /// <remarks>
    /// The hedging handler creates a breaker, limiter and metric series per request authority and never
    /// evicts them. <see cref="AuthorityAllowListHandler"/> refuses unlisted authorities first. Asserted in
    /// three places: the request is refused, the breaker tracker gains no key, and Polly's
    /// <c>pipeline.instance</c> takes no value from it. 200 distinct hosts.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task HedgedClient_KeepsEveryPerAuthorityDimension_BoundedByTheAllowList()
    {
        const string ClientName = "hedged-authority-bound";

        var pipelineInstances = new HashSet<string>(StringComparer.Ordinal);
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == HttpResilienceTelemetryExtensions.PollyMeterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, _, tags, _) => Collect(tags, pipelineInstances, ClientName));
        listener.SetMeasurementEventCallback<int>((_, _, tags, _) => Collect(tags, pipelineInstances, ClientName));
        listener.Start();

        var services = new ServiceCollection();
        services.AddHttpResilience(Configuration(
            Settings.Enabled()
                .Set("Timeout:Total", "00:00:10")
                .Set("Timeout:Attempt", "00:00:01")
                .Set("Hedging:Delay", "00:00:00.050")
                .Set("Hedging:MaxHedgedAttempts", "1")
                // Low, so that failing traffic actually opens the breakers and the tracker has keys to bound.
                .Set("CircuitBreaker:MinimumThroughput", "2")
                .Set("CircuitBreaker:FailureRatio", "0.1")
                .Set("PipelineSelection:Authorities:0", "http://allowed-one.test")
                .Set("PipelineSelection:Authorities:1", "http://allowed-two.test")));
        services.AddHttpResilienceTelemetry();

        var origin = new RecordingHandler(HttpStatusCode.InternalServerError);
        services.AddHttpClient(ClientName)
            .AddHttpResilienceHedgingHandler()
            .ConfigurePrimaryHttpMessageHandler(() => origin);

        await using ServiceProvider provider = services.BuildServiceProvider();
        HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName);

        // Enough failing traffic per allowed authority to open its breaker, so the tracker is populated.
        foreach (string authority in (string[])["http://allowed-one.test", "http://allowed-two.test"])
        {
            for (int i = 0; i < 20; i++)
            {
                try
                {
                    (await client.GetAsync($"{authority}/x", TestContext.Current.CancellationToken)).Dispose();
                }
                catch (Exception exception) when (exception is not HttpRequestException)
                {
                    // A broken circuit or a timeout. Either way the attempt was made.
                }
            }
        }

        int refused = 0;
        for (int i = 0; i < 200; i++)
        {
            InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => client.GetAsync($"http://not-listed-{i}.test/x", TestContext.Current.CancellationToken));

            Assert.Contains("PipelineSelection:Authorities", failure.Message, StringComparison.Ordinal);

            // The message names the authority and nothing else from the URI: no path, no query, no userinfo.
            Assert.DoesNotContain("/x", failure.Message, StringComparison.Ordinal);
            refused++;
        }

        Assert.Equal(200, refused);

        var tracker = provider.GetRequiredService<CircuitBreakerStateTracker>();
        string[] authorities = [.. tracker.Enumerate().Select(entry => entry.Key.Authority).Distinct()];

        Assert.NotEmpty(authorities);
        Assert.All(authorities, authority => Assert.Contains(
            authority,
            (string[])["http://allowed-one.test", "http://allowed-two.test", PipelineKeySelector.SharedKey]));

        listener.RecordObservableInstruments();
        Assert.All(pipelineInstances, instance => Assert.DoesNotContain(
            "not-listed", instance, StringComparison.Ordinal));

        // The two allowed authorities plus at most the shared key: bounded by configuration, not by traffic.
        Assert.True(
            pipelineInstances.Count <= 3,
            $"unbounded pipeline.instance dimension: {string.Join(", ", pipelineInstances)}");
    }

    /// <summary>
    /// Records the <c>pipeline.instance</c> values belonging to this test's own client.
    /// </summary>
    /// <remarks>
    /// Polly's meter is static and process-wide, so other tests' values would appear. Filtered on
    /// <c>pipeline.name</c>, which contains this test's unique client name.
    /// </remarks>
    private static void Collect(
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        HashSet<string> into,
        string clientName)
    {
        string? instance = null;
        bool mine = false;

        foreach (KeyValuePair<string, object?> tag in tags)
        {
            if (tag.Key == "pipeline.instance" && tag.Value is string value && value.Length > 0)
            {
                instance = value;
            }
            else if (tag.Key == "pipeline.name" && tag.Value is string name &&
                name.Contains(clientName, StringComparison.Ordinal))
            {
                mine = true;
            }
        }

        if (mine && instance is not null)
        {
            lock (into)
            {
                into.Add(instance);
            }
        }
    }

    private readonly record struct LimiterReading(long AvailablePermits, long QueuedRequests);

    /// <summary>
    /// Reads one limiter's statistics back through the gauges, which is the only public view of them.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="GaugeCollector"/>, which filters to this container's meter.
    /// </remarks>
    private static LimiterReading ReadLimiter(IServiceProvider services, string kind)
    {
        // Resolved for its side effect: the gauges do not exist until the metrics object does.
        _ = services.GetRequiredService<HttpResilienceMetrics>();

        using var collector = new GaugeCollector(services);

        long Read(string instrument) => Assert.Single(
            collector.Collect(instrument),
            measurement => (string?)measurement.Tag("http_resilience.limiter.kind") == kind)
            .Value;

        return new LimiterReading(
            Read("http_resilience.limiter.available_permits"),
            Read("http_resilience.limiter.queued_requests"));
    }
}
