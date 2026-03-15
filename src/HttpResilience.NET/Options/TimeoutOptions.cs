using System.ComponentModel.DataAnnotations;

namespace HttpResilience.NET.Options;

/// <summary>
/// Request timeout options. Config key: "HttpResilienceOptions:Timeout".
/// </summary>
public class TimeoutOptions
{
    /// <summary>
    /// Total time (seconds) allowed for the entire HTTP request, including all retries and hedged attempts. Maps to TotalRequestTimeout.
    /// <para><b>Use case:</b> Upper bound for "user wait" (e.g. 30–60 for UI-driven calls). Can be overridden per client via requestTimeoutSeconds. Default: 30.</para>
    /// </summary>
    [Range(1, 600, ErrorMessage = "TotalRequestTimeoutSeconds must be between 1 and 600.")]
    public int TotalRequestTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Timeout (seconds) for a single attempt (one try before retry/hedge). Maps to AttemptTimeout. If exceeded, the attempt is aborted and may be retried.
    /// <para><b>Use case:</b> Keep below TotalRequestTimeoutSeconds; typical 5–15 so that a few retries fit within the total timeout. Default: 10.</para>
    /// </summary>
    [Range(1, 300, ErrorMessage = "AttemptTimeoutSeconds must be between 1 and 300.")]
    public int AttemptTimeoutSeconds { get; set; } = 10;
}
