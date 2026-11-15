using System.Globalization;
using Microsoft.Extensions.Logging;

namespace HttpResilience.NET.Internal;

/// <summary>
/// Event 11: the traffic this client's circuit breaker needs before it can open.
/// </summary>
internal static class CircuitBreakerReachNotice
{
    public static void Report(ILogger logger, RegisteredClient client, HttpResilienceOptions options)
    {
        if (options.CircuitBreaker.SamplingDuration <= TimeSpan.Zero || !logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        double attemptsPerSecond =
            options.CircuitBreaker.MinimumThroughput / options.CircuitBreaker.SamplingDuration.TotalSeconds;
        string attemptRate = Rate(attemptsPerSecond);
        string requestRate = Rate(attemptsPerSecond / AttemptsPerRequest(client.Kind, options));
        string throughputPath = $"{client.Scope}:CircuitBreaker:MinimumThroughput";

        HttpResilienceLogging.CircuitBreakerReach(
            logger,
            client.Name,
            attemptRate,
            requestRate,
            options.CircuitBreaker.MinimumThroughput,
            options.CircuitBreaker.SamplingDuration.TotalSeconds,
            throughputPath);
    }

    /// <summary>
    /// How many observations one fully-failing caller request contributes to <b>one</b> breaker.
    /// </summary>
    private static int AttemptsPerRequest(PipelineKind kind, HttpResilienceOptions options) =>
        kind is PipelineKind.Hedging || !options.Retry.Enabled ? 1 : options.Retry.MaxRetries + 1;

    private static string Rate(double value) =>
        value.ToString(value < 10 ? "0.0" : "0", CultureInfo.InvariantCulture);
}
