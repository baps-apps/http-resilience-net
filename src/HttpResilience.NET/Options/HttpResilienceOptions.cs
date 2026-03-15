using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace HttpResilience.NET.Options;

/// <summary>
/// Configuration options for outgoing HTTP client resilience. Consumed by multiple applications that register
/// HTTP clients with <c>AddHttpClientWithResilience</c>.
/// Options are grouped by feature (Retry, CircuitBreaker, Timeout, etc.) for clear boundaries and separation.
/// All properties are bindable from configuration under "HttpResilienceOptions"; nested sections use the property names (e.g. "Retry", "CircuitBreaker").
/// </summary>
public class HttpResilienceOptions
{
    /// <summary>
    /// Master switch for HTTP resilience. When true, the resilience pipeline and primary handler are applied when you call
    /// AddHttpClientWithResilience. When false or not set, the extension does nothing.
    /// <para><b>Use case:</b> Set to true in appsettings (e.g. per environment) to enable resilience; set to false to disable without changing code. Default: false (opt-in).</para>
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Type of resilience pipeline: Standard (retry, circuit breaker, timeouts, optional rate limiting) or Hedging (multiple requests, first success wins).
    /// <para><b>Use case:</b> Set to Hedging in config for latency-sensitive calls to multiple replicas; use Standard (default) for typical API calls. Config key: "PipelineType" (values: "Standard", "Hedging").</para>
    /// </summary>
    [JsonPropertyName("PipelineType")]
    public ResiliencePipelineType PipelineType { get; set; } = ResiliencePipelineType.Standard;

    /// <summary>
    /// Connection and connection-pool options for the primary SocketsHttpHandler. Config key: "HttpResilienceOptions:Connection".
    /// </summary>
    [ValidateObjectMembers]
    public ConnectionOptions Connection { get; set; } = new();

    /// <summary>
    /// Request timeout options (total and per-attempt). Config key: "HttpResilienceOptions:Timeout".
    /// </summary>
    [ValidateObjectMembers]
    public TimeoutOptions Timeout { get; set; } = new();

    /// <summary>
    /// Retry strategy options. Config key: "HttpResilienceOptions:Retry".
    /// </summary>
    [ValidateObjectMembers]
    public RetryOptions Retry { get; set; } = new();

    /// <summary>
    /// Circuit breaker strategy options. Config key: "HttpResilienceOptions:CircuitBreaker".
    /// </summary>
    [ValidateObjectMembers]
    public CircuitBreakerOptions CircuitBreaker { get; set; } = new();

    /// <summary>
    /// Rate limiter options (standard and hedging pipelines when enabled). Config key: "HttpResilienceOptions:RateLimiter".
    /// </summary>
    [ValidateObjectMembers]
    public RateLimiterOptions RateLimiter { get; set; } = new();

    /// <summary>
    /// Fallback strategy options (both pipelines). Config key: "HttpResilienceOptions:Fallback".
    /// </summary>
    [ValidateObjectMembers]
    public FallbackOptions Fallback { get; set; } = new();

    /// <summary>
    /// Hedging strategy options (used when <see cref="PipelineType"/> is <see cref="ResiliencePipelineType.Hedging"/>). Config key: "HttpResilienceOptions:Hedging".
    /// </summary>
    [ValidateObjectMembers]
    public HedgingOptions Hedging { get; set; } = new();

    /// <summary>
    /// Bulkhead / concurrency limit options (both pipelines). Config key: "HttpResilienceOptions:Bulkhead".
    /// </summary>
    [ValidateObjectMembers]
    public BulkheadOptions Bulkhead { get; set; } = new();

    /// <summary>
    /// Order of fallback and bulkhead handlers when both are enabled. Ignored when <see cref="PipelineStrategyOrder"/> is set.
    /// <para><b>Options:</b> <see cref="PipelineOrderType.FallbackThenConcurrency"/> (default), <see cref="PipelineOrderType.ConcurrencyThenFallback"/>. Config key: "PipelineOrder".</para>
    /// <para><b>Effect:</b> FallbackThenConcurrency = fallback outermost, then bulkhead; ConcurrencyThenFallback = bulkhead outermost, then fallback.</para>
    /// </summary>
    [JsonPropertyName("PipelineOrder")]
    public PipelineOrderType PipelineOrder { get; set; } = PipelineOrderType.FallbackThenConcurrency;

    /// <summary>
    /// Optional order of outer strategies from outermost to innermost. Allowed values: "Fallback", "Bulkhead", "RateLimiter", "Standard", "Hedging".
    /// Must contain exactly one of "Standard" or "Hedging". When null or empty, <see cref="PipelineOrder"/> and <see cref="PipelineType"/> determine behavior.
    /// <para><b>Use case:</b> e.g. [ "Fallback", "Bulkhead", "RateLimiter", "Standard" ]. Config key: "PipelineStrategyOrder".</para>
    /// </summary>
    [JsonPropertyName("PipelineStrategyOrder")]
    public List<string>? PipelineStrategyOrder { get; set; }

    /// <summary>
    /// Pipeline selection (e.g. per-authority). When Mode is "ByAuthority", a separate pipeline instance is used per request authority (scheme + host + port).
    /// Config key: "HttpResilienceOptions:PipelineSelection".
    /// </summary>
    [ValidateObjectMembers]
    public PipelineSelectionOptions PipelineSelection { get; set; } = new();
}
