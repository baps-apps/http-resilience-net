using Microsoft.Extensions.Logging;

namespace HttpResilience.NET.Internal;

/// <summary>
/// Source-generated log messages. Request and response content is never logged.
/// </summary>
internal static partial class HttpResilienceLogging
{
    /// <summary>Notices about how a client is configured: events 6 and 10 to 21.</summary>
    public const string ConfigurationCategory = "HttpResilience.NET.Configuration";

    /// <summary>Per-request pipeline events: retries, hedged attempts and circuit breaker transitions (1 to 5).</summary>
    public const string PipelineCategory = "HttpResilience.NET.Pipeline";

    /// <summary>Limiter rejections: events 7 to 9.</summary>
    public const string LimiterCategory = "HttpResilience.NET.Limiter";

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

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning,
        Message = "HttpResilience is disabled for client '{ClientName}' by '{EnabledPath}': no retries, " +
                  "timeouts or circuit breaker are applied.")]
    public static partial void ResilienceDisabled(ILogger logger, string clientName, string enabledPath);

    // Events 7-9 are throttled to one entry per limiter per RejectionLogThrottle.Interval.
    [LoggerMessage(EventId = 7, Level = LogLevel.Warning,
        Message = "HttpResilience concurrency backstop rejected a request for client '{ClientName}' " +
                  "({Backstop} concurrent requests, no queue); {SuppressedCount} more since the last entry. " +
                  "Raise '{BackstopPath}' or check whether the dependency has slowed down.")]
    public static partial void ConcurrencyBackstopRejected(
        ILogger logger, string clientName, int backstop, int suppressedCount, string backstopPath);

    [LoggerMessage(EventId = 8, Level = LogLevel.Warning,
        Message = "HttpResilience rate limiter rejected a request for client '{ClientName}'; " +
                  "{SuppressedCount} more since the last entry. Raise '{PermitPath}' or reduce load. The " +
                  "limit applies per process.")]
    public static partial void RateLimiterRejected(
        ILogger logger, string clientName, int suppressedCount, string permitPath);

    [LoggerMessage(EventId = 9, Level = LogLevel.Warning,
        Message = "HttpResilience concurrency limiter rejected a request for client '{ClientName}' " +
                  "({Limit} in flight, queue of {QueueLimit} full); {SuppressedCount} more since the last " +
                  "entry. Raise '{LimitPath}' or check whether the dependency has slowed down.")]
    public static partial void ConcurrencyLimiterRejected(
        ILogger logger, string clientName, int limit, int queueLimit, int suppressedCount, string limitPath);

    [LoggerMessage(EventId = 10, Level = LogLevel.Warning,
        Message = "HttpResilience may send unsafe HTTP methods more than once for client '{ClientName}': " +
                  "{Methods} may be {Verb}, because of '{GuardPath}'. The endpoint must deduplicate on an " +
                  "idempotency key.")]
    public static partial void UnsafeMethodsRepeated(
        ILogger logger, string clientName, string methods, string verb, string guardPath);

    [LoggerMessage(EventId = 11, Level = LogLevel.Information,
        Message = "HttpResilience circuit breaker for client '{ClientName}' needs {AttemptsPerSecond} failing " +
                  "attempts per second (about {RequestsPerSecond} failing caller requests per second) before " +
                  "it can open: MinimumThroughput {MinimumThroughput} over {SamplingSeconds}s, counted " +
                  "per replica, not fleet-wide. Lower '{ThroughputPath}' for a quieter client.")]
    public static partial void CircuitBreakerReach(
        ILogger logger,
        string clientName,
        string attemptsPerSecond,
        string requestsPerSecond,
        int minimumThroughput,
        double samplingSeconds,
        string throughputPath);

    [LoggerMessage(EventId = 12, Level = LogLevel.Information,
        Message = "HttpResilience: client '{ClientName}' has {Actual} resilience handlers; this package added " +
                  "{Expected}. Expected if the extra one came from AddResilienceHandler. If it came from " +
                  "AddStandardResilienceHandler or AddStandardHedgingHandler, two pipelines are NESTED and " +
                  "retries multiply: three attempts become nine origin calls. Remove it, and use " +
                  "AddResilienceHandler for an extra strategy.")]
    public static partial void ExtraResilienceHandlers(
        ILogger logger, string clientName, int actual, int expected);

    [LoggerMessage(EventId = 13, Level = LogLevel.Warning,
        Message = "HttpResilience could not disable redirects for client '{ClientName}': its primary handler " +
                  "{HandlerType} has no AllowAutoRedirect setting. If it wraps a SocketsHttpHandler, redirects " +
                  "can reach hosts outside PipelineSelection:Authorities. Use a SocketsHttpHandler, or set " +
                  "Connection:AllowAutoRedirect to true if redirects are intended.")]
    public static partial void RedirectBoundNotApplied(ILogger logger, string clientName, string handlerType);

    [LoggerMessage(EventId = 14, Level = LogLevel.Warning,
        Message = "HttpResilience retry schedule for client '{ClientName}' can exceed Timeout:Total " +
                  "({TotalTimeout}): {Attempts} attempts of {AttemptTimeout} plus {Backoff} backoff is " +
                  "{WorstCase} if every attempt times out, so later retries may not run. Raise " +
                  "'{TotalPath}' or lower the retry count, unless Timeout:Total is meant as a hard cap.")]
    public static partial void RetryBudgetExceedsTotal(
        ILogger logger,
        string clientName,
        TimeSpan totalTimeout,
        int attempts,
        TimeSpan attemptTimeout,
        TimeSpan backoff,
        TimeSpan worstCase,
        string totalPath);

    [LoggerMessage(EventId = 16, Level = LogLevel.Warning,
        Message = "HttpResilience: client '{ClientName}' kept {Remaining} of the {Expected} resilience handlers " +
                  "this package added; something registered after AddHttpResilienceHandler removed them, so its " +
                  "requests run without the retries, timeouts or circuit breaker configured for it. If that was " +
                  "RemoveAllResilienceHandlers(), remove the call: this package already replaces resilience " +
                  "handlers registered before it, including .NET Aspire's.")]
    public static partial void ResilienceHandlersRemoved(
        ILogger logger, string clientName, int remaining, int expected);

    [LoggerMessage(EventId = 17, Level = LogLevel.Information,
        Message = "HttpResilience replaced {Count} resilience pipeline(s) registered on client '{ClientName}' before " +
                  "its own, so the two do not nest and multiply retries; its pipeline takes the outermost one's " +
                  "place, so handlers that pipeline wrapped, such as service discovery, stay inside it. " +
                  "Typically the standard handler from " +
                  "ConfigureHttpClientDefaults (.NET Aspire ServiceDefaults). To add a strategy alongside this " +
                  "package's, call AddResilienceHandler after AddHttpResilienceHandler.")]
    public static partial void EarlierResilienceHandlersReplaced(ILogger logger, string clientName, int count);

    [LoggerMessage(EventId = 18, Level = LogLevel.Warning,
        Message = "HttpResilience configuration declares SchemaVersion {Declared}, newer than this package " +
                  "({Package}), and holds {Count} key(s) this version does not know: {Keys}. They have no effect " +
                  "until HttpResilience.NET is upgraded to {Declared} or later. A misspelled key here is ignored too.")]
    public static partial void KeysFromNewerSchemaIgnored(
        ILogger logger, string declared, string package, int count, string keys);

    [LoggerMessage(EventId = 19, Level = LogLevel.Information,
        Message = "HttpResilience removed {Count} resilience pipeline(s) that ConfigureHttpClientDefaults (.NET " +
                  "Aspire ServiceDefaults) added to client '{ClientName}', which is disabled: the platform's " +
                  "standard handler retries every method, POST included. The client has no retries, timeouts or " +
                  "circuit breaker.")]
    public static partial void DefaultsResilienceHandlersRemoved(ILogger logger, string clientName, int count);

    [LoggerMessage(EventId = 20, Level = LogLevel.Warning,
        Message = "HttpResilience: client '{ClientName}' also has {Count} Polly v7 handler(s) " +
                  "(Microsoft.Extensions.Http.Polly: AddPolicyHandler, AddTransientHttpErrorPolicy, also from " +
                  "ConfigureHttpClientDefaults). A v7 retry policy NESTS with this package's pipeline and retries " +
                  "multiply: three retries around this package's three attempts are twelve origin calls, and its " +
                  "predicate retries POST. Remove it; move its values to the client's HttpResilience section, and " +
                  "add a strategy the schema lacks with AddResilienceHandler.")]
    public static partial void LegacyPollyHandlers(ILogger logger, string clientName, int count);

    [LoggerMessage(EventId = 21, Level = LogLevel.Warning,
        Message = "HttpResilience hedging for client '{ClientName}' starts the next attempt after {Delay}, so " +
                  "nearly every request is sent {Attempts} times at once, even when the dependency is healthy. " +
                  "Raise '{DelayPath}' to about the dependency's p95 latency, or lower Hedging:MaxHedgedAttempts.")]
    public static partial void HedgingFansOutEveryRequest(
        ILogger logger, string clientName, TimeSpan delay, int attempts, string delayPath);

    [LoggerMessage(EventId = 15, Level = LogLevel.Information,
        Message = "HttpResilience client '{ClientName}' uses the {Pipeline} pipeline: {Summary}.")]
    public static partial void ClientSummary(ILogger logger, string clientName, string pipeline, string summary);
}
