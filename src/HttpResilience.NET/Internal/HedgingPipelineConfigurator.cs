using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Hedging;

namespace HttpResilience.NET.Internal;

/// <summary>
/// Maps <see cref="HttpResilienceOptions"/> onto Microsoft's standard hedging handler.
/// </summary>
internal static class HedgingPipelineConfigurator
{
    public static Action<HttpStandardHedgingResilienceOptions, IServiceProvider> Create(
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
                PipelineKeySelector.CreateForTracking(options, PipelineKind.Hedging);

            resilience.TotalRequestTimeout.Timeout = options.Timeout.Total;
            resilience.Hedging.Delay = options.Hedging.Delay;
            resilience.Hedging.MaxHedgedAttempts = options.Hedging.MaxHedgedAttempts;

            // Covers hedging after a failed attempt. Hedging after the delay elapses is blocked separately in
            // SuppressUnsafeHedgedAttempts.
            if (options.Hedging.DisableForUnsafeHttpMethods)
            {
                var inner = resilience.Hedging.ShouldHandle;
                resilience.Hedging.ShouldHandle = args =>
                    HttpMethodPredicates.IsSafe(args.Context.GetRequestMessage()?.Method)
                        ? inner(args)
                        : new ValueTask<bool>(false);
            }

            if (logger is not null)
            {
                resilience.Hedging.OnHedging = args =>
                {
                    HttpResilienceLogging.HedgingAttempt(logger, args.AttemptNumber + 1, clientName);
                    return default;
                };
            }

            resilience.Endpoint.Timeout.Timeout = options.Timeout.Attempt;

            // The per-endpoint limiter always exists. Supplying the instances, one per authority, keeps the
            // bound per authority while letting the backstop be configured, logged and reported.
            BackstopLimiters backstops = serviceProvider.GetRequiredKeyedService<BackstopLimiters>(
                new RateLimiterKey(clientName, LimiterKind.Backstop));
            resilience.Endpoint.RateLimiter.RateLimiter = args =>
                backstops.For(trackingKey(args.Context.GetRequestMessage()))
                    .AcquireAsync(1, args.Context.CancellationToken);

            if (logger is not null)
            {
                int backstop = options.ConcurrencyLimiter.Backstop;
                string backstopPath = $"{scope}:ConcurrencyLimiter:Backstop";
                RejectionLogThrottle throttle = RejectionLogThrottle.Create(serviceProvider);
                resilience.Endpoint.RateLimiter.OnRejected = _ =>
                {
                    if (throttle.ShouldLog(out int suppressed))
                    {
                        HttpResilienceLogging.ConcurrencyBackstopRejected(
                            logger, clientName, backstop, suppressed, backstopPath);
                    }

                    return default;
                };
            }

            CircuitBreakerCallbacks.Apply(
                resilience.Endpoint.CircuitBreaker, options.CircuitBreaker, clientName, trackingKey, logger, tracker);

            // Last: anything that changes these values afterwards fails the client (PlatformOptionsGuard).
            PlatformOptionsGuard.Record(resilience, scope);
        };

    /// <summary>
    /// Stops a hedged attempt being created for an unsafe method, including when the hedging delay elapses
    /// on a slow primary, which <c>ShouldHandle</c> does not cover.
    /// </summary>
    /// <remarks>
    /// Must be registered after <c>AddStandardHedgingHandler</c>, whose own <c>PostConfigure</c> sets the
    /// <c>ActionGenerator</c> this wraps. Registered unconditionally, so a consumer's <c>Configure</c> or
    /// <c>PostConfigure</c> of the flag is honoured; the flag is read once, when the platform builds the
    /// pipeline, so writing to the cached options instance later cannot switch the guard off unvalidated.
    /// </remarks>
    public static void SuppressUnsafeHedgedAttempts(IServiceCollection services, string optionsName)
    {
        services.AddOptions<HttpStandardHedgingResilienceOptions>(optionsName)
            .PostConfigure<IOptionsMonitor<HttpResilienceOptions>>((resilience, resilienceOptions) =>
            {
                if (resilienceOptions.Get(optionsName).Hedging.DisableForUnsafeHttpMethods)
                {
                    Func<HedgingActionGeneratorArguments<HttpResponseMessage>, Func<ValueTask<Outcome<HttpResponseMessage>>>?> inner =
                        resilience.Hedging.ActionGenerator;

                    resilience.Hedging.ActionGenerator = args =>
                        HttpMethodPredicates.IsSafe(args.PrimaryContext.GetRequestMessage()?.Method)
                            ? inner(args)
                            : null;
                }

                // The platform's post-configure set the generator after the configurator recorded its values.
                PlatformOptionsGuard.RecordActionGenerator(resilience);
            });
    }
}
