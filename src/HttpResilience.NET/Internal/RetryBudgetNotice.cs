using Microsoft.Extensions.Logging;

namespace HttpResilience.NET.Internal;

/// <summary>
/// Event 14: a standard client's worst-case retry schedule is longer than <see cref="HttpResilienceTimeoutOptions.Total"/>.
/// </summary>
/// <remarks>
/// A warning rather than a validation failure: a total timeout shorter than the schedule is a valid way to
/// cap a request, and the platform's own defaults are configured that way.
/// </remarks>
internal static class RetryBudgetNotice
{
    public static void Report(ILogger logger, RegisteredClient client, HttpResilienceOptions options)
    {
        if (client.Kind is not PipelineKind.Standard ||
            !options.Retry.Enabled ||
            !logger.IsEnabled(LogLevel.Warning))
        {
            return;
        }

        (TimeSpan worstCase, TimeSpan backoff) = HttpResilienceOptionsValidator.EstimateRetrySchedule(options);
        if (worstCase <= options.Timeout.Total)
        {
            return;
        }

        HttpResilienceLogging.RetryBudgetExceedsTotal(
            logger,
            client.Name,
            options.Timeout.Total,
            options.Retry.MaxRetries + 1,
            options.Timeout.Attempt,
            backoff,
            worstCase,
            $"{client.Scope}:Timeout:Total");
    }
}
