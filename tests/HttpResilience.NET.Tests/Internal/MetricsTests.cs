using System.Diagnostics.Metrics;
using Microsoft.Extensions.Diagnostics.Metrics;
using System.Net;
using HttpResilience.NET.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Polly.CircuitBreaker;

namespace HttpResilience.NET.Tests.Internal;

/// <summary>
/// The two things an operator has to be able to graph that neither Polly nor <c>System.Net.Http</c> publishes:
/// whether a breaker is open <i>right now</i>, and how close a limiter is to saturation.
/// </summary>
/// <remarks>
/// Polly emits a counter of breaker transition events, which answers "did it open" and not "is it open" --
/// a gauge is the only instrument that survives a restarted scrape or a missed event. Limiter statistics are
/// on <see cref="System.Threading.RateLimiting.RateLimiter"/> and were simply never read.
/// <para>
/// Both are observable gauges, so the callback runs once per collection rather than once per request. A test
/// that polled them per request would not notice the difference, which is why
/// <see cref="Gauges_AreObservable_SoNothingIsPolledOnTheRequestPath"/> asserts the instrument type.
/// </para>
/// </remarks>
public class MetricsTests
{
    [Fact]
    public async Task CircuitBreakerState_IsPublishedAsAGauge()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled()
                .Set("CircuitBreaker:MinimumThroughput", "2")
                .Set("CircuitBreaker:FailureRatio", "0.1")
                .Set("Retry:Enabled", "false"),
            new RecordingHandler(HttpStatusCode.InternalServerError));

        using var collector = new GaugeCollector(harness.Services);

        // Once the breaker opens the next call fails fast rather than returning a response, which is the
        // state being asserted -- so the loop drives it there rather than treating it as an error.
        for (int i = 0; i < 10; i++)
        {
            try
            {
                (await harness.GetAsync()).Dispose();
            }
            catch (BrokenCircuitException)
            {
                break;
            }
        }

        IReadOnlyList<Measurement> states = collector.Collect("http.resilience.circuit_breaker.state");

        Measurement open = Assert.Single(states);
        Assert.Equal(1, open.Value); // 0 Closed, 1 Open, 2 HalfOpen
        Assert.Equal("test", open.Tag("http.client.name"));
        Assert.Equal("shared", open.Tag("http.resilience.authority"));

        // The shared pipeline names no destination, so it carries neither semantic-convention tag rather
        // than a placeholder value. An invented server.address would be worse than a missing one.
        Assert.Null(open.Tag("server.address"));
        Assert.Null(open.Tag("server.port"));
    }

    /// <summary>
    /// A breaker that belongs to one authority also carries <c>server.address</c> and <c>server.port</c>, the
    /// semantic-convention pair <c>System.Net.Http</c> tags its own series with.
    /// </summary>
    /// <remarks>
    /// Without them an operator correlating breaker state against <c>http.client.request.duration</c> has to
    /// split <c>scheme://host:port</c> inside the query. They add no series -- both are functionally
    /// determined by the authority already present -- so this is a join key, not new cardinality.
    /// <para>
    /// Fails if the gauge goes back to emitting only the authority string.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task CircuitBreakerState_CarriesTheSemanticConventionDestinationTags()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled()
                .Set("CircuitBreaker:MinimumThroughput", "2")
                .Set("CircuitBreaker:FailureRatio", "0.1")
                .Set("Retry:Enabled", "false")
                .Set("PipelineSelection:Mode", "ByAuthority")
                .Set("PipelineSelection:Authorities:0", "http://origin.test"),
            new RecordingHandler(HttpStatusCode.InternalServerError));

        using var collector = new GaugeCollector(harness.Services);

        for (int i = 0; i < 10; i++)
        {
            try
            {
                (await harness.GetAsync()).Dispose();
            }
            catch (BrokenCircuitException)
            {
                break;
            }
        }

        Measurement open = Assert.Single(collector.Collect("http.resilience.circuit_breaker.state"));

        Assert.Equal("http://origin.test", open.Tag("http.resilience.authority"));
        Assert.Equal("origin.test", open.Tag("server.address"));
        Assert.Equal(80, open.Tag("server.port"));
    }

    [Fact]
    public async Task RateLimiterStatistics_ArePublishedAsGauges()
    {
        var gate = new TaskCompletionSource();
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled()
                .Set("RateLimiter:Enabled", "true")
                .Set("RateLimiter:PermitLimit", "2")
                .Set("RateLimiter:Window", "01:00:00"),
            new RecordingHandler(async (request, _, cancellationToken) =>
            {
                await gate.Task.WaitAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };
            }));

        using var collector = new GaugeCollector(harness.Services);

        Task<HttpResponseMessage> held = harness.GetAsync();
        while (harness.Origin.Count == 0)
        {
            await Task.Delay(10);
        }

        // Not Assert.Single any more: a client with a rate limiter also has a displaced concurrency
        // backstop, and both are now reported on one instrument, told apart by kind. The previous
        // assertion would have passed for either one of them alone, which is what let the backstop go
        // unreported for as long as it did.
        Measurement permits = Single(collector, "available_permits", "rate");
        Measurement queued = Single(collector, "queued_requests", "rate");

        gate.SetResult();
        (await held).Dispose();

        Assert.Equal(1, permits.Value);
        Assert.Equal("test", permits.Tag("http.client.name"));
        Assert.Equal(0, queued.Value);
    }

    /// <summary>
    /// The concurrency limiter's permits and queue depth are gauged, on the same instruments as the rate
    /// limiter's and told apart by <c>http.resilience.limiter.kind</c>.
    /// </summary>
    /// <remarks>
    /// This queue is the sharper of the two, and it was the one with no instrument. Both limiters sit
    /// <i>outside</i> <c>Timeout:Total</c> -- the platform's ordering, and what makes one permit cover a whole
    /// logical request -- so time spent waiting for a slot is bounded only by <c>Timeout:Client</c> and the
    /// caller's token. <c>ConcurrencyLimiter:QueueLimit</c> may be as high as 1,000, and the only signal was a
    /// Warning emitted when the queue <i>overflowed</i>, which is the difference between an alert and a
    /// post-mortem.
    /// <para>
    /// Polly builds the limiter itself when handed <c>DefaultRateLimiterOptions</c>, so there was no instance
    /// to read statistics from. The limiter is now constructed here and passed in, the way the rate limiter
    /// already was. Fails if it goes back to <c>DefaultRateLimiterOptions</c>.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ConcurrencyLimiterStatistics_ArePublishedAsGauges()
    {
        var gate = new TaskCompletionSource();
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled()
                .Set("Retry:Enabled", "false")
                .Set("ConcurrencyLimiter:Enabled", "true")
                .Set("ConcurrencyLimiter:Limit", "1")
                .Set("ConcurrencyLimiter:QueueLimit", "5"),
            new RecordingHandler(async (request, _, cancellationToken) =>
            {
                await gate.Task.WaitAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };
            }));

        using var collector = new GaugeCollector(harness.Services);

        Task<HttpResponseMessage> held = harness.GetAsync();
        while (harness.Origin.Count == 0)
        {
            await Task.Delay(10);
        }

        // A second request cannot get the only slot, so it waits in the queue -- which is the number this
        // test exists for.
        Task<HttpResponseMessage> queuedRequest = harness.GetAsync();
        Measurement queued = Single(collector, "queued_requests", "concurrency");
        while (queued.Value == 0)
        {
            await Task.Delay(10);
            queued = Single(collector, "queued_requests", "concurrency");
        }

        Measurement permits = Single(collector, "available_permits", "concurrency");

        gate.SetResult();
        (await held).Dispose();
        (await queuedRequest).Dispose();

        Assert.Equal(0, permits.Value);
        Assert.Equal(1, queued.Value);
        Assert.Equal("test", permits.Tag("http.client.name"));
    }

    /// <summary>
    /// The concurrency backstop is reported too, which is the number nobody configured.
    /// </summary>
    /// <remarks>
    /// The backstop exists because the platform's limiter slot is never empty and its 1,000-permit default was
    /// invisible. A control surfaced in configuration and not in telemetry is only half surfaced: an operator
    /// can read the number but cannot see how close the client is to it.
    /// <para>
    /// Only the backstop that has a handler of its own is reported -- the displaced one, present whenever a
    /// rate limiter has taken the platform's slot. The <i>undisplaced</i> backstop lives inside the standard
    /// handler and is one limiter per pipeline, so under <c>ByAuthority</c> handing Polly a single instance
    /// would silently change a per-authority bound into a per-client one. That gap is documented rather than
    /// closed. Fails if the displaced backstop stops being tracked.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheDisplacedConcurrencyBackstop_IsReportedToo()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled()
                .Set("Retry:Enabled", "false")
                .Set("RateLimiter:Enabled", "true")
                .Set("RateLimiter:PermitLimit", "10")
                .Set("RateLimiter:Window", "01:00:00")
                .Set("ConcurrencyLimiter:Backstop", "7"),
            new RecordingHandler(HttpStatusCode.OK));

        using var collector = new GaugeCollector(harness.Services);
        (await harness.GetAsync()).Dispose();

        Assert.Equal(7, Single(collector, "available_permits", "backstop").Value);
    }

    private static Measurement Single(GaugeCollector collector, string instrument, string kind) =>
        Assert.Single(
            collector.Collect($"http.resilience.limiter.{instrument}"),
            m => Equals(m.Tag("http.resilience.limiter.kind"), kind));

    /// <summary>
    /// The gauges exist once a client has been created, without waiting for a request to be sent.
    /// </summary>
    /// <remarks>
    /// Not "before any client is created", which was the first thing this asserted and was not worth having:
    /// the breaker gauge reports nothing until a breaker transitions, and a client's limiter does not exist
    /// until the container creates it, so an earlier meter publishes an empty series and nothing more.
    /// Client creation is where both first have something to say.
    /// </remarks>
    [Fact]
    public async Task Gauges_ArePublished_WhenTheClientIsCreated()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled()
                .Set("RateLimiter:Enabled", "true")
                .Set("RateLimiter:PermitLimit", "2")
                .Set("RateLimiter:Window", "01:00:00"));

        using var collector = new GaugeCollector(harness.Services);
        Assert.Contains("http.resilience.circuit_breaker.state", collector.PublishedInstruments);
        Assert.Contains("http.resilience.limiter.available_permits", collector.PublishedInstruments);
    }

    /// <summary>
    /// Observable, not synchronous: the brief is explicit that limiter statistics must not be read on the
    /// request path. An <c>ObservableGauge</c> can only be read by a collection.
    /// </summary>
    [Fact]
    public async Task Gauges_AreObservable_SoNothingIsPolledOnTheRequestPath()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(Settings.Enabled());
        using var collector = new GaugeCollector(harness.Services);

        Assert.All(
            collector.Instruments.Where(i => i.Name.StartsWith("http.resilience.", StringComparison.Ordinal)),
            instrument => Assert.True(
                instrument.IsObservable,
                $"{instrument.Name} must be observable so it is read per collection, not per request."));
    }
}

