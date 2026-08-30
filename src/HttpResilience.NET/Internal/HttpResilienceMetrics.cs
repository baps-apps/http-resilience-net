using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Threading.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics;

namespace HttpResilience.NET.Internal;

/// <summary>
/// Which of the three limiters a gauge measurement belongs to.
/// </summary>
/// <remarks>
/// One instrument with a bounded dimension rather than three instruments, because an operator's question is
/// "how close is this client to shedding load", and the answer is whichever limiter is nearest its bound.
/// <para>
/// The values are the tag values, so renaming one is a telemetry break.
/// </para>
/// </remarks>
internal enum LimiterKind
{
    /// <summary>A configured rate limiter -- <c>RateLimiter:Enabled</c>.</summary>
    Rate,

    /// <summary>A configured concurrency cap -- <c>ConcurrencyLimiter:Enabled</c>.</summary>
    Concurrency,

    /// <summary>The concurrency backstop, in the handler it gets when a rate limiter displaces it.</summary>
    Backstop
}

/// <summary>
/// Publishes the two pieces of state neither Polly nor <c>System.Net.Http</c> exposes: whether a circuit
/// breaker is open right now, and how close each of a client's limiters is to shedding load.
/// </summary>
/// <remarks>
/// Deliberately three instruments and no more. Polly already counts breaker transition <i>events</i>, and a
/// counter answers "did it open" -- it cannot answer "is it open" after a scrape is missed or a collector
/// restarts, which is the question an operator asks during an incident. Limiter statistics already exist on
/// <see cref="RateLimiter.GetStatistics"/> and were simply never read.
/// <para>
/// Every instrument is an <c>ObservableGauge</c>, so the values are read once per collection and never on the
/// request path. Every dimension is fixed at registration: the client name comes from the registration and
/// the authority from the configured allow-list, so no tag value can originate from request data.
/// </para>
/// <para>
/// The meter comes from <see cref="IMeterFactory"/> rather than <c>new Meter(...)</c>, which is the platform's
/// answer to both halves of the ownership problem: the factory disposes the meter with the container, and it
/// stamps the meter with a scope identifying which container published it. A meter constructed directly is
/// never disposed -- an instance handed to <c>AddSingleton</c> is not disposed by the container either -- so a
/// process that builds more than one provider would publish every generation of the gauges at once, with
/// nothing in the measurement to say which was which.
/// </para>
/// <para>
/// It is constructed when the first client's pipeline is built, which is as early as it can be useful: the
/// tracker holds no breaker until one transitions, and a client's limiter does not exist until the container
/// creates it, so an earlier meter would publish an empty series and nothing more.
/// </para>
/// </remarks>
internal sealed class HttpResilienceMetrics
{
    private static readonly string[] _kindNames = ["rate", "concurrency", "backstop"];

    private readonly Meter _meter;
    private readonly CircuitBreakerStateTracker _tracker;
    private readonly ConcurrentDictionary<(string Client, LimiterKind Kind), RateLimiter> _limiters = new();
    private readonly ConcurrentDictionary<string, (string? Host, int? Port)> _authorities =
        new(StringComparer.Ordinal);

    public HttpResilienceMetrics(IMeterFactory meterFactory, CircuitBreakerStateTracker tracker)
    {
        _tracker = tracker;
        _meter = meterFactory.Create(HttpResilienceTelemetryExtensions.MeterName);

        _meter.CreateObservableGauge(
            "http.resilience.circuit_breaker.state",
            ObserveCircuitStates,
            unit: "{state}",
            description: "Circuit breaker state: 0 closed, 1 open, 2 half-open.");

        // One instrument per statistic rather than one per limiter kind. The kind is a tag, because the
        // operator's question is "how close is this client to shedding load" and the answer is whichever of
        // its limiters is nearest its bound -- which a single query over one instrument gives and three
        // instruments do not. Named `limiter` rather than `rate_limiter`: the concurrency cap and the
        // backstop report here too, and an instrument named for one of the three would be the kind of
        // misleading name this package refuses everywhere else.
        _meter.CreateObservableGauge(
            "http.resilience.limiter.available_permits",
            () => ObserveLimiters(static statistics => statistics.CurrentAvailablePermits),
            unit: "{permit}",
            description: "Permits this limiter could grant right now. Tagged by limiter kind.");

        _meter.CreateObservableGauge(
            "http.resilience.limiter.queued_requests",
            () => ObserveLimiters(static statistics => statistics.CurrentQueuedCount),
            unit: "{request}",
            description: "Requests currently waiting for a permit from this limiter. Tagged by limiter kind.");
    }

