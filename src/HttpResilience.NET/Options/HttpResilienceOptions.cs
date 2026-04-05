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
    /// Order of pipeline strategies from outermost to innermost. Allowed values: "Fallback", "Bulkhead", "RateLimiter", "Standard", "Hedging".
    /// Must contain exactly one of "Standard" or "Hedging". Required when <see cref="Enabled"/> is true.
    /// <para><b>Use case:</b> e.g. [ "Fallback", "Bulkhead", "RateLimiter", "Standard" ] or [ "Hedging" ]. Config key: "PipelineOrder".</para>
    /// <para><b>Standard</b> = retry, circuit breaker, timeouts, optional rate limiting. <b>Hedging</b> = multiple requests, first success wins.</para>
    /// </summary>
    [JsonPropertyName("PipelineOrder")]
    public List<string>? PipelineOrder { get; set; }

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
    /// Hedging strategy options (used when PipelineOrder contains "Hedging"). Config key: "HttpResilienceOptions:Hedging".
    /// </summary>
    [ValidateObjectMembers]
    public HedgingOptions Hedging { get; set; } = new();

    /// <summary>
    /// Bulkhead / concurrency limit options (both pipelines). Config key: "HttpResilienceOptions:Bulkhead".
    /// </summary>
    [ValidateObjectMembers]
    public BulkheadOptions Bulkhead { get; set; } = new();

    /// <summary>
    /// Pipeline selection (e.g. per-authority). When Mode is "ByAuthority", a separate pipeline instance is used per request authority (scheme + host + port).
    /// Config key: "HttpResilienceOptions:PipelineSelection".
    /// </summary>
    [ValidateObjectMembers]
    public PipelineSelectionOptions PipelineSelection { get; set; } = new();
}
