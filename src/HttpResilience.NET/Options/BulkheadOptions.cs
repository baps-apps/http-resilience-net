using System.ComponentModel.DataAnnotations;

namespace HttpResilience.NET.Options;

/// <summary>
/// Bulkhead / concurrency limit options (both pipelines). Config key: "HttpResilienceOptions:Bulkhead".
/// </summary>
public class BulkheadOptions
{
    /// <summary>
    /// When true, limits how many outbound HTTP requests can run at once (bulkhead pattern).
    /// <para><b>Use case:</b> Protect the app from too many concurrent calls to one dependency. Default: false.</para>
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Maximum concurrent outbound requests when <see cref="Enabled"/> is true. Extra requests wait in queue up to <see cref="QueueLimit"/> or fail.
    /// <para><b>Use case:</b> Set to the concurrency your downstream can handle. Default: 100.</para>
    /// </summary>
    [Range(1, 1000, ErrorMessage = "Limit must be between 1 and 1000.")]
    public int Limit { get; set; } = 100;

    /// <summary>
    /// Maximum requests that can wait for a concurrency slot when <see cref="Enabled"/> is true (0 = no queue; fail when limit reached).
    /// <para><b>Use case:</b> 0 for hard cap; small value to absorb short bursts. Default: 0.</para>
    /// </summary>
    [Range(0, 10_000, ErrorMessage = "QueueLimit must be between 0 and 10000.")]
    public int QueueLimit { get; set; }
}
