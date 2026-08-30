using Microsoft.Extensions.Logging;

namespace HttpResilience.NET.Internal;

/// <summary>
/// Structured, source-generated log messages. No request or response content is ever recorded.
/// </summary>
internal static partial class HttpResilienceLogging
{
    // Debug, not Warning: Polly's own telemetry already logs every retry twice at Warning, carrying the
    // pipeline name (which contains the client name), the outcome and the attempt number. A third Warning
    // line per attempt triples log volume at exactly the moment a dependency is failing. What this adds over
    // Polly's lines is the computed delay, which is a debugging detail rather than an alerting signal.
    [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
        Message = "HttpResilience retry {AttemptNumber} for client '{ClientName}' after {RetryDelayMs}ms. Status: {StatusCode}, Exception: {ExceptionType}")]
    public static partial void RetryAttempt(ILogger logger, int attemptNumber, string clientName, double retryDelayMs, int? statusCode, string? exceptionType);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "HttpResilience circuit breaker OPENED for client '{ClientName}' authority '{Authority}'. Break duration: {BreakDurationSeconds}s")]
    public static partial void CircuitBreakerOpened(ILogger logger, string clientName, string authority, double breakDurationSeconds);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information,
        Message = "HttpResilience circuit breaker HALF-OPEN for client '{ClientName}' authority '{Authority}'")]
    public static partial void CircuitBreakerHalfOpen(ILogger logger, string clientName, string authority);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information,
        Message = "HttpResilience circuit breaker CLOSED for client '{ClientName}' authority '{Authority}'")]
    public static partial void CircuitBreakerClosed(ILogger logger, string clientName, string authority);

    [LoggerMessage(EventId = 5, Level = LogLevel.Debug,
        Message = "HttpResilience hedging attempt {AttemptNumber} for client '{ClientName}'")]
    public static partial void HedgingAttempt(ILogger logger, int attemptNumber, string clientName);

    // Warning, not Debug: a rejection here is a request the service refused to send, and the operator has
    // no other way to tell it apart from a configured rate limit -- both are RateLimiterRejectedException on
    // the same instrument.
    [LoggerMessage(EventId = 7, Level = LogLevel.Warning,
        Message = "HttpResilience concurrency backstop rejected a request for client '{ClientName}'. " +
                  "The resilience handler's limiter allows {Backstop} concurrent requests with no queue. " +
                  "Raise '{BackstopPath}' if this client needs more in flight, or find out why requests are " +
                  "piling up -- this usually means the dependency slowed down.")]
    public static partial void ConcurrencyBackstopRejected(ILogger logger, string clientName, int backstop, string backstopPath);

    // Warning, for the same reason as the backstop line above, and this is the more important half: the
    // backstop is the control nobody configured, while these two are the ones an operator chose, alerts on
    // and has to re-size during an incident. All three surface as the same RateLimiterRejectedException on
    // the same instrument, so without these the configured control is quieter than the invisible one.
    [LoggerMessage(EventId = 8, Level = LogLevel.Warning,
        Message = "HttpResilience rate limiter rejected a request for client '{ClientName}'. " +
                  "The permit budget is exhausted and the queue is full. Raise '{PermitPath}' if this " +
                  "client's share of the downstream quota is too small -- the limiter is process-local, so " +
                  "the fleet-wide rate is replicas x clients x that value -- or shed load upstream.")]
    public static partial void RateLimiterRejected(ILogger logger, string clientName, string permitPath);

    [LoggerMessage(EventId = 9, Level = LogLevel.Warning,
        Message = "HttpResilience concurrency limiter rejected a request for client '{ClientName}'. " +
                  "{Limit} logical requests are already in flight and the queue of {QueueLimit} is full. " +
                  "Raise '{LimitPath}' if this client should be allowed to wait on more of the dependency " +
                  "at once, or find out why requests are piling up -- this usually means the dependency " +
                  "slowed down. Raising the queue instead only moves the latency somewhere no timeout " +
                  "in this package can bound it.")]
    public static partial void ConcurrencyLimiterRejected(
        ILogger logger, string clientName, int limit, int queueLimit, string limitPath);

    // Warning, not Information: this is the one message that has to survive a production log pipeline. The
    // state it reports -- a client with no retries, no timeouts and no circuit breaker -- is identical whether
    // it was chosen or forgotten, and it is invisible until the dependency that client calls starts failing.
    [LoggerMessage(EventId = 6, Level = LogLevel.Warning,
        Message = "HttpResilience is registered but disabled for client '{ClientName}'. No retries, timeouts or circuit breaker are applied. Set '{EnabledPath}' to true to enable it, or leave it false deliberately to keep this client's behavior unchanged.")]
    public static partial void ResilienceDisabled(ILogger logger, string clientName, string enabledPath);

    // Warning, and for the same reason as event 6: a client that repeats a mutating request is a state that
    // reads exactly like a client that does not, and it stays invisible until an origin is billed twice.
    // Emitted once per client at startup, before traffic, so that "which of our clients can duplicate a
    // mutation?" is answerable from logs during an incident rather than by grepping configuration across
    // repositories. Both mechanisms report -- the blunt flag and the explicit allow-list -- because the
    // hazard at the origin is identical and only the review trail differs.
    [LoggerMessage(EventId = 10, Level = LogLevel.Warning,
        Message = "HttpResilience will repeat unsafe HTTP methods for client '{ClientName}': {Methods} may be " +
                  "{Verb} more than once, because of '{GuardPath}'. Every repeat delivers the same body to " +
                  "the origin again, so that endpoint must deduplicate on an idempotency key -- and for " +
                  "hedging it must do so under simultaneous arrival, because hedged attempts give it no " +
                  "serialization to rely on. Remove that key if this was not a deliberate decision about " +
                  "one endpoint.")]
    public static partial void UnsafeMethodsRepeated(
        ILogger logger, string clientName, string methods, string verb, string guardPath);

    // Information, not Warning, for the reason spelled out on ResilienceHandlerCountFilter: the excess is not
    // attributable through public API, and the pattern this package documents -- AddResilienceHandler -- adds
    // a handler here too and is correct. A Warning on the documented pattern is a Warning operators filter
    // out, which would cost more than it buys. What this buys is that "does any client here have two nested
    // pipelines?" is answerable from logs at all, which it was not.
    [LoggerMessage(EventId = 12, Level = LogLevel.Information,
        Message = "HttpResilience: client '{ClientName}' has {Actual} resilience handlers where this package " +
                  "added {Expected}. If the extra one came from AddResilienceHandler this is expected and " +
                  "composes correctly. If it came from AddStandardResilienceHandler or " +
                  "AddStandardHedgingHandler on a client that already has HttpResilience, two pipelines are " +
                  "NESTED rather than merged: retries multiply rather than add -- three configured attempts " +
                  "become nine origin calls -- and the total timeout is applied twice. Check which, and use " +
                  "AddResilienceHandler for anything this schema does not express.")]
    public static partial void ExtraResilienceHandlers(
        ILogger logger, string clientName, int actual, int expected);

    // Information, not Warning, and this is the difference from events 6 and 10: those report a state that is
    // wrong often enough to interrupt for, while this one is frequently correct -- a busy client meets the
    // rate easily. What makes it worth a line at all is that the arithmetic is invisible. A client with the
    // default thresholds and a few requests a second per replica has a breaker in its configuration, a
    // breaker in its runbook, and no breaker in effect; nothing in its telemetry says so, because a breaker
    // that never opens emits exactly what a healthy one emits.
    [LoggerMessage(EventId = 11, Level = LogLevel.Information,
        Message = "HttpResilience circuit breaker for client '{ClientName}' needs {AttemptsPerSecond} failing " +
                  "attempts per second sustained in ONE replica before it can open -- about " +
                  "{RequestsPerSecond} failing caller requests per second at this client's retry count. " +
                  "That is MinimumThroughput {MinimumThroughput} over SamplingDuration {SamplingSeconds}s, " +
                  "observed per replica, not fleet-wide. Below that rate this client has timeouts and no " +
                  "circuit breaker: lower '{ThroughputPath}' if it is quieter than that.")]
    public static partial void CircuitBreakerReach(
        ILogger logger,
        string clientName,
        string attemptsPerSecond,
        string requestsPerSecond,
        int minimumThroughput,
        double samplingSeconds,
        string throughputPath);

    // Warning, and the same argument as events 6 and 10: a bound an operator believes is in force and is not.
    // Connection:AllowAutoRedirect resolves to false for every hedged client, because the authority allow-list
    // is enforced above the primary handler while a 3xx is resolved below it. When the primary handler is
    // neither a SocketsHttpHandler nor an HttpClientHandler there is nothing to set the flag on, and this
    // returned in silence. Not thrown, unlike the Connection:Enabled case -- see TryDisableAutoRedirect for
    // why the two differ, and why the answer is not to make the stub pattern fail.
    [LoggerMessage(EventId = 13, Level = LogLevel.Warning,
        Message = "HttpResilience could not apply the redirect bound for client '{ClientName}': its primary " +
                  "handler is a {HandlerType}, which is neither a SocketsHttpHandler nor an HttpClientHandler, " +
                  "so AllowAutoRedirect could not be set to false. A stub or in-memory handler resolves no " +
                  "redirects and is unaffected. A handler that wraps a SocketsHttpHandler of its own does " +
                  "resolve them, and then a 3xx from a listed authority reaches a destination this client's " +
                  "PipelineSelection:Authorities allow-list never sees -- with every custom credential header " +
                  "such as X-Api-Key re-sent verbatim across the hop, because the runtime strips only " +
                  "Authorization. Supply a SocketsHttpHandler for this client, or state " +
                  "Connection:AllowAutoRedirect true if redirects are intended.")]
    public static partial void RedirectBoundNotApplied(ILogger logger, string clientName, string handlerType);
}
