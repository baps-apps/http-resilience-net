using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Reflection;
using System.Threading.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics;
using Microsoft.Extensions.Options;

namespace HttpResilience.NET.Internal;

/// <summary>
/// Which of the three limiters a gauge measurement belongs to.
/// </summary>
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
internal sealed class HttpResilienceMetrics
{
    private static readonly string[] _kindNames = ["rate", "concurrency", "backstop"];

    /// <summary>The breaker states, as the <c>http_resilience.circuit_breaker.state</c> attribute spells them.</summary>
    private static readonly (CircuitState State, string Name)[] _states =
        [(CircuitState.Closed, "closed"), (CircuitState.Open, "open"), (CircuitState.HalfOpen, "half_open")];

    /// <summary>The package version, without the source-revision suffix, so exporters can tell versions apart.</summary>
    internal static readonly string? PackageVersion = typeof(HttpResilienceMetrics).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0];

    private readonly Meter _meter;
    private int _published;
    private readonly CircuitBreakerStateTracker _tracker;
    private readonly HttpResilienceRegistration? _registration;
    private readonly IOptionsMonitor<HttpResilienceOptions>? _options;
    private CircuitKey[]? _knownBreakers;
    private readonly ConcurrentDictionary<(string Client, LimiterKind Kind, string? Authority), RateLimiter> _limiters = new();
    private readonly ConcurrentDictionary<string, (string? Host, int? Port)> _authorities =
        new(StringComparer.Ordinal);

    public HttpResilienceMetrics(
        IMeterFactory meterFactory,
        CircuitBreakerStateTracker tracker,
        HttpResilienceRegistration? registration = null,
        IOptionsMonitor<HttpResilienceOptions>? options = null)
    {
        _tracker = tracker;
        _registration = registration;
        _options = options;
        _meter = meterFactory.Create(
            new MeterOptions(HttpResilienceTelemetryExtensions.MeterName) { Version = PackageVersion });
    }

    /// <summary>
    /// Publishes the three gauges, once; later calls do nothing. Called when the first handler chain is built,
    /// so every breaker the clients can have is reported, closed, from the next collection.
    /// </summary>
    public void Publish()
    {
        if (Interlocked.Exchange(ref _published, 1) == 1)
        {
            return;
        }

        // OpenTelemetry's state-metric shape: one series per state, 1 for the state the breaker is in.
        _meter.CreateObservableGauge(
            "http_resilience.circuit_breaker.state",
            ObserveCircuitStates,
            unit: "{breaker}",
            description: "Circuit breakers in the state named by http_resilience.circuit_breaker.state " +
                "(closed, open or half_open): 1 for a breaker's current state, 0 for the others.");

        _meter.CreateObservableGauge(
            "http_resilience.limiter.available_permits",
            () => ObserveLimiters(static statistics => statistics.CurrentAvailablePermits),
            unit: "{permit}",
            description: "Permits this limiter could grant right now. Tagged by limiter kind.");

        _meter.CreateObservableGauge(
            "http_resilience.limiter.queued_requests",
            () => ObserveLimiters(static statistics => statistics.CurrentQueuedCount),
            unit: "{request}",
            description: "Requests currently waiting for a permit from this limiter. Tagged by limiter kind.");
    }

    /// <summary>
    /// Registers a client's limiter for observation, at the point it is created.
    /// </summary>
    /// <param name="clientName">The client that owns the limiter.</param>
    /// <param name="kind">Which of the client's limiters it is.</param>
    /// <param name="limiter">The instance to read.</param>
    /// <param name="authority">
    /// The authority a per-authority limiter bounds, from the allow-list; <see langword="null"/> for a
    /// per-client limiter.
    /// </param>
    public void Track(string clientName, LimiterKind kind, RateLimiter limiter, string? authority = null) =>
        _limiters[(clientName, kind, authority)] = limiter;

    private IEnumerable<Measurement<int>> ObserveCircuitStates()
    {
        // Every breaker the clients can have, reported closed until it reports otherwise, so a breaker that never
        // changed state still has a series. Any key the tracker holds beyond them is reported too.
        CircuitKey[] known = _knownBreakers ??= KnownBreakers();
        IEnumerable<CircuitKey> keys = known.Concat(_tracker.Enumerate().Select(pair => pair.Key)).Distinct();

        foreach (CircuitKey key in keys)
        {
            CircuitState current = _tracker.GetState(key);
            (string? host, int? port) = _authorities.GetOrAdd(key.Authority, ParseAuthority);

            foreach ((CircuitState state, string name) in _states)
            {
                int value = state == current ? 1 : 0;
                yield return host is null
                    ? new Measurement<int>(
                        value,
                        new KeyValuePair<string, object?>("http_resilience.client.name", key.Client),
                        new KeyValuePair<string, object?>("http_resilience.authority", key.Authority),
                        new KeyValuePair<string, object?>("http_resilience.circuit_breaker.state", name))
                    : new Measurement<int>(
                        value,
                        new KeyValuePair<string, object?>("http_resilience.client.name", key.Client),
                        new KeyValuePair<string, object?>("http_resilience.authority", key.Authority),
                        new KeyValuePair<string, object?>("http_resilience.circuit_breaker.state", name),
                        new KeyValuePair<string, object?>("server.address", host),
                        new KeyValuePair<string, object?>("server.port", port));
            }
        }
    }

    /// <summary>
    /// The breakers of every enabled client this package registered, from its live options. Computed once:
    /// clients and options are fixed for the life of the container. A client whose options fail validation has
    /// no pipeline, so it is skipped rather than failing the collection.
    /// </summary>
    private CircuitKey[] KnownBreakers()
    {
        if (_registration is null || _options is null)
        {
            return [];
        }

        var keys = new List<CircuitKey>();
        foreach (RegisteredClient client in _registration.RegisteredClients)
        {
            HttpResilienceOptions options;
            try
            {
                options = _options.Get(client.Name);
            }
            catch (OptionsValidationException)
            {
                continue;
            }

            if (options.Enabled)
            {
                keys.AddRange(PipelineKeySelector.BreakerKeys(options, client.Kind)
                    .Select(authority => new CircuitKey(client.Name, authority)));
            }
        }

        return [.. keys];
    }

    /// <summary>
    /// Splits a pipeline key back into its host and port, or reports neither.
    /// </summary>
    private static (string? Host, int? Port) ParseAuthority(string authority) =>
        Uri.TryCreate(authority, UriKind.Absolute, out Uri? uri) && !string.IsNullOrEmpty(uri.Host)
            ? (uri.Host, uri.Port)
            : (null, null);

    private IEnumerable<Measurement<long>> ObserveLimiters(Func<RateLimiterStatistics, long> select)
    {
        foreach (((string Client, LimiterKind Kind, string? Authority) key, RateLimiter limiter) in _limiters)
        {
            if (TryReadStatistics(key, limiter) is not { } statistics)
            {
                continue;
            }

            if (key.Authority is null)
            {
                yield return new Measurement<long>(
                    select(statistics),
                    new KeyValuePair<string, object?>("http_resilience.client.name", key.Client),
                    new KeyValuePair<string, object?>("http_resilience.limiter.kind", KindName(key.Kind)));
                continue;
            }

            // The breaker gauge's destination tags too, so the two join on server.address; none for "shared".
            (string? host, int? port) = _authorities.GetOrAdd(key.Authority, ParseAuthority);
            yield return host is null
                ? new Measurement<long>(
                    select(statistics),
                    new KeyValuePair<string, object?>("http_resilience.client.name", key.Client),
                    new KeyValuePair<string, object?>("http_resilience.limiter.kind", KindName(key.Kind)),
                    new KeyValuePair<string, object?>("http_resilience.authority", key.Authority))
                : new Measurement<long>(
                    select(statistics),
                    new KeyValuePair<string, object?>("http_resilience.client.name", key.Client),
                    new KeyValuePair<string, object?>("http_resilience.limiter.kind", KindName(key.Kind)),
                    new KeyValuePair<string, object?>("http_resilience.authority", key.Authority),
                    new KeyValuePair<string, object?>("server.address", host),
                    new KeyValuePair<string, object?>("server.port", port));
        }
    }

    private static string KindName(LimiterKind kind)
    {
        int index = (int)kind;
        return (uint)index < (uint)_kindNames.Length ? _kindNames[index] : kind.ToString();
    }

    /// <summary>
    /// Reads a limiter's statistics, treating a disposed limiter as one with nothing to report.
    /// </summary>
    private RateLimiterStatistics? TryReadStatistics((string Client, LimiterKind Kind, string? Authority) key, RateLimiter limiter)
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
