using System.Net.Http;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using HttpResilience.NET.Options;

namespace HttpResilience.NET.Internal;

/// <summary>
/// Builds <see cref="HttpStandardResilienceOptions"/> from <see cref="HttpResilienceOptions"/> and request timeout.
/// Internal-only helper; not part of the public API.
/// </summary>
internal static class HttpStandardResilienceHandlerConfig
{
    /// <summary>
    /// Creates an action that configures <see cref="HttpStandardResilienceOptions"/> from the given options and per-request timeout.
    /// </summary>
    /// <param name="options">HTTP resilience options (retry, circuit breaker, timeout, rate limit).</param>
    /// <param name="requestTimeoutSeconds">Effective total request timeout in seconds (use options.TotalRequestTimeoutSeconds or override per client).</param>
    /// <param name="rateLimiterHandledExternally">When true, rate limiting is already added as an outer handler (e.g. via PipelineStrategyOrder); do not configure the built-in rate limiter.</param>
    /// <returns>An action that configures HttpStandardResilienceOptions when applied to an options instance.</returns>
    public static Action<HttpStandardResilienceOptions> Create(
        HttpResilienceOptions options,
        int requestTimeoutSeconds,
        bool rateLimiterHandledExternally = false)
    {
        var retry = options.Retry;
        var cb = options.CircuitBreaker;
        var rateLimit = options.RateLimiter;
        return resilienceOptions =>
        {
            resilienceOptions.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(requestTimeoutSeconds);
            resilienceOptions.AttemptTimeout.Timeout = TimeSpan.FromSeconds(options.Timeout.AttemptTimeoutSeconds);
            resilienceOptions.Retry.MaxRetryAttempts = retry.MaxRetryAttempts;
            resilienceOptions.Retry.Delay = TimeSpan.FromSeconds(retry.BaseDelaySeconds);
            resilienceOptions.Retry.BackoffType = ToPollyBackoffType(retry.BackoffType);
            resilienceOptions.Retry.UseJitter = retry.UseJitter;
            resilienceOptions.Retry.ShouldRetryAfterHeader = retry.UseRetryAfterHeader;
            resilienceOptions.CircuitBreaker.FailureRatio = cb.FailureRatio;
            resilienceOptions.CircuitBreaker.MinimumThroughput = cb.MinimumThroughput;
            resilienceOptions.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(cb.SamplingDurationSeconds);
            resilienceOptions.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(cb.BreakDurationSeconds);

            if (rateLimit.Enabled && !rateLimiterHandledExternally)
            {
                RateLimiter limiter = RateLimiterFactory.CreateRateLimiter(rateLimit);
                resilienceOptions.RateLimiter.RateLimiter = args =>
                    limiter.AcquireAsync(1, args.Context.CancellationToken);
            }
        };
    }

    private static DelayBackoffType ToPollyBackoffType(RetryBackoffType value) =>
        value switch
        {
            RetryBackoffType.Constant => DelayBackoffType.Constant,
            RetryBackoffType.Linear => DelayBackoffType.Linear,
            _ => DelayBackoffType.Exponential
        };
}
