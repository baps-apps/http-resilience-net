using HttpResilience.NET.Options;

namespace HttpResilience.NET.Internal;

/// <summary>
/// Canonical names for pipeline strategies used in <see cref="HttpResilienceOptions.PipelineStrategyOrder"/>.
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
    /// Set of allowed strategy names (case-insensitive) for validation.
    /// </summary>
    public static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        Fallback,
        Bulkhead,
        RateLimiter,
        Standard,
        Hedging
    };
}
