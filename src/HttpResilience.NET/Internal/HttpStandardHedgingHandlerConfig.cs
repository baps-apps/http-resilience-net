using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using HttpResilience.NET.Options;

namespace HttpResilience.NET.Internal;

/// <summary>
/// Builds <see cref="HttpStandardHedgingResilienceOptions"/> from <see cref="HttpResilienceOptions"/> and request timeout.
/// Hedging pipeline: TotalRequestTimeout, Hedging, and per-endpoint (Endpoint) options for AttemptTimeout and CircuitBreaker.
/// </summary>
internal static class HttpStandardHedgingHandlerConfig
{
    /// <summary>
    /// Creates an action that configures <see cref="HttpStandardHedgingResilienceOptions"/> from the given options and per-request timeout.
    /// </summary>
    /// <param name="options">HTTP resilience options (timeout, circuit breaker, hedging).</param>
    /// <param name="requestTimeoutSeconds">Effective total request timeout in seconds.</param>
    /// <param name="logger">Optional logger for structured resilience event logging.</param>
    /// <param name="clientName">Named HTTP client identifier for log correlation.</param>
    /// <returns>An action that configures HttpStandardHedgingResilienceOptions when applied to an options instance.</returns>
    public static Action<HttpStandardHedgingResilienceOptions> Create(
        HttpResilienceOptions options,
        int requestTimeoutSeconds,
        ILogger? logger = null,
        string? clientName = null)
    {
        var hedging = options.Hedging;
        var timeout = options.Timeout;
        var cb = options.CircuitBreaker;
        return resilienceOptions =>
        {
            resilienceOptions.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(requestTimeoutSeconds);
            resilienceOptions.Hedging.Delay = hedging.DelaySeconds == 0
                ? TimeSpan.Zero
                : TimeSpan.FromSeconds(hedging.DelaySeconds);
            resilienceOptions.Hedging.MaxHedgedAttempts = hedging.MaxHedgedAttempts;

            resilienceOptions.Endpoint.Timeout.Timeout = TimeSpan.FromSeconds(timeout.AttemptTimeoutSeconds);
            resilienceOptions.Endpoint.CircuitBreaker.FailureRatio = cb.FailureRatio;
            resilienceOptions.Endpoint.CircuitBreaker.MinimumThroughput = cb.MinimumThroughput;
            resilienceOptions.Endpoint.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(cb.SamplingDurationSeconds);
            resilienceOptions.Endpoint.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(cb.BreakDurationSeconds);

            if (logger is not null)
            {
                var name = clientName ?? "unknown";
                resilienceOptions.Endpoint.CircuitBreaker.OnOpened = args =>
                {
                    HttpResilienceLogging.CircuitBreakerOpened(logger, name, args.BreakDuration.TotalSeconds);
                    return default;
                };
                resilienceOptions.Endpoint.CircuitBreaker.OnHalfOpened = _ =>
                {
                    HttpResilienceLogging.CircuitBreakerHalfOpen(logger, name);
                    return default;
                };
                resilienceOptions.Endpoint.CircuitBreaker.OnClosed = _ =>
                {
                    HttpResilienceLogging.CircuitBreakerClosed(logger, name);
                    return default;
                };
            }
        };
    }
}
