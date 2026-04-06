using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HttpResilience.NET.Internal;

/// <summary>
/// Reports the aggregate circuit breaker state across all tracked HTTP clients.
/// Returns <see cref="HealthStatus.Healthy"/> when all breakers are closed,
/// <see cref="HealthStatus.Degraded"/> when any breaker is open or half-open.
/// </summary>
internal sealed class HttpResilienceHealthCheck : IHealthCheck
{
    private readonly CircuitBreakerStateTracker _tracker;

    /// <summary>Initializes a new instance with the given <paramref name="tracker"/>.</summary>
    public HttpResilienceHealthCheck(CircuitBreakerStateTracker tracker) => _tracker = tracker;

    /// <inheritdoc/>
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var states = _tracker.GetAllStates();
        var data = new Dictionary<string, object>();
        var unhealthy = new List<string>();

        foreach (var (clientName, state) in states)
        {
            data[clientName] = state.ToString();
            if (state != CircuitState.Closed)
                unhealthy.Add($"{clientName}={state}");
        }

        if (unhealthy.Count == 0)
            return Task.FromResult(HealthCheckResult.Healthy("All circuit breakers are closed.", data));

        var description = $"Circuit breakers not closed: {string.Join(", ", unhealthy)}";
        return Task.FromResult(HealthCheckResult.Degraded(description, data: data));
    }
}
