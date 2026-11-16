namespace HttpResilience.NET;

/// <summary>
/// Why an outbound request failed, as <see cref="HttpResilienceFailure.Classify"/> reads it from the exception.
/// </summary>
/// <remarks>
/// Map these to the caller's response, for example <see cref="Timeout"/> to 504, <see cref="CircuitOpen"/> and
/// <see cref="LimiterRejected"/> to 503, <see cref="RequestFailed"/> to 502. <see cref="Canceled"/> and
/// <see cref="None"/> are not dependency failures.
/// </remarks>
public enum HttpResilienceFailureType
{
    /// <summary>
    /// Not an outbound failure: a bug, or a request the client refused to send (a hedged client's unlisted
    /// authority throws <see cref="InvalidOperationException"/>).
    /// </summary>
    None = 0,

    /// <summary>
    /// A cancellation token passed by the caller fired. The pipeline's own timeouts are <see cref="Timeout"/>.
    /// </summary>
    Canceled = 1,

    /// <summary>
    /// <c>Timeout:Attempt</c>, <c>Timeout:Total</c> or <c>Timeout:Client</c> ended the request, or the
    /// connection could not be made in time.
    /// </summary>
    Timeout = 2,

    /// <summary>The circuit breaker is open: the request was not sent.</summary>
    CircuitOpen = 3,

    /// <summary>
    /// The rate limiter, the concurrency limiter or the concurrency backstop rejected the request: it was not sent.
    /// </summary>
    LimiterRejected = 4,

    /// <summary>
    /// The request failed after every allowed attempt: a connection or protocol error, or a non-success status
    /// turned into an exception by <see cref="HttpResponseMessage.EnsureSuccessStatusCode"/>.
    /// </summary>
    RequestFailed = 5
}
