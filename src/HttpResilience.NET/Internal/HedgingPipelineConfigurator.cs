using HttpResilience.NET.Options;
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
        string clientName) =>
        (resilience, serviceProvider) =>
        {
            // Read live, for the reason given on StandardPipelineConfigurator: the delegate runs when the
            // pipeline is built, so these are the same options a consumer reads back.
            HttpResilienceOptions options = serviceProvider
                .GetRequiredService<IOptionsMonitor<HttpResilienceOptions>>().Get(optionsName);

            ILogger? logger = serviceProvider.GetService<ILoggerFactory>()?.CreateLogger("HttpResilience");
            CircuitBreakerStateTracker? tracker = serviceProvider.GetService<CircuitBreakerStateTracker>();

            // Resolved for its side effect: constructing it creates the meter and publishes the gauges, and
            // this is the earliest point at which either has anything to report.
            _ = serviceProvider.GetService<HttpResilienceMetrics>();
            Func<HttpRequestMessage?, string> trackingKey =
                PipelineKeySelector.CreateForTracking(options, PipelineKind.Hedging);

            resilience.TotalRequestTimeout.Timeout = options.Timeout.Total;
            resilience.Hedging.Delay = options.Hedging.Delay;
            resilience.Hedging.MaxHedgedAttempts = options.Hedging.MaxHedgedAttempts;

            // Closes the outcome path: an attempt that completed and failed must not start another one.
            // The timer path is closed separately in SuppressUnsafeHedgedAttempts, and that is the one that
            // actually duplicates requests. The flag is read here rather than around the assignment so that
            // it is a value like any other -- read when the pipeline is built, from the options a consumer
            // reads back.
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

            // The endpoint pipeline's limiter is the platform's, always present, and per authority. Left at
            // its default it is an invisible 1,000-concurrent cap; mapping the schema onto it makes the
            // number one an operator can see and change.
            resilience.Endpoint.RateLimiter.DefaultRateLimiterOptions.PermitLimit =
                options.ConcurrencyLimiter.Backstop;
            resilience.Endpoint.RateLimiter.DefaultRateLimiterOptions.QueueLimit = 0;

            CircuitBreakerCallbacks.Apply(
                resilience.Endpoint.CircuitBreaker, options.CircuitBreaker, clientName, trackingKey, logger, tracker);
        };

    /// <summary>
    /// Stops a hedged attempt being created at all for a mutating request.
    /// </summary>
    /// <remarks>
    /// Polly starts a supplementary attempt for two reasons, and <c>ShouldHandle</c> gates only one of them.
    /// When the hedging delay elapses while every attempt is still running, the next attempt is created
    /// without consulting any outcome predicate -- which is precisely the case hedging exists for, a slow
    /// primary. A guard written only as <c>ShouldHandle</c> therefore lets a slow POST reach the origin
    /// <c>1 + MaxHedgedAttempts</c> times, with its body, while every hedging test that uses a fast origin
    /// passes. <c>ActionGenerator</c> returning <see langword="null"/> is the only hook on that path.
    /// <para>
    /// This has to be a <c>PostConfigure</c> registered <b>after</b> <c>AddStandardHedgingHandler</c>:
    /// that method installs its own <c>ActionGenerator</c> in a <c>PostConfigure</c> of its own, to snapshot
    /// and re-issue the request, so anything set through <c>Configure</c> is overwritten and never runs.
    /// The platform's generator is wrapped rather than replaced, so request cloning and routing are kept.
    /// </para>
    /// <para>
    /// It is registered whether or not <see cref="HedgingOptions.DisableForUnsafeHttpMethods"/> is on, and
    /// consults the option when an attempt is considered. Registering it conditionally would have made a
    /// safety guard something a later configuration change could delete by deleting its registration; this
    /// way the option is a value like every other, and the only thing that can turn the guard off is the
    /// option itself.
    /// </para>
    /// </remarks>
    public static void SuppressUnsafeHedgedAttempts(IServiceCollection services, string optionsName)
    {
        services.AddOptions<HttpStandardHedgingResilienceOptions>(optionsName)
            .PostConfigure<IOptionsMonitor<HttpResilienceOptions>>((resilience, resilienceOptions) =>
            {
                Func<HedgingActionGeneratorArguments<HttpResponseMessage>, Func<ValueTask<Outcome<HttpResponseMessage>>>?> inner =
                    resilience.Hedging.ActionGenerator;

                resilience.Hedging.ActionGenerator = args =>
                    !resilienceOptions.Get(optionsName).Hedging.DisableForUnsafeHttpMethods ||
                    HttpMethodPredicates.IsSafe(args.PrimaryContext.GetRequestMessage()?.Method)
                        ? inner(args)
                        : null;
            });
    }
}
