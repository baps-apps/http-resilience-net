using System.Collections.Frozen;
using System.Threading.RateLimiting;
using HttpResilience.NET.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;

namespace HttpResilience.NET.Internal;

/// <summary>
/// Maps <see cref="HttpResilienceOptions"/> onto Microsoft's standard resilience handler.
/// </summary>
/// <remarks>
/// The handler owns the strategy ordering -- rate limiter, total timeout, retry, circuit breaker, attempt
/// timeout -- and this type only supplies values and predicates. Nothing here composes a pipeline.
/// <para>
/// The values are read from <see cref="IOptionsMonitor{TOptions}"/> <i>inside</i> the delegate, which the
/// platform invokes when it first builds the pipeline -- after every <c>Configure</c> and
/// <c>PostConfigure</c>. So the options a consumer reads back are the same object the pipeline was built
/// from, and "what you read is what is running" holds by construction rather than by a validator comparing a
/// registration snapshot against the registered options. What a late change cannot reach is which handlers
/// exist; see <see cref="StructuralDecisions"/>.
/// </para>
/// </remarks>
internal static class StandardPipelineConfigurator
{
    public static Action<HttpStandardResilienceOptions, IServiceProvider> Create(
        string optionsName,
        string clientName,
        string scope) =>
        (resilience, serviceProvider) =>
        {
            HttpResilienceOptions options = serviceProvider
                .GetRequiredService<IOptionsMonitor<HttpResilienceOptions>>().Get(optionsName);

            ILogger? logger = serviceProvider.GetService<ILoggerFactory>()?.CreateLogger("HttpResilience");
            CircuitBreakerStateTracker? tracker = serviceProvider.GetService<CircuitBreakerStateTracker>();

            // Resolved for its side effect: constructing it creates the meter and publishes the gauges, and
            // this is the earliest point at which either has anything to report.
            _ = serviceProvider.GetService<HttpResilienceMetrics>();
            Func<HttpRequestMessage?, string> trackingKey =
                PipelineKeySelector.CreateForTracking(options, PipelineKind.Standard);

            resilience.TotalRequestTimeout.Timeout = options.Timeout.Total;
            resilience.AttemptTimeout.Timeout = options.Timeout.Attempt;

            ConfigureRetry(resilience, options, clientName, logger);
            CircuitBreakerCallbacks.Apply(
                resilience.CircuitBreaker, options.CircuitBreaker, clientName, trackingKey, logger, tracker);

            // The handler's limiter slot is never absent. Left alone it is a concurrency limiter of 1,000
            // with no queue, which is a scaling cliff that appears in no configuration file and surfaces as
            // a RateLimiterRejectedException naming a limiter the operator never enabled. Either the schema's
            // backstop goes here, or -- when a rate limiter is configured -- the backstop moves to its own
            // handler and this slot carries the rate limiter instead.
            if (options.RateLimiter.Enabled)
            {
                RateLimiter limiter =
                    serviceProvider.GetRequiredKeyedService<RateLimiter>(new RateLimiterKey(clientName));
                resilience.RateLimiter.RateLimiter = args => limiter.AcquireAsync(1, args.Context.CancellationToken);

                // A configured rejection has to be at least as loud as the backstop's. Both are the same
                // exception type on the same instrument, and this is the one an operator chose the number for.
                if (logger is not null)
                {
                    string permitPath = $"{scope}:{options.RateLimiter.PermitKey}";
                    resilience.RateLimiter.OnRejected = _ =>
                    {
                        HttpResilienceLogging.RateLimiterRejected(logger, clientName, permitPath);
                        return default;
                    };
                }
            }
            else
            {
                int backstop = options.ConcurrencyLimiter.Backstop;
                resilience.RateLimiter.DefaultRateLimiterOptions.PermitLimit = backstop;
                resilience.RateLimiter.DefaultRateLimiterOptions.QueueLimit = 0;

                // With no rate limiter configured, every rejection from this slot is the backstop. Saying so
                // is the difference between an operator seeing a RateLimiterRejectedException on a client
                // that has no rate limiter and knowing which number to change.
                if (logger is not null)
                {
                    string backstopPath = $"{scope}:ConcurrencyLimiter:Backstop";
                    resilience.RateLimiter.OnRejected = _ =>
                    {
                        HttpResilienceLogging.ConcurrencyBackstopRejected(logger, clientName, backstop, backstopPath);
                        return default;
                    };
                }
            }
        };

    private static void ConfigureRetry(
        HttpStandardResilienceOptions resilience,
        HttpResilienceOptions options,
        string clientName,
        ILogger? logger)
    {
        RetryOptions retry = options.Retry;

        // The underlying strategy requires at least one attempt, so retries are switched off through the
        // predicate rather than by setting the count to zero, which it rejects.
        resilience.Retry.MaxRetryAttempts = retry.Enabled ? retry.MaxRetries : 1;
        resilience.Retry.Delay = retry.BaseDelay;
        resilience.Retry.BackoffType = ToDelayBackoffType(retry.BackoffType);
        resilience.Retry.UseJitter = retry.UseJitter;
        resilience.Retry.ShouldRetryAfterHeader = retry.UseRetryAfterHeader;

        if (!retry.Enabled)
        {
            resilience.Retry.ShouldHandle = static _ => new ValueTask<bool>(false);
            return;
        }

        if (retry.RetryableMethods is { Count: > 0 } allowList)
        {
            // An explicit allow-list is the supported way to retry a non-idempotent method: the decision is
            // named in configuration rather than inherited from a default.
            FrozenSet<string> allowed = HttpMethodPredicates.ToMethodSet(allowList);
            var inner = resilience.Retry.ShouldHandle;

            // Not an async lambda. A method outside the allow-list is the common case on a client that opted
            // one in, and `async` would box a state machine to return a value it already has. Returning
            // inner(args) unchanged on the matching path also keeps the platform predicate's own
            // synchronous completion synchronous.
            resilience.Retry.ShouldHandle = args =>
            {
                HttpMethod? method = args.Context.GetRequestMessage()?.Method;
                return method is not null && allowed.Contains(method.Method)
                    ? inner(args)
                    : new ValueTask<bool>(false);
            };
        }
        else if (retry.DisableForUnsafeHttpMethods)
        {
            // Not resilience.Retry.DisableForUnsafeHttpMethods(): the platform's helper is a deny-list of
            // POST, PATCH, PUT, DELETE and CONNECT, so it retries any method it has never heard of --
            // a WebDAV MOVE, a cache PURGE, any new HttpMethod("..."). This package's claim is about unsafe
            // methods, not about five of them, so the predicate is the RFC 9110 safe set instead.
            var inner = resilience.Retry.ShouldHandle;
            resilience.Retry.ShouldHandle = args =>
                HttpMethodPredicates.IsSafe(args.Context.GetRequestMessage()?.Method)
                    ? inner(args)
                    : new ValueTask<bool>(false);
        }

        if (logger is not null)
        {
            resilience.Retry.OnRetry = args =>
            {
                HttpResilienceLogging.RetryAttempt(
                    logger,
                    args.AttemptNumber + 1,
                    clientName,
                    args.RetryDelay.TotalMilliseconds,
                    (int?)args.Outcome.Result?.StatusCode,
                    args.Outcome.Exception?.GetType().Name);
                return default;
            };
        }
    }

    internal static DelayBackoffType ToDelayBackoffType(RetryBackoffType value) => value switch
    {
        RetryBackoffType.Constant => DelayBackoffType.Constant,
        RetryBackoffType.Linear => DelayBackoffType.Linear,
        _ => DelayBackoffType.Exponential
    };
}
