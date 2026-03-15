using HttpResilience.NET.Internal;
using Microsoft.Extensions.DependencyInjection;
using Polly.Telemetry;

namespace HttpResilience.NET.Extensions;

/// <summary>
/// Extensions and constants for enabling telemetry enrichment for HttpResilience.NET using Polly telemetry.
/// </summary>
public static class HttpResilienceTelemetryExtensions
{
    /// <summary>
    /// Meter name used by Polly for resilience metrics. Use this constant when configuring your metrics pipeline
    /// (e.g. <c>metrics.AddMeter(HttpResilienceTelemetryExtensions.PollyMeterName)</c>) so resilience events
    /// are collected and exported.
    /// </summary>
    public const string PollyMeterName = "Polly";

    /// <summary>
    /// Registers a minimal Microsoft-style resilience telemetry enricher that adds tags such as
    /// error.type, request.name, and request.dependency.name to Polly resilience metrics.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddHttpResilienceTelemetry(this IServiceCollection services)
    {
        services.Configure<TelemetryOptions>(options =>
        {
            options.MeteringEnrichers.Add(new HttpResilienceMeteringEnricher());
        });

        return services;
    }
}

