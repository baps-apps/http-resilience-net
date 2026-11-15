namespace HttpResilience.NET;

/// <summary>
/// How the delay between retry attempts grows.
/// </summary>
/// <remarks>
/// Leave this at <see cref="Exponential"/> unless you have a reason. It backs off fastest, which is what
/// gives a struggling dependency room to recover. Keep <see cref="HttpResilienceRetryOptions.UseJitter"/> on whichever you
/// pick: without it every replica retries on the same schedule and the retries arrive as one wave.
/// </remarks>
/// <example>
/// <code language="json">
/// { "HttpResilience": { "Retry": { "BackoffType": "Exponential", "BaseDelay": "00:00:00.500" } } }
/// </code>
/// </example>
public enum HttpResilienceRetryBackoffType
{
    /// <summary>
    /// Every retry waits <see cref="HttpResilienceRetryOptions.BaseDelay"/>. Use for a dependency with a known fixed
    /// recovery time, such as one behind a leader election.
    /// </summary>
    Constant = 0,

    /// <summary>
    /// The delay grows linearly: 1x, 2x, 3x <see cref="HttpResilienceRetryOptions.BaseDelay"/>. A middle ground when
    /// exponential backs off further than the total budget allows.
    /// </summary>
    Linear = 1,

    /// <summary>
    /// The delay doubles each attempt: 1x, 2x, 4x <see cref="HttpResilienceRetryOptions.BaseDelay"/>. The default, and the
    /// right choice for almost every dependency.
    /// </summary>
    Exponential = 2
}
