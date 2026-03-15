using System.Threading.RateLimiting;
using HttpResilience.NET.Options;

namespace HttpResilience.NET.Internal;

/// <summary>
/// Creates <see cref="RateLimiter"/> instances from <see cref="RateLimiterOptions"/> for use in standard or hedging pipelines.
/// Internal-only helper; not part of the public API.
/// </summary>
internal static class RateLimiterFactory
{
    /// <summary>
    /// Creates a rate limiter from the given options (FixedWindow, SlidingWindow, or TokenBucket).
    /// </summary>
    public static RateLimiter CreateRateLimiter(RateLimiterOptions options)
    {
        return options.Algorithm switch
        {
            RateLimitAlgorithm.SlidingWindow => new SlidingWindowRateLimiter(new SlidingWindowRateLimiterOptions
            {
                PermitLimit = options.PermitLimit,
                Window = TimeSpan.FromSeconds(options.WindowSeconds),
                SegmentsPerWindow = options.SegmentsPerWindow,
                QueueLimit = options.QueueLimit
            }),
            RateLimitAlgorithm.TokenBucket => new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
            {
                TokenLimit = options.TokenBucketCapacity,
                TokensPerPeriod = options.TokensPerPeriod,
                ReplenishmentPeriod = TimeSpan.FromSeconds(options.ReplenishmentPeriodSeconds),
                QueueLimit = options.QueueLimit
            }),
            _ => new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
            {
                PermitLimit = options.PermitLimit,
                Window = TimeSpan.FromSeconds(options.WindowSeconds),
                QueueLimit = options.QueueLimit
            })
        };
    }
}
