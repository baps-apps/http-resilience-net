using HttpResilience.NET.Internal;
using HttpResilience.NET.Tests.Internal;
using Microsoft.Extensions.DependencyInjection;

namespace HttpResilience.NET.Tests.Infrastructure;

/// <summary>
/// Reads circuit breaker state from the published <c>http_resilience.circuit_breaker.state</c> gauge, the
/// signal an operator sees.
/// </summary>
internal static class BreakerState
{
    private const string Gauge = "http_resilience.circuit_breaker.state";

    /// <summary>
    /// Every breaker the gauge reports, keyed <c>client -&gt; authority</c>, with the state it is in: 0 closed,
    /// 1 open, 2 half-open. The gauge itself emits one series per state, 1 for the current one.
    /// </summary>
    public static IReadOnlyDictionary<string, long> All(IServiceProvider services)
    {
        _ = services.GetRequiredService<HttpResilienceMetrics>();
        using var collector = new GaugeCollector(services);

        return collector.Collect(Gauge)
            .Where(m => m.Value == 1)
            .ToDictionary(
                m => $"{m.Tag("http_resilience.client.name")} -> {m.Tag("http_resilience.authority")}",
                m => (string?)m.Tag("http_resilience.circuit_breaker.state") switch
                {
                    "open" => 1L,
                    "half_open" => 2L,
                    _ => 0L
                },
                StringComparer.Ordinal);
    }

    /// <summary>Breakers currently reported as anything other than Closed.</summary>
    public static IReadOnlyList<string> NotClosed(IServiceProvider services) =>
        [.. All(services).Where(e => e.Value != 0).Select(e => e.Key)];
}
