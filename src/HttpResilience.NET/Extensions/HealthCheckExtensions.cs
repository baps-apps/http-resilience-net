using HttpResilience.NET.Internal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HttpResilience.NET.Extensions;

/// <summary>
/// Extension methods for registering HTTP resilience health checks.
/// </summary>
public static class HealthCheckExtensions
{
    /// <summary>
    /// Adds a health check that reports the aggregate circuit breaker state across all HTTP clients
    /// configured with <see cref="ServiceCollectionExtensions.AddHttpClientWithResilience(IHttpClientBuilder,IConfiguration,int?)"/>.
    /// Returns <see cref="HealthStatus.Degraded"/> when any circuit breaker is open or half-open.
    /// <para><b>Use case:</b> Wire into Kubernetes readiness probes or ASP.NET health check endpoints
    /// so that traffic is shifted away when downstream dependencies are unhealthy.</para>
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="name">Health check name. Default: "http-resilience".</param>
    /// <param name="failureStatus">Status to report on failure. Default: <see cref="HealthStatus.Degraded"/>.</param>
    /// <param name="tags">Optional tags for filtering health checks.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddHttpResilienceHealthChecks(
        this IServiceCollection services,
        string name = "http-resilience",
        HealthStatus? failureStatus = null,
        IEnumerable<string>? tags = null)
    {
        services.TryAddSingleton<CircuitBreakerStateTracker>();

        services.AddHealthChecks()
            .Add(new HealthCheckRegistration(
                name,
                sp => new HttpResilienceHealthCheck(sp.GetRequiredService<CircuitBreakerStateTracker>()),
                failureStatus,
                tags));

        return services;
    }
}
