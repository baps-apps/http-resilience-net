using System.Threading.RateLimiting;

namespace HttpResilience.NET.Internal;

/// <summary>
/// Builds a <see cref="RateLimiter"/> from configuration, using the BCL implementations directly.
/// </summary>
internal static class RateLimiterFactory
{
    public static RateLimiter Create(HttpResilienceRateLimiterOptions options) => options.Algorithm switch
    {
        HttpResilienceRateLimitAlgorithm.SlidingWindow => new SlidingWindowRateLimiter(new SlidingWindowRateLimiterOptions
        {
            PermitLimit = options.PermitLimit!.Value,
            Window = options.Window,
            SegmentsPerWindow = options.SegmentsPerWindow,
            QueueLimit = options.QueueLimit
        }),
        HttpResilienceRateLimitAlgorithm.TokenBucket => new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = options.TokenLimit!.Value,
            TokensPerPeriod = options.TokensPerPeriod!.Value,
            ReplenishmentPeriod = options.ReplenishmentPeriod,
            QueueLimit = options.QueueLimit
        }),
        _ => new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = options.PermitLimit!.Value,
            Window = options.Window,
            QueueLimit = options.QueueLimit
        })
    };
}
