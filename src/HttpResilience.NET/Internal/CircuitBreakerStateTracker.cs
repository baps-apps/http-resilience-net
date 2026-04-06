using System.Collections.Concurrent;

namespace HttpResilience.NET.Internal;

/// <summary>
/// Possible states for a circuit breaker.
/// </summary>
public enum CircuitState
{
    /// <summary>Circuit is closed — requests flow normally.</summary>
    Closed,
    /// <summary>Circuit is open — requests are rejected.</summary>
    Open,
    /// <summary>Circuit is half-open — a trial request is allowed.</summary>
    HalfOpen
}

/// <summary>
/// Thread-safe tracker for circuit breaker state per named HTTP client.
/// Updated by Polly circuit breaker callbacks; read by health checks.
/// </summary>
public sealed class CircuitBreakerStateTracker
{
    private readonly ConcurrentDictionary<string, CircuitState> _states = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Reports that the circuit breaker for the given client has opened.</summary>
    public void ReportOpened(string clientName) => _states[clientName] = CircuitState.Open;

    /// <summary>Reports that the circuit breaker for the given client is half-open.</summary>
    public void ReportHalfOpen(string clientName) => _states[clientName] = CircuitState.HalfOpen;

    /// <summary>Reports that the circuit breaker for the given client has closed.</summary>
    public void ReportClosed(string clientName) => _states[clientName] = CircuitState.Closed;

    /// <summary>Gets the current state for the given client. Returns <see cref="CircuitState.Closed"/> if unknown.</summary>
    public CircuitState GetState(string clientName) =>
        _states.TryGetValue(clientName, out var state) ? state : CircuitState.Closed;

    /// <summary>Returns a snapshot of all tracked client states.</summary>
    public Dictionary<string, CircuitState> GetAllStates() => new(_states, StringComparer.OrdinalIgnoreCase);

    /// <summary>True if any tracked circuit breaker is currently open.</summary>
    public bool HasOpenCircuits => _states.Values.Any(s => s == CircuitState.Open);
}
