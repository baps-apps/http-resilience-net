using System.Collections.Frozen;
using HttpResilience.NET.Options;

namespace HttpResilience.NET.Internal;

/// <summary>
/// Canonical names for pipeline strategies used in <see cref="HttpResilienceOptions.PipelineOrder"/>.
/// Values must match configuration (e.g. "Fallback", "Bulkhead", "RateLimiter", "Standard", "Hedging").
/// </summary>
internal static class PipelineStrategyNames
{
    public const string Fallback = "Fallback";
    public const string Bulkhead = "Bulkhead";
    public const string RateLimiter = "RateLimiter";
    public const string Standard = "Standard";
    public const string Hedging = "Hedging";

    /// <summary>
    /// Set of allowed strategy names (case-insensitive) for validation. Frozen for fast read-only Contains.
    /// </summary>
    public static readonly FrozenSet<string> Allowed = new[]
    {
        Fallback,
        Bulkhead,
        RateLimiter,
        Standard,
        Hedging
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
}
