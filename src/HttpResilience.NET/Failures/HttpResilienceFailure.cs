using Polly.CircuitBreaker;
using Polly.RateLimiting;
using Polly.Timeout;

namespace HttpResilience.NET;

/// <summary>
/// Reads the cause of a failed outbound request from its exception, so consumers need no reference to Polly's
/// exception types.
/// </summary>
/// <remarks>
/// The whole inner-exception chain is read, so a failure wrapped in the consumer's own exception keeps its
/// cause. A resilience cause outranks a generic one around it: an <see cref="HttpRequestException"/> wrapping
/// a pipeline timeout is <see cref="HttpResilienceFailureType.Timeout"/>.
/// </remarks>
/// <example>
/// <code language="csharp">
/// catch (Exception ex) when (HttpResilienceFailure.Classify(ex) is HttpResilienceFailureType.CircuitOpen)
/// {
///     return StatusCode(503);
/// }
/// </code>
/// </example>
public static class HttpResilienceFailure
{
    /// <summary>Returns why the request that threw <paramref name="exception"/> failed.</summary>
    /// <param name="exception">The exception thrown by the client, or one wrapping it.</param>
    /// <returns>The cause, or <see cref="HttpResilienceFailureType.None"/> when it is not an outbound failure.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="exception"/> is <see langword="null"/>.</exception>
    public static HttpResilienceFailureType Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        HttpResilienceFailureType found = HttpResilienceFailureType.None;
        int foundRank = 0;
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            (int rank, HttpResilienceFailureType type) = current switch
            {
                TimeoutRejectedException => (6, HttpResilienceFailureType.Timeout),
                BrokenCircuitException => (5, HttpResilienceFailureType.CircuitOpen),
                RateLimiterRejectedException => (4, HttpResilienceFailureType.LimiterRejected),
                // HttpClient.Timeout and connect timeouts: a TaskCanceledException with this inside.
                TimeoutException => (3, HttpResilienceFailureType.Timeout),
                HttpRequestException => (2, HttpResilienceFailureType.RequestFailed),
                OperationCanceledException => (1, HttpResilienceFailureType.Canceled),
                _ => (0, HttpResilienceFailureType.None)
            };

            if (rank > foundRank)
            {
                foundRank = rank;
                found = type;
            }
        }

        return found;
    }

    /// <summary>
    /// Gets how long a rejecting rate limiter asked the caller to wait, for a <c>Retry-After</c> header.
    /// </summary>
    /// <remarks>
    /// Only window and token-bucket rate limiters know when a permit is due; concurrency limiters, the
    /// backstop and an open circuit give none.
    /// </remarks>
    /// <param name="exception">The exception thrown by the client, or one wrapping it.</param>
    /// <param name="retryAfter">The wait, when one was given.</param>
    /// <returns><see langword="true"/> when the chain holds a rate limiter rejection with a wait.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="exception"/> is <see langword="null"/>.</exception>
    public static bool TryGetRetryAfter(Exception exception, out TimeSpan retryAfter)
    {
        ArgumentNullException.ThrowIfNull(exception);

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is RateLimiterRejectedException { RetryAfter: { } wait })
            {
                retryAfter = wait;
                return true;
            }
        }

        retryAfter = default;
        return false;
    }
}
