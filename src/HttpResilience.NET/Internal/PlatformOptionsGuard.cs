using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

namespace HttpResilience.NET.Internal;

/// <summary>
/// Fails a client whose platform options were changed after this package wrote them.
/// </summary>
/// <remarks>
/// The platform's options (<c>{client}-standard</c>, or <c>{client}</c> for hedging) are shared by every
/// registration on the client, so a consumer's <c>Configure</c>, <c>PostConfigure</c>, <c>ConfigureAll</c> or second
/// <c>AddStandardResilienceHandler(configure)</c> would otherwise rewrite the validated pipeline, safe-method guard
/// included. What the package wrote is recorded per instance; validators run after every configure and
/// post-configure, so any later change is seen before the pipeline is built from it. Only instances the package
/// wrote are judged: another client's options, and a disabled client's, carry no record.
/// </remarks>
internal sealed class PlatformOptionsGuard :
    IValidateOptions<HttpStandardResilienceOptions>,
    IValidateOptions<HttpStandardHedgingResilienceOptions>
{
    private const string ActionGenerator = "Hedging.ActionGenerator";

    private static readonly ConditionalWeakTable<object, Written> Records = new();

    /// <summary>Records what the standard configurator wrote. Call after its last write.</summary>
    public static void Record(HttpStandardResilienceOptions options, string scope) =>
        Records.AddOrUpdate(options, new Written(scope, PipelineKind.Standard, Read(options)));

    /// <summary>Records what the hedging configurator wrote. Call after its last write.</summary>
    public static void Record(HttpStandardHedgingResilienceOptions options, string scope) =>
        Records.AddOrUpdate(options, new Written(scope, PipelineKind.Hedging, Read(options)));

    /// <summary>
    /// The platform sets the action generator in its own post-configure, after the configurator ran; the one
    /// in place once <c>SuppressUnsafeHedgedAttempts</c> has run is the one that must reach the pipeline.
    /// </summary>
    public static void RecordActionGenerator(HttpStandardHedgingResilienceOptions options)
    {
        if (!Records.TryGetValue(options, out Written? written))
        {
            return;
        }

        for (int i = 0; i < written.Values.Length; i++)
        {
            if (written.Values[i].Property == ActionGenerator)
            {
                written.Values[i] = (ActionGenerator, options.Hedging.ActionGenerator);
            }
        }
    }

    public ValidateOptionsResult Validate(string? name, HttpStandardResilienceOptions options) =>
        Check(name, options, Read(options));

    public ValidateOptionsResult Validate(string? name, HttpStandardHedgingResilienceOptions options) =>
        Check(name, options, Read(options));

    private static ValidateOptionsResult Check(string? name, object options, (string Property, object? Value)[] current)
    {
        if (!Records.TryGetValue(options, out Written? written))
        {
            return ValidateOptionsResult.Skip;
        }

        List<string> changed = [];
        for (int i = 0; i < current.Length; i++)
        {
            (string property, object? expected) = written.Values[i];
            if (!Equals(expected, current[i].Value))
            {
                changed.Add(expected is Delegate || current[i].Value is Delegate
                    ? $"{property} (replaced)"
                    : $"{property} ({Format(expected)}, now {Format(current[i].Value)})");
            }
        }

        if (changed.Count == 0)
        {
            return ValidateOptionsResult.Success;
        }

        (string type, string platformCall, string packageCall) = written.Kind is PipelineKind.Hedging
            ? (nameof(HttpStandardHedgingResilienceOptions), "a second AddStandardHedgingHandler",
                "AddHttpResilienceHedgingHandler(configure)")
            : (nameof(HttpStandardResilienceOptions), "AddStandardResilienceHandler(configure)",
                "AddHttpResilienceHandler(configure)");

        return ValidateOptionsResult.Fail(
            $"{written.Scope} -- {type} '{name}' was changed after this package wrote it from configuration: " +
            $"{string.Join(", ", changed)}. These options are the client's validated pipeline, shared by every " +
            $"registration on the client: code that sets them ({type} Configure, PostConfigure or ConfigureAll, or " +
            $"{platformCall} on the client) would replace validated values and can make an unsafe " +
            $"method such as POST repeatable. Set the value in {written.Scope} or {packageCall}; add a strategy " +
            "the schema lacks with AddResilienceHandler.");
    }

    private static string Format(object? value) =>
        Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null";

    // Everything the configurators write, plus each strategy's predicate, which the schema decides by omission.
    // Delegates compare by method and target, so only the package's own instance matches.
    private static (string Property, object? Value)[] Read(HttpStandardResilienceOptions o) =>
    [
        ("TotalRequestTimeout.Timeout", o.TotalRequestTimeout.Timeout),
        ("AttemptTimeout.Timeout", o.AttemptTimeout.Timeout),
        ("Retry.MaxRetryAttempts", o.Retry.MaxRetryAttempts),
        ("Retry.Delay", o.Retry.Delay),
        ("Retry.BackoffType", o.Retry.BackoffType),
        ("Retry.UseJitter", o.Retry.UseJitter),
        ("Retry.MaxDelay", o.Retry.MaxDelay),
        ("Retry.ShouldRetryAfterHeader", o.Retry.ShouldRetryAfterHeader),
        ("Retry.ShouldHandle", o.Retry.ShouldHandle),
        ("Retry.DelayGenerator", o.Retry.DelayGenerator),
        ("Retry.OnRetry", o.Retry.OnRetry),
        .. Read("CircuitBreaker", o.CircuitBreaker),
        ("RateLimiter.RateLimiter", o.RateLimiter.RateLimiter),
        ("RateLimiter.OnRejected", o.RateLimiter.OnRejected)
    ];

    private static (string Property, object? Value)[] Read(HttpStandardHedgingResilienceOptions o) =>
    [
        ("TotalRequestTimeout.Timeout", o.TotalRequestTimeout.Timeout),
        ("Hedging.Delay", o.Hedging.Delay),
        ("Hedging.MaxHedgedAttempts", o.Hedging.MaxHedgedAttempts),
        ("Hedging.ShouldHandle", o.Hedging.ShouldHandle),
        ("Hedging.DelayGenerator", o.Hedging.DelayGenerator),
        ("Hedging.OnHedging", o.Hedging.OnHedging),
        (ActionGenerator, o.Hedging.ActionGenerator),
        ("Endpoint.Timeout.Timeout", o.Endpoint.Timeout.Timeout),
        ("Endpoint.RateLimiter.RateLimiter", o.Endpoint.RateLimiter.RateLimiter),
        ("Endpoint.RateLimiter.OnRejected", o.Endpoint.RateLimiter.OnRejected),
        .. Read("Endpoint.CircuitBreaker", o.Endpoint.CircuitBreaker)
    ];

    private static (string Property, object? Value)[] Read(string prefix, HttpCircuitBreakerStrategyOptions o) =>
    [
        ($"{prefix}.FailureRatio", o.FailureRatio),
        ($"{prefix}.MinimumThroughput", o.MinimumThroughput),
        ($"{prefix}.SamplingDuration", o.SamplingDuration),
        ($"{prefix}.BreakDuration", o.BreakDuration),
        ($"{prefix}.ShouldHandle", o.ShouldHandle),
        ($"{prefix}.OnOpened", o.OnOpened),
        ($"{prefix}.OnHalfOpened", o.OnHalfOpened),
        ($"{prefix}.OnClosed", o.OnClosed)
    ];

    private sealed class Written(string scope, PipelineKind kind, (string Property, object? Value)[] values)
    {
        public string Scope { get; } = scope;

        public PipelineKind Kind { get; } = kind;

        public (string Property, object? Value)[] Values { get; } = values;
    }
}
