using System.Collections.Frozen;
using System.Net.Http.Headers;
using System.Threading.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;

namespace HttpResilience.NET.Internal;

/// <summary>
/// Maps <see cref="HttpResilienceOptions"/> onto Microsoft's standard resilience handler.
/// </summary>
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

            // A disabled client's handler is removed from its chain; leave the platform defaults in place.
            if (!options.Enabled)
            {
                return;
            }

            ILogger? logger = serviceProvider.GetService<ILoggerFactory>()?.CreateLogger(HttpResilienceLogging.PipelineCategory);
            CircuitBreakerStateTracker? tracker = serviceProvider.GetService<CircuitBreakerStateTracker>();

            Func<HttpRequestMessage?, string> trackingKey =
                PipelineKeySelector.CreateForTracking(options, PipelineKind.Standard);

            resilience.TotalRequestTimeout.Timeout = options.Timeout.Total;
            resilience.AttemptTimeout.Timeout = options.Timeout.Attempt;

            TimeProvider timeProvider = serviceProvider.GetService<TimeProvider>() ?? TimeProvider.System;
            ConfigureRetry(resilience, options, clientName, logger, timeProvider);
            CircuitBreakerCallbacks.Apply(
                resilience.CircuitBreaker, options.CircuitBreaker, clientName, trackingKey, logger, tracker);

            // The handler's limiter slot is never empty: it holds the rate limiter when one is configured,
            // otherwise the concurrency backstop.
            if (options.RateLimiter.Enabled)
            {
                RateLimiter limiter =
                    serviceProvider.GetRequiredKeyedService<RateLimiter>(new RateLimiterKey(clientName));
                resilience.RateLimiter.RateLimiter = args => limiter.AcquireAsync(1, args.Context.CancellationToken);

                if (logger is not null)
                {
                    string permitPath = $"{scope}:{options.RateLimiter.PermitKey()}";
                    RejectionLogThrottle throttle = RejectionLogThrottle.Create(serviceProvider);
                    resilience.RateLimiter.OnRejected = _ =>
                    {
                        if (throttle.ShouldLog(out int suppressed))
                        {
                            HttpResilienceLogging.RateLimiterRejected(logger, clientName, suppressed, permitPath);
                        }

                        return default;
                    };
                }
            }
            else
            {
                // Owned instances, one per pipeline, so the backstop can be reported without its bound
                // changing from per authority to per client.
                int backstop = options.ConcurrencyLimiter.Backstop;
                BackstopLimiters backstops = serviceProvider.GetRequiredKeyedService<BackstopLimiters>(
                    new RateLimiterKey(clientName, LimiterKind.Backstop));
                resilience.RateLimiter.RateLimiter = args =>
                    backstops.For(trackingKey(args.Context.GetRequestMessage()))
                        .AcquireAsync(1, args.Context.CancellationToken);

                if (logger is not null)
                {
                    string backstopPath = $"{scope}:ConcurrencyLimiter:Backstop";
                    RejectionLogThrottle throttle = RejectionLogThrottle.Create(serviceProvider);
                    resilience.RateLimiter.OnRejected = _ =>
                    {
                        if (throttle.ShouldLog(out int suppressed))
                        {
                            HttpResilienceLogging.ConcurrencyBackstopRejected(
                                logger, clientName, backstop, suppressed, backstopPath);
                        }

                        return default;
                    };
                }
            }

            // Last: anything that changes these values afterwards fails the client (PlatformOptionsGuard).
            PlatformOptionsGuard.Record(resilience, scope);
        };

    private static void ConfigureRetry(
        HttpStandardResilienceOptions resilience,
        HttpResilienceOptions options,
        string clientName,
        ILogger? logger,
        TimeProvider timeProvider)
    {
        HttpResilienceRetryOptions retry = options.Retry;

        // Polly requires at least one retry, so "disabled" is expressed through the predicate. Nothing else is
        // written: a disabled schedule is not validated, and the platform's own values are never used.
        if (!retry.Enabled)
        {
            resilience.Retry.MaxRetryAttempts = 1;
            resilience.Retry.ShouldHandle = static _ => new ValueTask<bool>(false);
            return;
        }

        resilience.Retry.MaxRetryAttempts = retry.MaxRetries;
        resilience.Retry.Delay = retry.BaseDelay;
        resilience.Retry.BackoffType = ToDelayBackoffType(retry.BackoffType);
        resilience.Retry.UseJitter = retry.UseJitter;
        resilience.Retry.MaxDelay = retry.MaxDelay;
        resilience.Retry.ShouldRetryAfterHeader = retry.UseRetryAfterHeader;

        if (retry.RetryableMethods is { Count: > 0 } allowList)
        {
            FrozenSet<string> allowed = HttpMethodPredicates.ToMethodSet(allowList);
            var inner = resilience.Retry.ShouldHandle;

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
            // An allow-list of the RFC 9110 safe methods, not the platform helper's deny-list, so unknown
            // methods such as PURGE are never retried.
            var inner = resilience.Retry.ShouldHandle;
            resilience.Retry.ShouldHandle = args =>
                HttpMethodPredicates.IsSafe(args.Context.GetRequestMessage()?.Method)
                    ? inner(args)
                    : new ValueTask<bool>(false);
        }

        if (retry.UseRetryAfterHeader)
        {
            CapRetryAfter(resilience.Retry, retry.MaxDelay, timeProvider);
        }

        if (logger is not null)
        {
            resilience.Retry.OnRetry = args =>
            {
                if (logger.IsEnabled(LogLevel.Debug))
                {
                    int? statusCode = (int?)args.Outcome.Result?.StatusCode;
                    string? exceptionType = args.Outcome.Exception?.GetType().Name;
                    HttpResilienceLogging.RetryAttempt(
                        logger,
                        args.AttemptNumber + 1,
                        clientName,
                        args.RetryDelay.TotalMilliseconds,
                        statusCode,
                        exceptionType);
                }

                return default;
            };
        }
    }

    /// <summary>
    /// Holds <c>Retry-After</c> to <c>Retry:MaxDelay</c>: a longer value ends the retries, so the caller gets the
    /// origin's response at once instead of retrying sooner than the origin asked or waiting past the cap.
    /// </summary>
    /// <remarks>
    /// The platform reads the header in its delay generator, which cannot decline a retry, so the decision is
    /// made in the predicate. The generator is also cut to the cap, for an HTTP-date that moved past it in
    /// between.
    /// </remarks>
    private static void CapRetryAfter(HttpRetryStrategyOptions strategy, TimeSpan maxDelay, TimeProvider timeProvider)
    {
        var shouldHandle = strategy.ShouldHandle;
        strategy.ShouldHandle = args =>
            RetryAfter(args.Outcome.Result, timeProvider) > maxDelay
                ? new ValueTask<bool>(false)
                : shouldHandle(args);

        if (strategy.DelayGenerator is { } generator)
        {
            strategy.DelayGenerator = async args =>
            {
                TimeSpan? delay = await generator(args).ConfigureAwait(false);
                return delay > maxDelay ? maxDelay : delay;
            };
        }
    }

    /// <summary>
    /// The wait a failed response's <c>Retry-After</c> asks for, or <see langword="null"/>. A success is not
    /// read, so the header costs nothing on the common path.
    /// </summary>
    private static TimeSpan? RetryAfter(HttpResponseMessage? response, TimeProvider timeProvider)
    {
        if (response is null || (int)response.StatusCode < 400 ||
            response.Headers.RetryAfter is not RetryConditionHeaderValue header)
        {
            return null;
        }

        return header.Delta ?? (header.Date is { } date ? date - timeProvider.GetUtcNow() : null);
    }

    internal static DelayBackoffType ToDelayBackoffType(HttpResilienceRetryBackoffType value) => value switch
    {
        HttpResilienceRetryBackoffType.Constant => DelayBackoffType.Constant,
        HttpResilienceRetryBackoffType.Linear => DelayBackoffType.Linear,
        _ => DelayBackoffType.Exponential
    };
}
