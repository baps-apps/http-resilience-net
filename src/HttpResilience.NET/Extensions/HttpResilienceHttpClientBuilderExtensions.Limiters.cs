using System.Threading.RateLimiting;
using HttpResilience.NET;
using HttpResilience.NET.Internal;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.RateLimiting;

namespace Microsoft.Extensions.DependencyInjection;

public static partial class HttpResilienceHttpClientBuilderExtensions
{
    private static void AddAuthorityAllowList(IHttpClientBuilder builder)
    {
        string optionsName = builder.Name;
        builder.AddHttpMessageHandler(serviceProvider =>
            new AuthorityAllowListHandler(
                AuthorityIndex.Create(Live(serviceProvider, optionsName).PipelineSelection)));
    }

    /// <summary>
    /// Re-adds the concurrency backstop as its own handler when a rate limiter takes the standard handler's
    /// only limiter slot.
    /// </summary>
    private static void AddConcurrencyBackstop(IHttpClientBuilder builder, string scope)
    {
        string clientName = builder.Name;
        string backstopPath = $"{scope}:ConcurrencyLimiter:Backstop";

        AddOwnedConcurrencyLimiter(builder, LimiterKind.Backstop, concurrency => new ConcurrencyLimiterOptions
        {
            PermitLimit = concurrency.Backstop,
            QueueLimit = 0
        });

        builder.AddResilienceHandler(ConcurrencyBackstopHandlerName, (pipeline, context) =>
        {
            HttpResilienceOptions live = Live(context.ServiceProvider, clientName);
            if (!IsBackstopDisplaced(live))
            {
                return;
            }

            int backstop = live.ConcurrencyLimiter.Backstop;
            ILogger? logger = LimiterLogger(context.ServiceProvider);
            RejectionLogThrottle throttle = RejectionLogThrottle.Create(context.ServiceProvider);
            RateLimiter limiter = context.ServiceProvider.GetRequiredKeyedService<RateLimiter>(
                new RateLimiterKey(clientName, LimiterKind.Backstop));

            pipeline.AddRateLimiter(new RateLimiterStrategyOptions
            {
                RateLimiter = args => limiter.AcquireAsync(1, args.Context.CancellationToken),
                OnRejected = logger is null ? null : _ =>
                {
                    if (throttle.ShouldLog(out int suppressed))
                    {
                        HttpResilienceLogging.ConcurrencyBackstopRejected(
                            logger, clientName, backstop, suppressed, backstopPath);
                    }

                    return default;
                }
            });
        });
    }

    /// <summary>
    /// Registers a concurrency limiter instance this package owns, so its statistics can be published as
    /// gauges. A keyed singleton created on first use, so the container disposes it.
    /// </summary>
    private static void AddOwnedConcurrencyLimiter(
        IHttpClientBuilder builder,
        LimiterKind kind,
        Func<HttpResilienceConcurrencyLimiterOptions, ConcurrencyLimiterOptions> shape)
    {
        string optionsName = builder.Name;

        builder.Services.AddKeyedSingleton<RateLimiter>(
            new RateLimiterKey(optionsName, kind),
            (serviceProvider, key) =>
            {
                RateLimiter limiter = new ConcurrencyLimiter(
                    shape(Live(serviceProvider, optionsName).ConcurrencyLimiter));

                serviceProvider.GetService<HttpResilienceMetrics>()
                    ?.Track(((RateLimiterKey)key!).ClientName, kind, limiter);

                return limiter;
            });
    }

    /// <summary>
    /// Adds the client's concurrency cap outside the platform handler, so one slot covers a request and all
    /// of its retries.
    /// </summary>
    private static void AddConcurrencyLimiter(IHttpClientBuilder builder, string scope)
    {
        string clientName = builder.Name;
        string limitPath = $"{scope}:ConcurrencyLimiter:Limit";

        AddOwnedConcurrencyLimiter(builder, LimiterKind.Concurrency, concurrency => new ConcurrencyLimiterOptions
        {
            PermitLimit = concurrency.Limit!.Value,
            QueueLimit = concurrency.QueueLimit
        });

        builder.AddResilienceHandler(ConcurrencyLimiterHandlerName, (pipeline, context) =>
        {
            HttpResilienceOptions live = Live(context.ServiceProvider, clientName);
            if (!HasConcurrencyLimiter(live))
            {
                return;
            }

            HttpResilienceConcurrencyLimiterOptions concurrency = live.ConcurrencyLimiter;
            int limit = concurrency.Limit!.Value;
            int queueLimit = concurrency.QueueLimit;
            ILogger? logger = LimiterLogger(context.ServiceProvider);
            RejectionLogThrottle throttle = RejectionLogThrottle.Create(context.ServiceProvider);
            RateLimiter limiter = context.ServiceProvider.GetRequiredKeyedService<RateLimiter>(
                new RateLimiterKey(clientName, LimiterKind.Concurrency));

            pipeline.AddRateLimiter(new RateLimiterStrategyOptions
            {
                RateLimiter = args => limiter.AcquireAsync(1, args.Context.CancellationToken),
                OnRejected = logger is null ? null : _ =>
                {
                    if (throttle.ShouldLog(out int suppressed))
                    {
                        HttpResilienceLogging.ConcurrencyLimiterRejected(
                            logger, clientName, limit, queueLimit, suppressed, limitPath);
                    }

                    return default;
                }
            });
        });
    }

    private static ILogger? LimiterLogger(IServiceProvider serviceProvider) =>
        serviceProvider.GetService<ILoggerFactory>()?.CreateLogger(HttpResilienceLogging.LimiterCategory);

    /// <summary>
    /// Adds the hedged client's rate limiter outside the hedging handler, so one permit covers a request and
    /// all of its hedged attempts.
    /// </summary>
    private static void AddHedgedRateLimiter(IHttpClientBuilder builder, string scope)
    {
        string clientName = builder.Name;

        builder.AddResilienceHandler(RateLimiterHandlerName, (pipeline, context) =>
        {
            HttpResilienceOptions live = Live(context.ServiceProvider, clientName);
            if (!HasRateLimiter(live))
            {
                return;
            }

            string permitPath = $"{scope}:{live.RateLimiter.PermitKey()}";
            RateLimiter limiter =
                context.ServiceProvider.GetRequiredKeyedService<RateLimiter>(new RateLimiterKey(clientName));
            ILogger? logger = LimiterLogger(context.ServiceProvider);
            RejectionLogThrottle throttle = RejectionLogThrottle.Create(context.ServiceProvider);

            pipeline.AddRateLimiter(new RateLimiterStrategyOptions
            {
                RateLimiter = args => limiter.AcquireAsync(1, args.Context.CancellationToken),
                OnRejected = logger is null ? null : _ =>
                {
                    if (throttle.ShouldLog(out int suppressed))
                    {
                        HttpResilienceLogging.RateLimiterRejected(logger, clientName, suppressed, permitPath);
                    }

                    return default;
                }
            });
        });
    }
}
