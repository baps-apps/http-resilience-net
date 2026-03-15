using System.ComponentModel.DataAnnotations;

namespace HttpResilience.NET.Options;

/// <summary>
/// Hedging strategy options (used by AddHttpClientWithHedgingResilience only). Config key: "HttpResilienceOptions:Hedging".
/// </summary>
public class HedgingOptions
{
    /// <summary>
    /// Delay (seconds) before sending an extra hedged request. 0 = send hedges immediately with the first request.
    /// <para><b>Use case:</b> 0 for latency-sensitive, multi-endpoint calls; 1–3 to give the first attempt a chance. Default: 2.</para>
    /// </summary>
    [Range(0, 60, ErrorMessage = "DelaySeconds must be between 0 and 60.")]
    public int DelaySeconds { get; set; } = 2;

    /// <summary>
    /// Maximum number of extra hedged attempts (beyond the first). Total attempts = 1 + MaxHedgedAttempts.
    /// <para><b>Use case:</b> 1 = one backup request (typical); higher for very latency-sensitive paths. Default: 1.</para>
    /// </summary>
    [Range(0, 10, ErrorMessage = "MaxHedgedAttempts must be between 0 and 10.")]
    public int MaxHedgedAttempts { get; set; } = 1;
}
