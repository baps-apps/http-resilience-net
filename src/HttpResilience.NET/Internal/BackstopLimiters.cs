using System.Collections.Concurrent;
using System.Threading.RateLimiting;

namespace HttpResilience.NET.Internal;

/// <summary>
/// The concurrency backstop in the platform's limiter slot: one limiter per pipeline, held by this package so
/// it can be logged and reported.
/// </summary>
/// <remarks>
/// The platform builds one limiter per pipeline, which is per authority for a hedged client and under
/// <c>ByAuthority</c>. Owning one instance per pipeline key keeps that bound while making the instances
/// readable; owning one per client would weaken it. Keys come from <see cref="PipelineKeySelector"/>, so they
/// are bounded by the authority allow-list. A keyed singleton; the container disposes it.
/// </remarks>
internal sealed class BackstopLimiters : IDisposable
{
    private readonly ConcurrentDictionary<string, RateLimiter> _byKey = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly string _clientName;
    private readonly int _permitLimit;
    private readonly bool _perAuthority;
    private readonly HttpResilienceMetrics? _metrics;

    /// <param name="clientName">The client that owns the backstop.</param>
    /// <param name="permitLimit"><c>ConcurrencyLimiter:Backstop</c>, read from live options.</param>
    /// <param name="perAuthority">
    /// Whether the client has one pipeline per authority. If not, every request maps to one limiter, reported
    /// without an authority tag.
    /// </param>
    /// <param name="metrics">The gauges, if registered.</param>
    public BackstopLimiters(string clientName, int permitLimit, bool perAuthority, HttpResilienceMetrics? metrics)
    {
        _clientName = clientName;
        _permitLimit = permitLimit;
        _perAuthority = perAuthority;
        _metrics = metrics;
    }

    /// <summary>The limiter for one pipeline key, created on its first request.</summary>
    public RateLimiter For(string key)
    {
        if (!_perAuthority)
        {
            key = PipelineKeySelector.SharedKey;
        }

        if (_byKey.TryGetValue(key, out RateLimiter? limiter))
        {
            return limiter;
        }

        // Created under a lock so exactly one instance per key is ever enforced and reported.
        lock (_gate)
        {
            if (_byKey.TryGetValue(key, out limiter))
            {
                return limiter;
            }

            limiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions
            {
                PermitLimit = _permitLimit,
                QueueLimit = 0
            });

            _byKey[key] = limiter;
            _metrics?.Track(_clientName, LimiterKind.Backstop, limiter, _perAuthority ? key : null);
            return limiter;
        }
    }

    public void Dispose()
    {
        foreach (RateLimiter limiter in _byKey.Values)
        {
            limiter.Dispose();
        }
    }
}
