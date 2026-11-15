using HttpResilience.NET.Internal;
using Polly.Telemetry;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Meter names to register with OpenTelemetry, and one enricher that fills a gap in Polly's metrics.
/// </summary>
public static class HttpResilienceTelemetryExtensions
{
    /// <summary>
    /// The Polly meter name, for <c>metrics.AddMeter(...)</c>. Carries retry counts, circuit breaker
    /// transitions and per-strategy timings.
    /// </summary>
    public const string PollyMeterName = "Polly";

    /// <summary>
    /// This package's meter, for <c>metrics.AddMeter(...)</c>: circuit breaker state and limiter saturation.
    /// </summary>
    /// <remarks>
    /// Observable gauges, read once per collection:
    /// <list type="bullet">
    /// <item><description><c>http_resilience.circuit_breaker.state</c>: three series per breaker, tagged <c>http_resilience.circuit_breaker.state</c> = <c>closed</c>, <c>open</c> or <c>half_open</c>, with 1 for the breaker's current state and 0 for the others. Every breaker is reported, as closed, from the first collection. Tags: <c>http_resilience.client.name</c>, <c>http_resilience.authority</c>, and <c>server.address</c> / <c>server.port</c> when known.</description></item>
    /// <item><description><c>http_resilience.limiter.available_permits</c>: permits available now.</description></item>
    /// <item><description><c>http_resilience.limiter.queued_requests</c>: requests waiting for a permit.</description></item>
    /// </list>
    /// Limiter gauges are tagged <c>http_resilience.client.name</c> and <c>http_resilience.limiter.kind</c>
    /// (<c>rate</c>, <c>concurrency</c> or <c>backstop</c>); a per-authority backstop (a hedged client, or a
    /// <c>ByAuthority</c> client without a rate limiter) also carries <c>http_resilience.authority</c>. All tag
    /// values come from configuration, never from
    /// request data.
    /// </remarks>
    public const string MeterName = "HttpResilience.NET";

    /// <summary>
    /// Adds an <c>error.type</c> tag to Polly's resilience metrics for exception outcomes, carrying the
    /// exception type name. Calling this more than once has no additional effect.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection, for chaining.</returns>
    /// <remarks>
    /// The platform tags <c>error.type</c> with the status code of every response outcome; this adds the
    /// exception type for failed attempts (connection failures, timeouts), so dashboards filtering on
    /// <c>error.type</c> see both.
    /// <para>
    /// Applies to every Polly pipeline in the container, not only this package's. It never overwrites an
    /// existing tag. This package creates no spans; <c>System.Net.Http</c> already emits one per attempt.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code language="csharp">
    /// builder.Services.AddHttpResilienceTelemetry();
    ///
    /// builder.Services.AddOpenTelemetry().WithMetrics(metrics => metrics
    ///     .AddMeter(HttpResilienceTelemetryExtensions.PollyMeterName)   // retries, breaker events
    ///     .AddMeter(HttpResilienceTelemetryExtensions.MeterName)        // breaker state, limiter saturation
    ///     .AddMeter("System.Net.Http"));                                // request duration, connection pool
    /// </code>
    /// </example>
    public static IServiceCollection AddHttpResilienceTelemetry(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.Configure<TelemetryOptions>(options =>
        {
            if (!options.MeteringEnrichers.Any(static e => e is HttpResilienceMeteringEnricher))
            {
                options.MeteringEnrichers.Add(new HttpResilienceMeteringEnricher());
            }
        });

        return services;
    }
}
