using Microsoft.Extensions.Logging;

namespace HttpResilience.NET.Internal;

/// <summary>
/// Event 21: a hedged client's <see cref="HttpResilienceHedgingOptions.Delay"/> is so short that nearly every
/// request is sent <see cref="HttpResilienceHedgingOptions.MaxHedgedAttempts"/> + 1 times, healthy dependency or not.
/// </summary>
/// <remarks>
/// A warning rather than a validation failure: a zero delay is a valid choice for a dependency built to absorb
/// the fan-out. The threshold is the shortest timeout the platform accepts.
/// </remarks>
internal static class HedgingFanOutNotice
{
    internal static readonly TimeSpan Threshold = HttpResilienceOptionsValidator.MinStrategyTimeout;

    public static void Report(ILogger logger, RegisteredClient client, HttpResilienceOptions options)
    {
        if (client.Kind is not PipelineKind.Hedging ||
            options.Hedging.Delay >= Threshold ||
            !logger.IsEnabled(LogLevel.Warning))
        {
            return;
        }

        HttpResilienceLogging.HedgingFansOutEveryRequest(
            logger,
            client.Name,
            options.Hedging.Delay,
            options.Hedging.MaxHedgedAttempts + 1,
            $"{client.Scope}:Hedging:Delay");
    }
}
