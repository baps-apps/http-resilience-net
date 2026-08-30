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
    /// This package's own meter, for <c>metrics.AddMeter(...)</c>. Carries circuit breaker state and limiter
    /// saturation -- the two things Polly and <c>System.Net.Http</c> do not publish.
    /// </summary>
    /// <remarks>
    /// Three <c>ObservableGauge</c> instruments, read once per collection and never on the request path:
    /// <list type="bullet">
    /// <item><description>
    /// <c>http.resilience.circuit_breaker.state</c> -- 0 closed, 1 open, 2 half-open. Tagged
    /// <c>http.client.name</c>, <c>http.resilience.authority</c>, and <c>server.address</c> /
    /// <c>server.port</c> when the authority names a destination.
    /// </description></item>
    /// <item><description>
    /// <c>http.resilience.limiter.available_permits</c> -- permits this limiter could grant right now.
    /// </description></item>
    /// <item><description>
    /// <c>http.resilience.limiter.queued_requests</c> -- requests currently waiting for a permit.
    /// </description></item>
    /// </list>
    /// The two limiter instruments are tagged <c>http.client.name</c> and
    /// <c>http.resilience.limiter.kind</c>, which is one of <c>rate</c>, <c>concurrency</c> or
    /// <c>backstop</c> -- one instrument with a bounded dimension rather than three instruments, because the
    /// operator's question is "how close is this client to shedding load" and the answer is whichever limiter
    /// is nearest its bound. Named <c>limiter</c> rather than <c>rate_limiter</c> for the same reason: the
    /// concurrency cap and the backstop report here too.
    /// <para>
    /// Every dimension is fixed at registration -- client name, allow-listed authority, and a limiter kind
    /// from a closed set -- so no tag value comes from request data and cardinality cannot grow with traffic.
    /// </para>
    /// </remarks>
    public const string MeterName = "HttpResilience.NET";

    /// <summary>
    /// Adds an <c>error.type</c> tag to Polly's resilience metrics for exception outcomes, carrying the
    /// exception type name. Calling this more than once has no additional effect.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection, for chaining.</returns>
    /// <remarks>
    /// Call this if your dashboards or alerts filter on <c>error.type</c>. Without it they silently miss every
    /// connection failure, DNS failure and timeout: <c>Microsoft.Extensions.Http.Resilience</c> already tags
    /// <c>error.type</c> with the status code when a <i>response</i> is the failure, but when an
    /// <i>exception</i> is the failure Polly emits <c>exception.type</c> instead. That one gap is the whole of
    /// what this adds.
    /// <para>
    /// It affects every Polly pipeline in the process, not only the ones this package registers, because
    /// Polly's telemetry options are container-wide. The tag it adds is an exception type name, so it is bounded
    /// by your code rather than by traffic.
    /// </para>
    /// <para>
    /// This package creates no spans on purpose. <c>System.Net.Http</c> already emits one <c>Activity</c> per
    /// <i>attempt</i>, so a retried or hedged call already appears as several sibling HTTP spans under the
    /// caller's span -- counting them is how you answer "was it retried, and how many times".
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

        // Polly's TelemetryOptions are resolved from the container, so this enricher reaches every Polly
        // pipeline registered in THIS container -- including pipelines another library registered through
        // AddResiliencePipeline, which this package knows nothing about. Measured: a second container in the
        // same process that does not call this method keeps its own enricher list, so the reach is the
        // container rather than the process. In a service with one provider those are the same thing, which
        // is why the distinction went unnoticed; in a test host or a process building several providers it
        // is not. That is deliberate rather than incidental: error.type missing on an
        // exception outcome is the same dashboard gap wherever the pipeline came from, the enricher only ever
        // adds a bounded exception type name, and it never overwrites a tag that is already there. Documented
        // because a method named for this package having container-wide reach should not be a surprise found
        // while debugging somebody else's metrics.
        services.Configure<TelemetryOptions>(options =>
        {
            // Idempotent: a second call must not double-tag every metric.
            if (!options.MeteringEnrichers.Any(static e => e is HttpResilienceMeteringEnricher))
            {
                options.MeteringEnrichers.Add(new HttpResilienceMeteringEnricher());
            }
        });

        return services;
    }
}
