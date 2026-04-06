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
    /// <param name="tracker">Optional circuit breaker state tracker for health check integration.</param>
    /// <returns>An action that configures HttpStandardHedgingResilienceOptions when applied to an options instance.</returns>
    public static Action<HttpStandardHedgingResilienceOptions> Create(
        HttpResilienceOptions options,
        int requestTimeoutSeconds,
        ILogger? logger = null,
        string? clientName = null,
        CircuitBreakerStateTracker? tracker = null)
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

            var name = clientName ?? "unknown";
            resilienceOptions.Endpoint.CircuitBreaker.OnOpened = args =>
            {
                if (logger is not null)
                    HttpResilienceLogging.CircuitBreakerOpened(logger, name, args.BreakDuration.TotalSeconds);
                tracker?.ReportOpened(name);
                return default;
            };
            resilienceOptions.Endpoint.CircuitBreaker.OnHalfOpened = _ =>
            {
                if (logger is not null)
                    HttpResilienceLogging.CircuitBreakerHalfOpen(logger, name);
                tracker?.ReportHalfOpen(name);
                return default;
            };
            resilienceOptions.Endpoint.CircuitBreaker.OnClosed = _ =>
            {
                if (logger is not null)
                    HttpResilienceLogging.CircuitBreakerClosed(logger, name);
                tracker?.ReportClosed(name);
                return default;
            };
        };
    }
}
