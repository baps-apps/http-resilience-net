using Microsoft.Extensions.Logging;

namespace HttpResilience.NET.Internal;

/// <summary>
/// High-performance structured log messages for HTTP resilience events.
/// Uses LoggerMessage source generation for zero-allocation logging when the log level is disabled.
/// </summary>
internal static partial class HttpResilienceLogging
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "HttpResilience fallback activated for client '{ClientName}'. Original status: {StatusCode}, Exception: {ExceptionType}")]
    public static partial void FallbackActivated(ILogger logger, string clientName, int? statusCode, string? exceptionType);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "HttpResilience retry attempt {AttemptNumber} for client '{ClientName}' after {RetryDelayMs}ms. Reason: {Reason}")]
    public static partial void RetryAttempt(ILogger logger, int attemptNumber, string clientName, double retryDelayMs, string? reason);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
        Message = "HttpResilience circuit breaker OPENED for client '{ClientName}'. Break duration: {BreakDurationSeconds}s")]
    public static partial void CircuitBreakerOpened(ILogger logger, string clientName, double breakDurationSeconds);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information,
        Message = "HttpResilience circuit breaker HALF-OPEN for client '{ClientName}'")]
    public static partial void CircuitBreakerHalfOpen(ILogger logger, string clientName);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information,
        Message = "HttpResilience circuit breaker CLOSED for client '{ClientName}'")]
    public static partial void CircuitBreakerClosed(ILogger logger, string clientName);
}
