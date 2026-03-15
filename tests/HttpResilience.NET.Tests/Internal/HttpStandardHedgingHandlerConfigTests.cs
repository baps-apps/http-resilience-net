using Microsoft.Extensions.Http.Resilience;
using HttpResilience.NET.Internal;
using HttpResilience.NET.Options;

namespace HttpResilience.NET.Tests.Internal;

public class HttpStandardHedgingHandlerConfigTests
{
    [Fact]
    public void Create_MapsOptionsToHttpStandardHedgingResilienceOptions()
    {
        var options = new HttpResilienceOptions
        {
            Timeout = { AttemptTimeoutSeconds = 3 },
            Hedging = { DelaySeconds = 1, MaxHedgedAttempts = 2 },
            CircuitBreaker =
            {
                FailureRatio = 0.2,
                MinimumThroughput = 50,
                SamplingDurationSeconds = 30,
                BreakDurationSeconds = 10
            }
        };

        var config = HttpStandardHedgingHandlerConfig.Create(options, requestTimeoutSeconds: 8);
        var target = new HttpStandardHedgingResilienceOptions();

        config(target);

        Assert.Equal(TimeSpan.FromSeconds(8), target.TotalRequestTimeout.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(1), target.Hedging.Delay);
        Assert.Equal(2, target.Hedging.MaxHedgedAttempts);
        Assert.Equal(TimeSpan.FromSeconds(3), target.Endpoint.Timeout.Timeout);
        Assert.Equal(0.2, target.Endpoint.CircuitBreaker.FailureRatio);
        Assert.Equal(50, target.Endpoint.CircuitBreaker.MinimumThroughput);
        Assert.Equal(TimeSpan.FromSeconds(30), target.Endpoint.CircuitBreaker.SamplingDuration);
        Assert.Equal(TimeSpan.FromSeconds(10), target.Endpoint.CircuitBreaker.BreakDuration);
    }
}