/// <summary>Records what the package's own meter publishes, and forces a collection on demand.</summary>
internal sealed class GaugeCollector : IDisposable
{
    private readonly MeterListener _listener;
    private readonly List<Instrument> _instruments = [];
    private readonly List<Instrument> _completed = [];
    private readonly List<Measurement> _measurements = [];

    private readonly IMeterFactory? _scope;

    /// <param name="services">
    /// Restricts collection to the meter this container published. The package's meter name is process-wide,
    /// so a collector that did not filter would see every other live container's gauges as well -- which in a
    /// parallel test run makes any count assertion a coin flip. IMeterFactory stamps each meter with the
    /// factory that created it, which is per container, so that is the discriminator.
    /// </param>
    public GaugeCollector(IServiceProvider? services = null)
    {
        _scope = services?.GetRequiredService<IMeterFactory>();

        _listener = new MeterListener
        {
            MeasurementsCompleted = (instrument, _) =>
            {
                lock (_instruments)
                {
                    _completed.Add(instrument);
                }
            },
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name != HttpResilienceTelemetryExtensions.MeterName ||
                    (_scope is not null && !ReferenceEquals(instrument.Meter.Scope, _scope)))
                {
                    return;
                }

                lock (_instruments)
                {
                    _instruments.Add(instrument);
                }

                listener.EnableMeasurementEvents(instrument);
            }
        };

        _listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) => Record(instrument, value, tags));
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
        _listener.Start();
    }

    public IReadOnlyList<Instrument> Instruments
    {
        get
        {
            lock (_instruments)
            {
                return [.. _instruments];
            }
        }
    }

    public IReadOnlyList<string> PublishedInstruments => [.. Instruments.Select(i => i.Name)];

    /// <summary>Instruments that have been published and not yet retired by their meter being disposed.</summary>
    public IReadOnlyList<Instrument> LiveInstruments
    {
        get
        {
            lock (_instruments)
            {
                return [.. _instruments.Except(_completed)];
            }
        }
    }

    public IReadOnlyList<Measurement> Collect(string instrumentName)
    {
        lock (_measurements)
        {
            _measurements.Clear();
        }

        _listener.RecordObservableInstruments();

        lock (_measurements)
        {
            return [.. _measurements.Where(m => m.Instrument == instrumentName)];
        }
    }

    public void Dispose() => _listener.Dispose();

    private void Record(Instrument instrument, long value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var copied = new List<KeyValuePair<string, object?>>(tags.Length);
        foreach (KeyValuePair<string, object?> tag in tags)
        {
            copied.Add(tag);
        }

        lock (_measurements)
        {
            _measurements.Add(new Measurement(instrument.Name, value, copied));
        }
    }
}

