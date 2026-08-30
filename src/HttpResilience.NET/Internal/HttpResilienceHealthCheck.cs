using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HttpResilience.NET.Internal;

/// <summary>
/// Reports the aggregate state of every circuit breaker this library configures.
/// </summary>
/// <remarks>
/// This is a <b>dependency</b> health check and reports <see cref="HealthStatus.Degraded"/> at worst. An open
/// breaker means a downstream is unhealthy, not that this process is: a pod whose dependency is failing is
/// still able to serve traffic, and failing a liveness or readiness probe on it would remove capacity during
/// an outage and turn a partial degradation into a self-inflicted one.
/// </remarks>
internal sealed class HttpResilienceHealthCheck : IHealthCheck
{
    // Indexed by (int)CircuitState to avoid the allocation and boxing of Enum.ToString.
    private static readonly string[] _stateNames = ["Closed", "Open", "HalfOpen"];

    private readonly CircuitBreakerStateTracker _tracker;

    public HttpResilienceHealthCheck(CircuitBreakerStateTracker tracker) => _tracker = tracker;

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        Dictionary<string, object>? data = null;
        List<string>? notClosed = null;

        foreach ((CircuitKey key, CircuitState state) in _tracker.Enumerate())
        {
            string stateName = StateName(state);
            (data ??= [])[key.ToString()] = stateName;

            if (state is not CircuitState.Closed)
            {
                (notClosed ??= []).Add($"{key} = {stateName}");
            }
        }

        return Task.FromResult(notClosed is null
            ? HealthCheckResult.Healthy("All circuit breakers are closed.", data)
            : HealthCheckResult.Degraded(
                $"Circuit breakers not closed: {string.Join(", ", notClosed)}", data: data));
    }

    private static string StateName(CircuitState state)
    {
        int index = (int)state;
        return (uint)index < (uint)_stateNames.Length ? _stateNames[index] : state.ToString();
    }
}
