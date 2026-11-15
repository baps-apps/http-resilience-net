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
/// Polly's transition counter answers "did it open", not "is it open"; a gauge survives a missed scrape. Both
/// are observable gauges, read once per collection;
/// <see cref="Gauges_AreObservable_SoNothingIsPolledOnTheRequestPath"/> asserts the instrument type.
/// </remarks>
public class MetricsTests
{
    [Fact(Timeout = TestTimeouts.Default)]
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
                (await harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken)).Dispose();
            }
            catch (BrokenCircuitException)
            {
                break;
            }
        }

        IReadOnlyList<Measurement> states = collector.Collect("http_resilience.circuit_breaker.state");

        // One series per state, as OpenTelemetry state metrics do: 1 for the state the breaker is in, 0 for the others.
        Assert.Equal(
            ["closed=0", "half_open=0", "open=1"],
            states.Select(m => $"{m.Tag("http_resilience.circuit_breaker.state")}={m.Value}").Order(StringComparer.Ordinal));

        Assert.All(states, m =>
        {
            Assert.Equal("test", m.Tag("http_resilience.client.name"));
            Assert.Equal("shared", m.Tag("http_resilience.authority"));

            // The shared pipeline names no destination, so it carries neither semantic-convention tag.
            Assert.Null(m.Tag("server.address"));
            Assert.Null(m.Tag("server.port"));
        });
    }

    /// <summary>
    /// A breaker is reported from the first collection, as closed, not only after its first transition, so
    /// "no series" never has to be read as "closed" and a dashboard shows every breaker the client has.
    /// </summary>
    /// <remarks>
    /// The keys come from the client's own allow-list: a standard <c>ByAuthority</c> client has one breaker per
    /// listed authority plus the shared one for the rest; a hedged client refuses unlisted authorities, so it has
    /// no shared breaker. Fails if the gauge reports only breakers that have changed state.
    /// </remarks>
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(false, false, "shared")]
    [InlineData(false, true, "http://a.test,http://b.test,shared")]
    [InlineData(true, true, "http://a.test,http://b.test")]
    public async Task EveryBreaker_IsReportedClosed_BeforeAnyRequest(bool hedged, bool byAuthority, string expected)
    {
        Settings settings = hedged ? Settings.Hedged() : Settings.Enabled();
        if (byAuthority)
        {
            settings = settings
                .Set("PipelineSelection:Authorities:0", "http://a.test")
                .Set("PipelineSelection:Authorities:1", "http://b.test");
            settings = hedged ? settings : settings.Set("PipelineSelection:Mode", "ByAuthority");
        }

        await using ResilienceHarness harness = ResilienceHarness.Create(settings, hedged: hedged);
        using var collector = new GaugeCollector(harness.Services);

        Measurement[] closed =
        [
            .. collector.Collect("http_resilience.circuit_breaker.state")
                .Where(m => Equals(m.Tag("http_resilience.circuit_breaker.state"), "closed"))
        ];

        Assert.All(closed, m => Assert.Equal(1, m.Value));
        Assert.Equal(
            expected.Split(','),
            closed.Select(m => (string)m.Tag("http_resilience.authority")!).Order(StringComparer.Ordinal));
    }

    /// <summary>A disabled client has no breaker, so it reports none.</summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ADisabledClient_ReportsNoBreaker()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(Settings.Enabled().Set("Enabled", "false"));
        using var collector = new GaugeCollector(harness.Services);

        Assert.Empty(collector.Collect("http_resilience.circuit_breaker.state"));
    }

    /// <summary>
    /// A breaker that belongs to one authority also carries <c>server.address</c> and <c>server.port</c>, the
    /// semantic-convention pair <c>System.Net.Http</c> tags its own series with.
    /// </summary>
    /// <remarks>
    /// Lets breaker state join to <c>http.client.request.duration</c> without splitting the authority string.
    /// No extra series: both derive from the authority. Fails if the gauge emits only the authority.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
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
                (await harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken)).Dispose();
            }
            catch (BrokenCircuitException)
            {
                break;
            }
        }

        Measurement open = Assert.Single(
            collector.Collect("http_resilience.circuit_breaker.state"),
            m => Equals(m.Tag("http_resilience.circuit_breaker.state"), "open") && m.Value == 1);

        Assert.Equal("http://origin.test", open.Tag("http_resilience.authority"));
        Assert.Equal("origin.test", open.Tag("server.address"));
        Assert.Equal(80, open.Tag("server.port"));
    }

    [Fact(Timeout = TestTimeouts.Default)]
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

        Task<HttpResponseMessage> held = harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken);
        await harness.Origin.WaitForCountAsync(1);

        // A client with a rate limiter also has a displaced concurrency backstop; both are reported on one
        // instrument, told apart by kind.
        Measurement permits = Single(collector, "available_permits", "rate");
        Measurement queued = Single(collector, "queued_requests", "rate");

        gate.SetResult();
        (await held).Dispose();

        Assert.Equal(1, permits.Value);
        Assert.Equal("test", permits.Tag("http_resilience.client.name"));
        Assert.Equal(0, queued.Value);
    }

    /// <summary>
    /// The concurrency limiter's permits and queue depth are gauged, on the same instruments as the rate
    /// limiter's and told apart by <c>http_resilience.limiter.kind</c>.
    /// </summary>
    /// <remarks>
    /// Concurrency queue wait is outside <c>Timeout:Total</c>, so it needs its own signal. The limiter is
    /// constructed here and passed to Polly so its statistics can be read. Fails if it goes back to
    /// <c>DefaultRateLimiterOptions</c>.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
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

        Task<HttpResponseMessage> held = harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken);
        await harness.Origin.WaitForCountAsync(1);

        // A second request cannot get the only slot, so it waits in the queue.
        Task<HttpResponseMessage> queuedRequest = harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken);
        Measurement queued = Single(collector, "queued_requests", "concurrency");
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            while (queued.Value == 0)
            {
                // The queue is filled by the limiter on another thread; nothing signals it, so poll with a deadline.
                await Task.Yield();
                deadline.Token.ThrowIfCancellationRequested();
                queued = Single(collector, "queued_requests", "concurrency");
            }
        }

        Measurement permits = Single(collector, "available_permits", "concurrency");

        gate.SetResult();
        (await held).Dispose();
        (await queuedRequest).Dispose();

        Assert.Equal(0, permits.Value);
        Assert.Equal(1, queued.Value);
        Assert.Equal("test", permits.Tag("http_resilience.client.name"));
    }

    /// <summary>
    /// The displaced concurrency backstop is reported too.
    /// </summary>
    /// <remarks>
    /// When a rate limiter takes the platform's slot, the backstop moves into its own handler with its own
    /// limiter. Fails if that limiter stops being tracked.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
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
        (await harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken)).Dispose();

        Assert.Equal(7, Single(collector, "available_permits", "backstop").Value);
    }

    /// <summary>
    /// A hedged client's backstop is reported once per listed authority, because that is the bound it enforces.
    /// </summary>
    /// <remarks>
    /// Fails if the endpoint limiter goes back to the platform's own instance, which nothing can read, or if
    /// one instance is shared across authorities (both would then report the same, combined, permit count).
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task AHedgedClientsBackstop_IsReportedPerAuthority()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Hedged()
                .Set("ConcurrencyLimiter:Backstop", "7")
                .Set("PipelineSelection:Authorities:1", "http://other.test"),
            new RecordingHandler(HttpStatusCode.OK),
            hedged: true);

        using var collector = new GaugeCollector(harness.Services);
        (await harness.GetAsync("http://origin.test/x", TestContext.Current.CancellationToken)).Dispose();
        (await harness.GetAsync("http://other.test/x", TestContext.Current.CancellationToken)).Dispose();

        Measurement[] backstops =
        [
            .. collector.Collect("http_resilience.limiter.available_permits")
                .Where(m => Equals(m.Tag("http_resilience.limiter.kind"), "backstop"))
        ];

        Assert.Equal(
            ["http://origin.test", "http://other.test"],
            backstops.Select(m => (string?)m.Tag("http_resilience.authority")).Order(StringComparer.Ordinal));
        Assert.All(backstops, m => Assert.Equal(7, m.Value));

        // The semantic-convention destination tags, as on the breaker gauge, so dashboards join on one key.
        Assert.Equal(
            ["origin.test", "other.test"],
            backstops.Select(m => (string?)m.Tag("server.address")).Order(StringComparer.Ordinal));
        Assert.All(backstops, m => Assert.Equal(80, m.Tag("server.port")));
    }

    /// <summary>
    /// A standard client's backstop is reported even when no rate limiter has displaced it.
    /// </summary>
    /// <remarks>
    /// One pipeline, so one per-client series with no authority tag. Fails if the platform's limiter slot goes
    /// back to building its own instance, which nothing can read.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task AStandardClientsBackstop_IsReported_WithoutARateLimiter()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled()
                .Set("Retry:Enabled", "false")
                .Set("ConcurrencyLimiter:Backstop", "7"),
            new RecordingHandler(HttpStatusCode.OK));

        using var collector = new GaugeCollector(harness.Services);
        (await harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken)).Dispose();

        Measurement backstop = Single(collector, "available_permits", "backstop");
        Assert.Equal(7, backstop.Value);
        Assert.Null(backstop.Tag("http_resilience.authority"));
    }

    /// <summary>
    /// Under <c>ByAuthority</c> the backstop is one limiter per authority, and is reported that way.
    /// </summary>
    /// <remarks>
    /// Fails if the series is missing, or if one instance is shared across authorities.
    /// <c>Backstop_IsPerAuthority_UnderByAuthoritySelection</c> pins the bound itself.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task AStandardClientsBackstop_IsReportedPerAuthority_UnderByAuthority()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled()
                .Set("Retry:Enabled", "false")
                .Set("ConcurrencyLimiter:Backstop", "7")
                .Set("PipelineSelection:Mode", "ByAuthority")
                .Set("PipelineSelection:Authorities:0", "http://a.test")
                .Set("PipelineSelection:Authorities:1", "http://b.test"),
            new RecordingHandler(HttpStatusCode.OK));

        using var collector = new GaugeCollector(harness.Services);
        (await harness.GetAsync("http://a.test/x", TestContext.Current.CancellationToken)).Dispose();
        (await harness.GetAsync("http://b.test/x", TestContext.Current.CancellationToken)).Dispose();

        Measurement[] backstops =
        [
            .. collector.Collect("http_resilience.limiter.available_permits")
                .Where(m => Equals(m.Tag("http_resilience.limiter.kind"), "backstop"))
        ];

        Assert.Equal(
            ["http://a.test", "http://b.test"],
            backstops.Select(m => (string?)m.Tag("http_resilience.authority")).Order(StringComparer.Ordinal));
        Assert.All(backstops, m => Assert.Equal(7, m.Value));
    }

    private static Measurement Single(GaugeCollector collector, string instrument, string kind) =>
        Assert.Single(
            collector.Collect($"http_resilience.limiter.{instrument}"),
            m => Equals(m.Tag("http_resilience.limiter.kind"), kind));

    /// <summary>
    /// The gauges exist once a client has been created, without waiting for a request to be sent.
    /// </summary>
    /// <remarks>
    /// The breaker gauge reports nothing until a transition, and a limiter exists only once the client is
    /// created.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task Gauges_ArePublished_WhenTheClientIsCreated()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled()
                .Set("RateLimiter:Enabled", "true")
                .Set("RateLimiter:PermitLimit", "2")
                .Set("RateLimiter:Window", "01:00:00"));

        using var collector = new GaugeCollector(harness.Services);
        Assert.Contains("http_resilience.circuit_breaker.state", collector.PublishedInstruments);
        Assert.Contains("http_resilience.limiter.available_permits", collector.PublishedInstruments);
    }

    /// <summary>
    /// Observable, not synchronous, so limiter statistics are never read on the request path.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task Gauges_AreObservable_SoNothingIsPolledOnTheRequestPath()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(Settings.Enabled());
        using var collector = new GaugeCollector(harness.Services);

        Assert.All(
            collector.Instruments.Where(i => i.Name.StartsWith("http_resilience.", StringComparison.Ordinal)),
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
    /// Restricts collection to this container's meter. The meter name is process-wide, so without this a
    /// parallel test run would see other containers' gauges. IMeterFactory stamps each meter with its factory.
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
/// Otherwise every container leaves its gauges published for the life of the process. Fails if the
/// <see cref="System.Diagnostics.Metrics.Meter"/> is created with <c>new Meter(name)</c> instead of through
/// <see cref="IMeterFactory"/>, which owns its lifetime.
/// </remarks>
public class MetricsDisposalTests
{
    [Fact(Timeout = TestTimeouts.Default)]
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
