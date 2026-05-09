using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HttpResilience.NET.Internal;

/// <summary>
/// Reports the aggregate circuit breaker state across all tracked HTTP clients.
/// Returns <see cref="HealthStatus.Healthy"/> when all breakers are closed,
/// <see cref="HealthStatus.Degraded"/> when any breaker is open or half-open.
/// </summary>
internal sealed class HttpResilienceHealthCheck : IHealthCheck
{
    // Indexed by (int)CircuitState to avoid boxing/allocation from Enum.ToString.
    private static readonly string[] _stateNames = { "Closed", "Open", "HalfOpen" };

    private readonly CircuitBreakerStateTracker _tracker;

    /// <summary>Initializes a new instance with the given <paramref name="tracker"/>.</summary>
    public HttpResilienceHealthCheck(CircuitBreakerStateTracker tracker) => _tracker = tracker;

    /// <inheritdoc/>
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        Dictionary<string, object>? data = null;
        List<string>? unhealthy = null;

        foreach (var (clientName, state) in _tracker.Enumerate())
        {
            string stateName = StateName(state);
            (data ??= new Dictionary<string, object>())[clientName] = stateName;
            if (state != CircuitState.Closed)
            {
                (unhealthy ??= new List<string>()).Add($"{clientName}={stateName}");
            }
        }

        if (unhealthy is null)
        {
            return Task.FromResult(HealthCheckResult.Healthy("All circuit breakers are closed.", data));
        }

        var description = $"Circuit breakers not closed: {string.Join(", ", unhealthy)}";
        return Task.FromResult(HealthCheckResult.Degraded(description, data: data));
    }

    private static string StateName(CircuitState state)
    {
        int idx = (int)state;
        return (uint)idx < (uint)_stateNames.Length ? _stateNames[idx] : state.ToString();
    }
}
