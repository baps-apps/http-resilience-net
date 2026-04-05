using System.Threading.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
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
    /// <param name="services">Service collection for registering disposable resources (e.g. rate limiters).</param>
    /// <param name="rateLimiterHandledExternally">When true, rate limiting is already added as an outer handler (e.g. via PipelineOrder); do not configure the built-in rate limiter.</param>
    /// <param name="logger">Optional logger for structured resilience event logging.</param>
    /// <param name="clientName">Named HTTP client identifier for log correlation.</param>
    /// <returns>An action that configures HttpStandardResilienceOptions when applied to an options instance.</returns>
    public static Action<HttpStandardResilienceOptions> Create(
        HttpResilienceOptions options,
        int requestTimeoutSeconds,
        IServiceCollection services,
        bool rateLimiterHandledExternally = false,
        ILogger? logger = null,
        string? clientName = null)
    {
        var retry = options.Retry;
        var cb = options.CircuitBreaker;
        var rateLimit = options.RateLimiter;

        RateLimiter? limiter = null;
        if (rateLimit.Enabled && !rateLimiterHandledExternally)
        {
            limiter = RateLimiterFactory.CreateRateLimiter(rateLimit);
            services.AddSingleton(limiter);
        }

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

            if (limiter is not null)
            {
                resilienceOptions.RateLimiter.RateLimiter = args =>
                    limiter.AcquireAsync(1, args.Context.CancellationToken);
            }

            if (logger is not null)
            {
                var name = clientName ?? "unknown";
                resilienceOptions.Retry.OnRetry = args =>
                {
                    HttpResilienceLogging.RetryAttempt(logger, args.AttemptNumber, name,
                        args.RetryDelay.TotalMilliseconds,
                        args.Outcome.Exception?.Message ?? args.Outcome.Result?.StatusCode.ToString());
                    return default;
                };
                resilienceOptions.CircuitBreaker.OnOpened = args =>
                {
                    HttpResilienceLogging.CircuitBreakerOpened(logger, name, args.BreakDuration.TotalSeconds);
                    return default;
                };
                resilienceOptions.CircuitBreaker.OnHalfOpened = _ =>
                {
                    HttpResilienceLogging.CircuitBreakerHalfOpen(logger, name);
                    return default;
                };
                resilienceOptions.CircuitBreaker.OnClosed = _ =>
                {
                    HttpResilienceLogging.CircuitBreakerClosed(logger, name);
                    return default;
                };
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
