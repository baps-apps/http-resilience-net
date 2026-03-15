using System.Threading.RateLimiting;
using HttpResilience.NET.Internal;
using HttpResilience.NET.Options;

namespace HttpResilience.NET.Tests.Internal;

public class RateLimiterFactoryTests
{
    [Fact]
    public void CreateRateLimiter_FixedWindow_ReturnsFixedWindowRateLimiter()
    {
        var options = new RateLimiterOptions
        {
            Enabled = true,
            Algorithm = RateLimitAlgorithm.FixedWindow,
            PermitLimit = 10,
            WindowSeconds = 1,
            QueueLimit = 0
        };

        RateLimiter limiter = RateLimiterFactory.CreateRateLimiter(options);

        Assert.IsType<FixedWindowRateLimiter>(limiter);
    }

    [Fact]
    public void CreateRateLimiter_SlidingWindow_ReturnsSlidingWindowRateLimiter()
    {
        var options = new RateLimiterOptions
        {
            Enabled = true,
            Algorithm = RateLimitAlgorithm.SlidingWindow,
            PermitLimit = 10,
            WindowSeconds = 1,
            SegmentsPerWindow = 4,
            QueueLimit = 0
        };

        RateLimiter limiter = RateLimiterFactory.CreateRateLimiter(options);

        Assert.IsType<SlidingWindowRateLimiter>(limiter);
    }

    [Fact]
    public void CreateRateLimiter_TokenBucket_ReturnsTokenBucketRateLimiter()
    {
        var options = new RateLimiterOptions
        {
            Enabled = true,
            Algorithm = RateLimitAlgorithm.TokenBucket,
            TokenBucketCapacity = 10,
            TokensPerPeriod = 10,
            ReplenishmentPeriodSeconds = 1,
            QueueLimit = 0
        };

        RateLimiter limiter = RateLimiterFactory.CreateRateLimiter(options);

        Assert.IsType<TokenBucketRateLimiter>(limiter);
    }
}