internal sealed record Measurement(string Instrument, long Value, List<KeyValuePair<string, object?>> Tags)
{
    public object? Tag(string key) => Tags.FirstOrDefault(t => t.Key == key).Value;
}

/// <summary>
/// The meter must stop reporting when the container that published it goes away.
/// </summary>
/// <remarks>
/// If it does not, every container built in a process leaves its gauges published for the life of that
/// process. In a test run that is cross-test pollution; in a service that builds a second provider it is
/// double-counted series that no scrape can attribute.
/// <para>
/// Production change that would make this fail: creating the <see cref="System.Diagnostics.Metrics.Meter"/>
/// with <c>new Meter(name)</c> instead of through <see cref="IMeterFactory"/>. The factory owns the meter's
/// lifetime and is disposed with the container; <c>HttpResilienceMetrics</c> itself is deliberately not
/// <c>IDisposable</c>, because a type that both creates a meter through a factory and disposes it would
/// dispose something it does not own. An earlier revision of this remark named dropping <c>IDisposable</c>
/// from <c>HttpResilienceMetrics</c> as the falsification, which was a change that could not be made -- there
/// has never been an <c>IDisposable</c> there to drop.
/// </para>
/// </remarks>
public class MetricsDisposalTests
{
    [Fact]
    public async Task Meter_IsDisposedWithTheContainer()
    {
        ResilienceHarness harness = ResilienceHarness.Create(Settings.Enabled());
        using var collector = new GaugeCollector(harness.Services);
        Assert.NotEmpty(collector.LiveInstruments);

        await harness.DisposeAsync();

        // A disposed Meter stops reporting: RecordObservableInstruments no longer reaches its callbacks, and
        // the listener is told the instruments are gone.
        Assert.Empty(collector.LiveInstruments);
    }
}
