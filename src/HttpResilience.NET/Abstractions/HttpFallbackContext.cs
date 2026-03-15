using Polly;

namespace HttpResilience.NET.Abstractions;

/// <summary>
/// Context passed to <see cref="IHttpFallbackHandler"/> when the resilience pipeline invokes fallback after a failure.
/// </summary>
/// <remarks>
/// Do not dispose <see cref="Result"/>; ownership remains with the pipeline. Use it only for reading (e.g. status, headers, or logging).
/// </remarks>
public sealed class HttpFallbackContext
{
    internal HttpFallbackContext(Outcome<HttpResponseMessage> outcome)
    {
        Outcome = outcome;
    }

    /// <summary>
    /// The outcome that triggered the fallback: either a failed result (non-success response) or an exception.
    /// </summary>
    public Outcome<HttpResponseMessage> Outcome { get; }

    /// <summary>
    /// Whether the outcome is a result (response) rather than an exception.
    /// </summary>
    public bool HasResult => Outcome.Result is not null;

    /// <summary>
    /// The failed response if <see cref="HasResult"/> is true; otherwise null.
    /// </summary>
    public HttpResponseMessage? Result => Outcome.Result;

    /// <summary>
    /// The exception if the outcome represents a fault; otherwise null.
    /// </summary>
    public Exception? Exception => Outcome.Exception;
}
