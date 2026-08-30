using System.Collections.Concurrent;

namespace HttpResilience.NET.Internal;

/// <summary>The state of a single circuit breaker instance.</summary>
internal enum CircuitState
{
    /// <summary>Requests flow normally.</summary>
    Closed,

    /// <summary>Requests fail fast without reaching the dependency.</summary>
    Open,

    /// <summary>A trial request is allowed through to test recovery.</summary>
    HalfOpen
}

/// <summary>Identifies one circuit breaker instance.</summary>
/// <param name="Client">The named <see cref="HttpClient"/>.</param>
/// <param name="Authority">
/// The authority the breaker guards, or <see cref="PipelineKeySelector.SharedKey"/> when the client uses a
/// single pipeline for every host.
/// </param>
internal readonly record struct CircuitKey(string Client, string Authority)
{
    public override string ToString() => $"{Client} -> {Authority}";
}

/// <summary>
/// Tracks the live state of every circuit breaker this library configures, so a health check can report it.
/// </summary>
/// <remarks>
/// Keyed by client <b>and</b> authority. Under per-authority pipeline selection one client owns several
/// breakers, and collapsing them onto the client name alone means the last callback to fire overwrites the
/// others -- so one host recovering would mask another still being open.
/// </remarks>
internal sealed class CircuitBreakerStateTracker
{
    private readonly ConcurrentDictionary<CircuitKey, CircuitState> _states = new();

    public void Report(CircuitKey key, CircuitState state) => _states[key] = state;

    public CircuitState GetState(CircuitKey key) =>
        _states.TryGetValue(key, out CircuitState state) ? state : CircuitState.Closed;

    public IEnumerable<KeyValuePair<CircuitKey, CircuitState>> Enumerate() => _states;
}
