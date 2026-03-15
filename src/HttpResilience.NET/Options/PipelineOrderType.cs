using System.Text.Json.Serialization;

namespace HttpResilience.NET.Options;

/// <summary>
/// Order of fallback and bulkhead handlers when both are enabled. Ignored when <see cref="HttpResilienceOptions.PipelineStrategyOrder"/> is set.
/// Config key: "HttpResilienceOptions:PipelineOrder" (values: "FallbackThenConcurrency", "ConcurrencyThenFallback").
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PipelineOrderType
{
    /// <summary>
    /// Fallback handler is outermost, then bulkhead. Config value: "FallbackThenConcurrency".
    /// </summary>
    FallbackThenConcurrency = 0,

    /// <summary>
    /// Bulkhead (concurrency) handler is outermost, then fallback. Config value: "ConcurrencyThenFallback".
    /// </summary>
    ConcurrencyThenFallback = 1
}
