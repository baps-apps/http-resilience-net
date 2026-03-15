using System.Text.Json.Serialization;

namespace HttpResilience.NET.Options;

/// <summary>
/// Rate limiting algorithm. Uses <see cref="System.Threading.RateLimiting"/> implementations.
/// Config key: "HttpResilienceOptions:RateLimiter:Algorithm" (values: "FixedWindow", "SlidingWindow", "TokenBucket").
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RateLimitAlgorithm
{
    /// <summary>
    /// Fixed time window; simple "X permits per window". Config value: "FixedWindow".
    /// </summary>
    FixedWindow = 0,

    /// <summary>
    /// Sliding window; smoother, avoids boundary spikes. Config value: "SlidingWindow".
    /// </summary>
    SlidingWindow = 1,

    /// <summary>
    /// Token bucket; sustained average rate with burst capacity. Config value: "TokenBucket".
    /// </summary>
    TokenBucket = 2
}
