using System.Threading.RateLimiting;
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
    /// <param name="rateLimiter">
    /// Pre-created rate limiter singleton to wire into the standard pipeline, or <see langword="null"/> when rate limiting
    /// is disabled or handled by a separate outer handler. The caller is responsible for registering this instance
    /// in the DI container via a factory-based <c>AddSingleton</c> overload so the container owns the lifetime and
    /// disposes the limiter when the <see cref="IServiceProvider"/> is disposed.
    /// </param>
    /// <param name="logger">Optional logger for structured resilience event logging.</param>
    /// <param name="clientName">Named HTTP client identifier for log correlation.</param>
    /// <param name="tracker">Optional circuit breaker state tracker for health check integration.</param>
    /// <returns>An action that configures HttpStandardResilienceOptions when applied to an options instance.</returns>
    public static Action<HttpStandardResilienceOptions> Create(
        HttpResilienceOptions options,
        int requestTimeoutSeconds,
        RateLimiter? rateLimiter = null,
        ILogger? logger = null,
        string? clientName = null,
        CircuitBreakerStateTracker? tracker = null)
    {
        var retry = options.Retry;
        var cb = options.CircuitBreaker;

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

            if (rateLimiter is not null)
            {
                resilienceOptions.RateLimiter.RateLimiter = args =>
                    rateLimiter.AcquireAsync(1, args.Context.CancellationToken);
            }

            var name = clientName ?? "unknown";

            if (logger is not null)
            {
                resilienceOptions.Retry.OnRetry = args =>
                {
                    HttpResilienceLogging.RetryAttempt(logger, args.AttemptNumber, name,
                        args.RetryDelay.TotalMilliseconds,
                        args.Outcome.Exception?.Message ?? args.Outcome.Result?.StatusCode.ToString());
                    return default;
                };
            }

            resilienceOptions.CircuitBreaker.OnOpened = args =>
            {
                if (logger is not null)
                    HttpResilienceLogging.CircuitBreakerOpened(logger, name, args.BreakDuration.TotalSeconds);
                tracker?.ReportOpened(name);
                return default;
            };
            resilienceOptions.CircuitBreaker.OnHalfOpened = _ =>
            {
                if (logger is not null)
                    HttpResilienceLogging.CircuitBreakerHalfOpen(logger, name);
                tracker?.ReportHalfOpen(name);
                return default;
            };
            resilienceOptions.CircuitBreaker.OnClosed = _ =>
            {
                if (logger is not null)
                    HttpResilienceLogging.CircuitBreakerClosed(logger, name);
                tracker?.ReportClosed(name);
                return default;
            };
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
