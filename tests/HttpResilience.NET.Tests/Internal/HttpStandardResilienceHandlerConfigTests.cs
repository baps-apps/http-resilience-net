using Microsoft.Extensions.Http.Resilience;
using Polly;
using HttpResilience.NET.Internal;
using HttpResilience.NET.Options;

namespace HttpResilience.NET.Tests.Internal;

public class HttpStandardResilienceHandlerConfigTests
{
    [Fact]
    public void Create_MapsOptionsToHttpStandardResilienceOptions()
    {
        var options = new HttpResilienceOptions
        {
            Timeout = { TotalRequestTimeoutSeconds = 30, AttemptTimeoutSeconds = 5 },
            Retry =
            {
                MaxRetryAttempts = 3,
                BaseDelaySeconds = 2,
                BackoffType = RetryBackoffType.Linear,
                UseJitter = false,
                UseRetryAfterHeader = true
            },
            CircuitBreaker =
            {
                FailureRatio = 0.5,
                MinimumThroughput = 10,
                SamplingDurationSeconds = 60,
                BreakDurationSeconds = 15
            },
            RateLimiter =
            {
                Enabled = false
            }
        };

        var config = HttpStandardResilienceHandlerConfig.Create(options, requestTimeoutSeconds: 25);
        var target = new HttpStandardResilienceOptions();

        config(target);

        Assert.Equal(TimeSpan.FromSeconds(25), target.TotalRequestTimeout.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(5), target.AttemptTimeout.Timeout);
        Assert.Equal(3, target.Retry.MaxRetryAttempts);
        Assert.Equal(TimeSpan.FromSeconds(2), target.Retry.Delay);
        Assert.Equal(DelayBackoffType.Linear, target.Retry.BackoffType);
        Assert.False(target.Retry.UseJitter);
        Assert.True(target.Retry.ShouldRetryAfterHeader);
        Assert.Equal(0.5, target.CircuitBreaker.FailureRatio);
        Assert.Equal(10, target.CircuitBreaker.MinimumThroughput);
        Assert.Equal(TimeSpan.FromSeconds(60), target.CircuitBreaker.SamplingDuration);
        Assert.Equal(TimeSpan.FromSeconds(15), target.CircuitBreaker.BreakDuration);
    }

    [Fact]
    public void Create_WhenRateLimiterHandledExternally_DoesNotConfigureRateLimiter()
    {
        var options = new HttpResilienceOptions
        {
            Timeout = { TotalRequestTimeoutSeconds = 30, AttemptTimeoutSeconds = 10 },
            RateLimiter = { Enabled = true, PermitLimit = 100, WindowSeconds = 1 }
        };

        var config = HttpStandardResilienceHandlerConfig.Create(options, requestTimeoutSeconds: 30, rateLimiterHandledExternally: true);
        var target = new HttpStandardResilienceOptions();

        config(target);

        Assert.Null(target.RateLimiter.RateLimiter);
    }
}

