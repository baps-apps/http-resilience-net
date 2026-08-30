using HttpResilience.NET.Options;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Polly;

namespace HttpResilience.NET.Internal;

/// <summary>
/// Applies circuit breaker thresholds and wires state reporting, for both the standard and hedging pipelines.
/// </summary>
internal static class CircuitBreakerCallbacks
{
    public static void Apply(
        HttpCircuitBreakerStrategyOptions strategy,
        CircuitBreakerOptions options,
        string clientName,
        Func<HttpRequestMessage?, string> trackingKey,
        ILogger? logger,
        CircuitBreakerStateTracker? tracker)
    {
        strategy.FailureRatio = options.FailureRatio;
        strategy.MinimumThroughput = options.MinimumThroughput;
        strategy.SamplingDuration = options.SamplingDuration;
        strategy.BreakDuration = options.BreakDuration;

        // The breaker is labelled with the key of the pipeline it belongs to, read from the resilience
        // context. Per-authority pipelines therefore report as distinct breakers instead of overwriting one
        // another under a shared client name, and the set of labels stays bounded by configuration.
        strategy.OnOpened = args =>
        {
            string authority = Authority(args.Context, trackingKey);
            if (logger is not null)
            {
                HttpResilienceLogging.CircuitBreakerOpened(logger, clientName, authority, args.BreakDuration.TotalSeconds);
            }

            tracker?.Report(new CircuitKey(clientName, authority), CircuitState.Open);
            return default;
        };

        strategy.OnHalfOpened = args =>
        {
            string authority = Authority(args.Context, trackingKey);
            if (logger is not null)
            {
                HttpResilienceLogging.CircuitBreakerHalfOpen(logger, clientName, authority);
            }

            tracker?.Report(new CircuitKey(clientName, authority), CircuitState.HalfOpen);
            return default;
        };

        strategy.OnClosed = args =>
        {
            string authority = Authority(args.Context, trackingKey);
            if (logger is not null)
            {
                HttpResilienceLogging.CircuitBreakerClosed(logger, clientName, authority);
            }

            tracker?.Report(new CircuitKey(clientName, authority), CircuitState.Closed);
            return default;
        };
    }

    private static string Authority(ResilienceContext context, Func<HttpRequestMessage?, string> trackingKey) =>
        trackingKey(context.GetRequestMessage());
}
