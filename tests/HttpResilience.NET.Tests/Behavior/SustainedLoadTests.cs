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
/// Every other behavioral test here drives one request, or a handful, in sequence. That is the right shape
/// for a pipeline-ordering or safe-method assertion and it is the wrong shape for the four pieces of mutable
/// state this package owns: the limiter instances, the circuit-breaker tracker, the metrics dictionaries and
/// the per-authority pipeline keys. Those are reached by every request on every thread at once, and a race in
/// one of them is a production defect no single-request test can see.
/// <para>
/// These are deterministic assertions -- a concurrency ceiling, a permit balance, a bounded key set -- not
/// timing ones, so they do not flake and they do not sleep. What they cannot do is prove the absence of a
/// race; they can only fail when one is common enough to hit at this width. That is worth having and is not
/// worth confusing with a proof.
/// </para>
/// </remarks>
public class SustainedLoadTests
{
    private const string Origin = "http://origin.test";

    private static IConfigurationSection Configuration(Settings settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings.Build()).Build()
            .GetSection("HttpResilience");

    /// <summary>
    /// The concurrency bound holds under 500-way parallelism, and every permit taken is given back.
    /// </summary>
    /// <remarks>
    /// Two failures are in scope and neither is visible one request at a time. The first is the bound itself:
    /// the limiter sits outside the retry loop so one permit covers a whole logical request, and a mistake
    /// that re-acquired per attempt would still pass every sequential test while letting concurrency drift
    /// above the cap under load. <see cref="RecordingHandler.MaxConcurrent"/> is measured at the origin, which
    /// is the only place the real number is.
    /// <para>
    /// The second is a leak. A lease that is not disposed on some path -- a retry, a rejection, a cancelled
    /// wait -- burns a permit permanently, and the symptom is a client that quietly stops being able to send
    /// anything after hours of traffic. Reading the limiter's own statistics back through the gauge after the
    /// load has drained turns that into an assertion: available permits must be the full configured limit
    /// again and the queue must be empty. Retries are on and a quarter of the responses fail, so the retry
    /// path is exercised rather than assumed.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ParallelLoad_HoldsTheConcurrencyBound_AndReturnsEveryPermit()
    {
        const int Limit = 8;
        const int Requests = 500;

        var origin = new RecordingHandler(async (_, n, cancellationToken) =>
        {
            await Task.Delay(5, cancellationToken);
            return new HttpResponseMessage(
                n % 4 == 0 ? HttpStatusCode.InternalServerError : HttpStatusCode.OK);
        });

        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled()
                // High enough that the breaker never opens: this test is about the limiters, and a breaker
                // tripping would shorten the load rather than fail the assertion.
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

        // Retries happened, so the permit accounting was exercised on the path that reuses one permit for
        // several attempts rather than only on the straight-through one.
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
    /// The hedging handler mints an endpoint pipeline -- a circuit breaker, a limiter and a Polly metric
    /// series -- per request authority, and never evicts one. That is why
    /// <c>AddHedgedHttpResilience</c> requires <c>PipelineSelection:Authorities</c>: without a bound, a
    /// destination that can be influenced by request data is a memory-exhaustion path and an unbounded
    /// metric dimension in the same move.
    /// <para>
    /// The bound is <see cref="AuthorityAllowListHandler"/>, registered outermost so an unlisted authority is
    /// refused before anything is allocated for it. This asserts the consequence at all three places it has
    /// to hold: the request is refused, this package's breaker tracker gains no key for it, and Polly's own
    /// <c>pipeline.instance</c> dimension -- which this package does not control and cannot filter -- takes
    /// no value from it either. 200 distinct hosts, because one would not distinguish a bound from a
    /// coincidence.
    /// </para>
    /// </remarks>
    [Fact]
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
            .AddHedgedHttpResilience(clientName: string.Empty)
            .ConfigurePrimaryHttpMessageHandler(() => origin);

        await using ServiceProvider provider = services.BuildServiceProvider();
        HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName);

        // Enough failing traffic on each allowed authority to open its breaker, so the tracker is populated
        // rather than trivially empty.
        foreach (string authority in (string[])["http://allowed-one.test", "http://allowed-two.test"])
        {
            for (int i = 0; i < 20; i++)
            {
                try
                {
                    (await client.GetAsync($"{authority}/x")).Dispose();
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
            HttpRequestException failure = await Assert.ThrowsAsync<HttpRequestException>(
                () => client.GetAsync($"http://not-listed-{i}.test/x"));

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
    /// Polly's meter is a <b>static</b> instrument set named <c>Polly</c>, shared by every pipeline in the
    /// process -- unlike this package's meter, which <c>IMeterFactory</c> scopes per container and which
    /// <see cref="GaugeCollector"/> filters on. There is no container discriminator available here at all, so
    /// an unfiltered listener collects the authorities of every other test running in the same assembly:
    /// measured, this assertion saw <c>orders.internal</c> and an IDN host from two unrelated fixtures and
    /// failed as an "unbounded dimension" that was nothing of the sort. The client name is unique to this
    /// test and appears in <c>pipeline.name</c>, so that is the discriminator.
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
    /// Via <see cref="GaugeCollector"/> rather than a listener of its own, because the package's meter name is
    /// process-wide: an unscoped listener sees every other live container's gauges, so this assertion read
    /// another test's limiter and failed with 0 available permits when the suite ran together and passed when
    /// it ran alone. <c>IMeterFactory</c> stamps each meter with the factory that created it, which is per
    /// container, and the collector already filters on it.
    /// </remarks>
    private static LimiterReading ReadLimiter(IServiceProvider services, string kind)
    {
        // Resolved for its side effect: the gauges do not exist until the metrics object does.
        _ = services.GetRequiredService<HttpResilienceMetrics>();

        using var collector = new GaugeCollector(services);

        long Read(string instrument) => Assert.Single(
            collector.Collect(instrument),
            measurement => (string?)measurement.Tag("http.resilience.limiter.kind") == kind)
            .Value;

        return new LimiterReading(
            Read("http.resilience.limiter.available_permits"),
            Read("http.resilience.limiter.queued_requests"));
    }
}
