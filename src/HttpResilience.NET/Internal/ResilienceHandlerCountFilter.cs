using System.Collections.Concurrent;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HttpResilience.NET.Internal;

/// <summary>
/// Reports a client that carries more resilience handlers than this package added to it (event 12), or that
/// lost some of the ones it added (event 16), or that also carries a Polly v7 handler (event 20).
/// </summary>
/// <remarks>
/// Event 12 is Information, not Warning: an extra handler from <c>AddResilienceHandler</c> is the supported
/// extension point and cannot be told apart from a nested standard handler through public API. Event 16 is a
/// Warning: no supported setup removes this package's handlers after it registered them. Event 20 is a Warning
/// rather than a failure: a v7 policy may only time out or isolate, which this package cannot see, but its retry
/// nests with this pipeline's, and <c>AddResilienceHandler</c> is the supported place for another strategy.
/// </remarks>
internal sealed class ResilienceHandlerCountFilter : IHttpMessageHandlerBuilderFilter
{
    private const string LegacyPollyHandlerName = "Microsoft.Extensions.Http.PolicyHttpMessageHandler";

    private readonly ConcurrentDictionary<string, byte> _reported = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _reportedMissing = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _reportedLegacy = new(StringComparer.Ordinal);

    private readonly HttpResilienceMetrics _metrics;
    private readonly HttpResilienceRegistration _registration;
    private readonly IOptionsMonitor<HttpResilienceOptions> _options;
    private readonly ILogger? _logger;

    /// <param name="registration">The registration, which records each client's pipeline kind.</param>
    /// <param name="options">Each client's live options, which decide its handlers.</param>
    /// <param name="metrics">The gauges, published when the first handler chain is built.</param>
    /// <param name="loggerFactory">Logging, if the application has any.</param>
    public ResilienceHandlerCountFilter(
        HttpResilienceRegistration registration,
        IOptionsMonitor<HttpResilienceOptions> options,
        HttpResilienceMetrics metrics,
        ILoggerFactory? loggerFactory = null)
    {
        _metrics = metrics;
        _registration = registration;
        _options = options;
        _logger = loggerFactory?.CreateLogger(HttpResilienceLogging.ConfigurationCategory);
    }

    /// <summary>
    /// The platform resilience handlers this package adds for these options, or <see langword="null"/> when
    /// it adds none, so a disabled client may carry the platform's own handler.
    /// </summary>
    internal static int? ExpectedHandlers(HttpResilienceOptions options, PipelineKind kind)
    {
        if (!options.Enabled)
        {
            return null;
        }

        // The hedging handler contributes two handlers (routing and hedging); the standard handler one.
        int count = kind is PipelineKind.Hedging ? 2 : 1;

        if (options.ConcurrencyLimiter.Enabled)
        {
            count++;
        }

        // A hedged client's rate limiter, or a standard client's displaced backstop.
        if (options.RateLimiter.Enabled && (kind is PipelineKind.Hedging || !options.ConcurrencyLimiter.Enabled))
        {
            count++;
        }

        return count;
    }

    public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next)
    {
        ArgumentNullException.ThrowIfNull(next);

        return builder =>
        {
            // Before the chain is built, so the gauges exist by the time any pipeline in it can report.
            _metrics.Publish();
            next(builder);

            if (_logger is null ||
                builder.Name is not { } clientName ||
                _registration.KindOf(clientName) is not { } kind ||
                ExpectedHandlers(_options.Get(clientName), kind) is not { } expected)
            {
                return;
            }

            int actual = 0;
            int owned = 0;
            int legacy = 0;
            foreach (DelegatingHandler handler in builder.AdditionalHandlers)
            {
                if (IsLegacyPollyHandler(handler))
                {
                    legacy++;
                }

                if (IsPlatformResilienceHandler(handler))
                {
                    actual++;
                    if (PackageHandlers.IsOwned(handler))
                    {
                        owned++;
                    }
                }
            }

            // Followed by identity, not count, so a consumer's handler in place of ours does not hide the loss.
            if (owned < expected && _reportedMissing.TryAdd(clientName, 0))
            {
                HttpResilienceLogging.ResilienceHandlersRemoved(_logger, clientName, owned, expected);
            }

            if (legacy > 0 && _reportedLegacy.TryAdd(clientName, 0))
            {
                HttpResilienceLogging.LegacyPollyHandlers(_logger, clientName, legacy);
            }

            if (actual <= expected || !_reported.TryAdd(clientName, 0))
            {
                return;
            }

            HttpResilienceLogging.ExtraResilienceHandlers(_logger, clientName, actual, expected);
        };
    }

    /// <summary>
    /// Whether a handler is the platform's resilience handler, a public type.
    /// </summary>
    private static bool IsPlatformResilienceHandler(DelegatingHandler handler) => handler is ResilienceHandler;

    /// <summary>
    /// Whether a handler is, or derives from, the Polly v7 handler of <c>Microsoft.Extensions.Http.Polly</c>
    /// (<c>AddPolicyHandler</c>, <c>AddTransientHttpErrorPolicy</c>). Matched by name, because this package does
    /// not reference that one; <see cref="Type.BaseType"/> and <see cref="Type.FullName"/> are trim-safe.
    /// </summary>
    internal static bool IsLegacyPollyHandler(DelegatingHandler handler)
    {
        for (Type? type = handler.GetType(); type is not null && type != typeof(DelegatingHandler); type = type.BaseType)
        {
            if (string.Equals(type.FullName, LegacyPollyHandlerName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