    /// <summary>
    /// Registers a client's limiter for observation, at the point the container creates it.
    /// </summary>
    /// <remarks>
    /// The limiter is a keyed singleton created on first resolve, so there is nothing to read before that.
    /// Hooking its construction rather than resolving it here keeps this type free of the service provider,
    /// which does not exist yet when it is built.
    /// <para>
    /// A client may report up to two limiters: a rate limiter and either a concurrency cap or the displaced
    /// backstop. Both are limiters whose instance this package owns. The <i>undisplaced</i> backstop is not
    /// here, because it lives inside the platform's own limiter slot where Polly constructs it -- one per
    /// pipeline, which is what makes it per authority under <c>ByAuthority</c>. Supplying an instance for it
    /// would turn that per-authority bound into a per-client one, so the gap is documented instead.
    /// </para>
    /// </remarks>
    public void Track(string clientName, LimiterKind kind, RateLimiter limiter) =>
        _limiters[(clientName, kind)] = limiter;

    private IEnumerable<Measurement<int>> ObserveCircuitStates()
    {
        foreach ((CircuitKey key, CircuitState state) in _tracker.Enumerate())
        {
            // server.address and server.port are the OpenTelemetry semantic-convention pair for a
            // destination, and they are what System.Net.Http tags its own series with -- so emitting them
            // here is what lets an operator join breaker state to request duration without splitting
            // "scheme://host:port" in the query. They add no series: both are functionally determined by the
            // authority already present, and the authority is kept because it is the pipeline key an
            // operator sees in Polly's pipeline.instance and in this package's own messages.
            //
            // http.client.name has no semantic-convention equivalent -- there is no attribute for a named
            // HttpClient -- so it stays as this package's own dimension.
            (string? host, int? port) = _authorities.GetOrAdd(key.Authority, ParseAuthority);

            yield return host is null
                ? new Measurement<int>(
                    (int)state,
                    new KeyValuePair<string, object?>("http.client.name", key.Client),
                    new KeyValuePair<string, object?>("http.resilience.authority", key.Authority))
                : new Measurement<int>(
                    (int)state,
                    new KeyValuePair<string, object?>("http.client.name", key.Client),
                    new KeyValuePair<string, object?>("http.resilience.authority", key.Authority),
                    new KeyValuePair<string, object?>("server.address", host),
                    new KeyValuePair<string, object?>("server.port", port));
        }
    }

    /// <summary>
    /// Splits a pipeline key back into its host and port, or reports neither.
    /// </summary>
    /// <remarks>
    /// The shared key (<see cref="PipelineKeySelector.SharedKey"/>) names no destination, and neither does
    /// any future key that is not an authority, so those series carry the two tags' absence rather than a
    /// placeholder value -- an invented <c>server.address</c> would be worse than a missing one. Memoised
    /// because the key set is bounded by configuration and this runs once per key per collection.
    /// </remarks>
    private static (string? Host, int? Port) ParseAuthority(string authority) =>
        Uri.TryCreate(authority, UriKind.Absolute, out Uri? uri) && !string.IsNullOrEmpty(uri.Host)
            ? (uri.Host, uri.Port)
            : (null, null);

    private IEnumerable<Measurement<long>> ObserveLimiters(Func<RateLimiterStatistics, long> select)
    {
        foreach (((string Client, LimiterKind Kind) key, RateLimiter limiter) in _limiters)
        {
            if (TryReadStatistics(key, limiter) is { } statistics)
            {
                yield return new Measurement<long>(
                    select(statistics),
                    new KeyValuePair<string, object?>("http.client.name", key.Client),
                    new KeyValuePair<string, object?>("http.resilience.limiter.kind", KindName(key.Kind)));
            }
        }
    }

    // Indexed by (int)LimiterKind to avoid the allocation and boxing of Enum.ToString on a collection path.
    private static string KindName(LimiterKind kind)
    {
        int index = (int)kind;
        return (uint)index < (uint)_kindNames.Length ? _kindNames[index] : kind.ToString();
    }

    /// <summary>
    /// Reads a limiter's statistics, treating a disposed limiter as one with nothing to report.
    /// </summary>
    /// <remarks>
    /// This type holds a strong reference to every limiter it publishes, and both are container singletons
    /// disposed in reverse order of creation -- an order neither controls. The limiter is created after this
    /// type (the pipeline configurator resolves the metrics first), so it is disposed first, and a collection
    /// landing in that window called <c>GetStatistics()</c> on a disposed limiter and threw. An exception out
    /// of an observable-instrument callback is not absorbed: it reaches whichever <c>MeterListener</c> is
    /// collecting, which for a real deployment means an instrument failure logged by the OpenTelemetry reader
    /// during shutdown -- the moment an operator is least able to tell a teardown artefact from a fault.
    /// <para>
    /// The entry is dropped as well as skipped, so a process that keeps scraping does not re-enter the
    /// catch on every collection.
    /// </para>
    /// </remarks>
    private RateLimiterStatistics? TryReadStatistics((string Client, LimiterKind Kind) key, RateLimiter limiter)
    {
        try
        {
            return limiter.GetStatistics();
        }
        catch (ObjectDisposedException)
        {
            _limiters.TryRemove(key, out _);
            return null;
        }
    }
}
