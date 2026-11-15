using System.Globalization;
using Microsoft.Extensions.Logging;

namespace HttpResilience.NET.Internal;

/// <summary>
/// Event 15: one line per enabled client stating the settings it runs with, so an operator can answer "what is
/// this client actually doing?" from the startup log.
/// </summary>
internal static class ClientSummaryNotice
{
    private const string SafeMethods = "GET, HEAD, OPTIONS, TRACE";

    public static void Report(ILogger logger, RegisteredClient client, HttpResilienceOptions options)
    {
        if (!logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        string pipeline = client.Kind is PipelineKind.Hedging ? "hedging" : "standard";
        string summary = Describe(client.Kind, options);
        HttpResilienceLogging.ClientSummary(logger, client.Name, pipeline, summary);
    }

    internal static string Describe(PipelineKind kind, HttpResilienceOptions options)
    {
        HttpResilienceTimeoutOptions timeout = options.Timeout;
        HttpResilienceCircuitBreakerOptions breaker = options.CircuitBreaker;

        string[] parts =
        [
            $"timeout total {Span(timeout.Total)}, attempt {Span(timeout.Attempt)}, client {Span(timeout.EffectiveClientTimeout())}",
            kind is PipelineKind.Hedging ? Hedging(options) : Retry(options),
            Invariant($"breaker opens at {breaker.FailureRatio * 100:0.#}% failures of at least {breaker.MinimumThroughput} attempts in {Span(breaker.SamplingDuration)}, for {Span(breaker.BreakDuration)}"),
            Selection(kind, options.PipelineSelection),
            Concurrency(options.ConcurrencyLimiter),
            Rate(options.RateLimiter),
            options.Connection.Enabled ? "connection settings on" : "connection settings off"
        ];

        return string.Join("; ", parts);
    }

    private static string Retry(HttpResilienceOptions options)
    {
        HttpResilienceRetryOptions retry = options.Retry;
        if (!retry.Enabled)
        {
            return "retry off";
        }

        string methods = retry.RetryableMethods.Count > 0
            ? string.Join(", ", retry.RetryableMethods.Select(method => method.ToUpperInvariant()))
            : retry.DisableForUnsafeHttpMethods ? SafeMethods : "every method";

        return Invariant($"retry {retry.MaxRetries} ({retry.BackoffType} from {Span(retry.BaseDelay)}, at most {Span(retry.MaxDelay)}) for {methods}");
    }

    private static string Hedging(HttpResilienceOptions options)
    {
        HttpResilienceHedgingOptions hedging = options.Hedging;
        string methods = hedging.DisableForUnsafeHttpMethods ? SafeMethods : "every method";

        return Invariant($"hedging {hedging.MaxHedgedAttempts} after {Span(hedging.Delay)} for {methods}");
    }

    private static string Selection(PipelineKind kind, HttpResiliencePipelineSelectionOptions selection) =>
        kind is PipelineKind.Hedging
            ? Invariant($"{selection.Authorities.Count} allowed {(selection.Authorities.Count == 1 ? "authority" : "authorities")}")
            : selection.Mode is HttpResiliencePipelineSelectionMode.ByAuthority
                ? Invariant($"a pipeline per listed authority ({selection.Authorities.Count})")
                : "one pipeline";

    private static string Concurrency(HttpResilienceConcurrencyLimiterOptions limiter) =>
        limiter.Enabled
            ? Invariant($"concurrency limit {limiter.Limit} (queue {limiter.QueueLimit}), backstop {limiter.Backstop}")
            : Invariant($"concurrency backstop {limiter.Backstop}");

    private static string Rate(HttpResilienceRateLimiterOptions limiter) =>
        !limiter.Enabled
            ? "rate limit off"
            : limiter.Algorithm is HttpResilienceRateLimitAlgorithm.TokenBucket
                ? Invariant($"rate limit token bucket of {limiter.TokenLimit}, {limiter.TokensPerPeriod} per {Span(limiter.ReplenishmentPeriod)} (queue {limiter.QueueLimit})")
                : Invariant($"rate limit {limiter.PermitLimit} per {Span(limiter.Window)}, {limiter.Algorithm} (queue {limiter.QueueLimit})");

    private static string Span(TimeSpan value) =>
        value.Days > 0
            ? value.ToString("c", CultureInfo.InvariantCulture)
            : value.ToString(value.Milliseconds == 0 ? @"hh\:mm\:ss" : @"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);

    private static string Invariant(FormattableString value) => FormattableString.Invariant(value);
}
