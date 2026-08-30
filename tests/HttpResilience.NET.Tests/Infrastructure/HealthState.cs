using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HttpResilience.NET.Tests.Infrastructure;

/// <summary>
/// Reads circuit breaker state back out through the public health check, rather than reaching into internals.
/// </summary>
internal static class HealthState
{
    public static HealthReport Report(IServiceProvider services) =>
        services.GetRequiredService<HealthCheckService>().CheckHealthAsync().GetAwaiter().GetResult();

    public static IReadOnlyDictionary<string, object> Data(IServiceProvider services) =>
        Report(services).Entries["http-resilience"].Data;

    /// <summary>Breaker keys currently reported as anything other than Closed.</summary>
    public static IReadOnlyList<string> NotClosed(IServiceProvider services) =>
        [.. Data(services).Where(e => !string.Equals(e.Value as string, "Closed", StringComparison.Ordinal))
            .Select(e => e.Key)];

    public static HealthStatus Status(IServiceProvider services) =>
        Report(services).Entries["http-resilience"].Status;
}
