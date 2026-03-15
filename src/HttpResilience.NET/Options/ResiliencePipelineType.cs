namespace HttpResilience.NET.Options;

/// <summary>
/// Type of resilience pipeline to use for the HTTP client.
/// When <see cref="Standard"/> is used, the pipeline includes retry, circuit breaker, timeouts, and optional rate limiting.
/// When <see cref="Hedging"/> is used, the pipeline sends multiple requests and uses the first successful response.
/// </summary>
public enum ResiliencePipelineType
{
    /// <summary>
    /// Standard pipeline: total/attempt timeouts, retry, circuit breaker, optional rate limiting.
    /// </summary>
    Standard = 0,

    /// <summary>
    /// Hedging pipeline: multiple requests with delay between hedged attempts; first successful response wins.
    /// </summary>
    Hedging = 1
}
