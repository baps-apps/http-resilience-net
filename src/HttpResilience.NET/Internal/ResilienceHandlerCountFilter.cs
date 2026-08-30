using System.Collections.Concurrent;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;

namespace HttpResilience.NET.Internal;

/// <summary>
/// Reports a client that carries more resilience handlers than this package added to it.
/// </summary>
/// <remarks>
/// <c>AddHttpResilience</c> refuses a second call on the same client, because two pipelines nest rather than
/// merge. That guard is a ledger of clients that called <i>this package's</i> API, and it cannot see the same
/// mistake made through the platform's -- which is the API every Microsoft Learn page shows:
/// <code language="csharp">
/// services.AddHttpClient("Orders")
///     .AddHttpResilience()
///     .AddStandardResilienceHandler();   // nests a second pipeline
/// </code>
/// Measured: one GET makes <b>nine</b> origin calls -- three configured attempts, each retried three times by
/// the outer pipeline -- and the total timeout is applied twice. Nothing throws and, before this type, nothing
/// logged. (The third symptom is now gone: the second platform handler used to reset
/// <see cref="HttpClient.Timeout"/> to infinite over the finite one this package puts back, which a
/// post-configure on <c>HttpClientFactoryOptions</c> now wins.)
/// <para>
/// <b>Why this reports rather than fails.</b> The excess is not attributable through public API. The escape
/// hatch this package tells consumers to use -- <c>AddResilienceHandler</c> -- adds a <c>ResilienceHandler</c>
/// to the chain exactly as a second standard handler does, and it is <i>correct</i>: measured, the origin
/// still sees three calls and no timeout is doubled. Telling the two apart means reading the pipeline name off
/// the handler, which is an internal field, or matching the platform's internal <c>HttpKey</c> in the service
/// collection. Both need reflection this package's trim and Native AOT declarations rule out, and every other
/// observable difference was measured to be nothing: the platform's own options registrations are
/// <c>TryAdd</c>-shaped, so a second <c>AddStandardResilienceHandler</c> adds no descriptor a consumer's
/// <c>AddResilienceHandler</c> does not.
/// </para>
/// <para>
/// So the state is ambiguous, and this package's rule for an ambiguous state is the one behind log event 11:
/// a signal that is frequently correct gets <b>Information</b>, not Warning, because a line that cries wolf
/// on the documented pattern is a line operators filter out. What it buys is that "does any client here have
/// two nested pipelines?" is answerable from logs, which it was not.
/// </para>
/// <para>
/// It runs from an <see cref="IHttpMessageHandlerBuilderFilter"/> because that is the one place the composed
/// chain is visible: additional handlers are collected from every registration, and this runs after all of
/// them. The cost is one pass over a handler list per client construction, not per request.
/// </para>
/// </remarks>
internal sealed class ResilienceHandlerCountFilter : IHttpMessageHandlerBuilderFilter
{
    // One line per client, not one per handler construction. IHttpClientFactory rebuilds a client's handler
    // chain every time the handler lifetime expires -- every two minutes by default -- and this filter runs
    // again each time, so without this the notice would repeat for the life of the process. Every other
    // notice in this package reports once for the same reason; they use an Interlocked flag because they are
    // registered per client, and this one is a single container-wide filter, so it needs a set.
    private readonly ConcurrentDictionary<string, byte> _reported = new(StringComparer.Ordinal);

    private readonly HttpResilienceRegistration _registration;
    private readonly ILogger? _logger;

    /// <param name="registration">The root registration, which holds each client's handler tally.</param>
    /// <param name="loggerFactory">
    /// Optional, with a default so the container can construct this type through
    /// <c>TryAddEnumerable</c> -- which needs an implementation <i>type</i> rather than a factory in order to
    /// deduplicate, and would otherwise refuse the registration. A container with no logging registered gets
    /// no notice, which is the same bargain every other notice in this package makes.
    /// </param>
    public ResilienceHandlerCountFilter(
        HttpResilienceRegistration registration,
        ILoggerFactory? loggerFactory = null)
    {
        _registration = registration;
        _logger = loggerFactory?.CreateLogger("HttpResilience");
    }

    public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next)
    {
        ArgumentNullException.ThrowIfNull(next);

        return builder =>
        {
            next(builder);

            if (_logger is null ||
                builder.Name is not { } clientName ||
                _registration.ExpectedResilienceHandlers(clientName) is not { } expected)
            {
                // Not a client this package gave a pipeline to. Includes every client with
                // Enabled: false, which may add the platform's handler itself.
                return;
            }

            int actual = 0;
            foreach (DelegatingHandler handler in builder.AdditionalHandlers)
            {
                if (IsPlatformResilienceHandler(handler))
                {
                    actual++;
                }
            }

            if (actual <= expected || !_reported.TryAdd(clientName, 0))
            {
                return;
            }

            HttpResilienceLogging.ExtraResilienceHandlers(_logger, clientName, actual, expected);
        };
    }

    /// <summary>
    /// Whether a handler is the platform's resilience handler.
    /// </summary>
    /// <remarks>
    /// By type identity rather than by a name lookup: <c>Assembly.GetType(string)</c> is exactly the kind of
    /// reflection the trim and Native AOT declarations on this package rule out, and it would warn under
    /// <c>EnableTrimAnalyzer</c>. <see cref="ResilienceHandlerContext"/> is a public type in the same
    /// assembly, so it anchors the comparison without a string lookup; <c>ResilienceHandler</c> itself is
    /// internal there. If the platform ever renames or moves it this returns <see langword="false"/>, which
    /// disables the guard rather than breaking every client -- the safe direction for a defence-in-depth
    /// check, and pinned by <c>ConsumerBoundaryTests</c> so the loss is visible.
    /// </remarks>
    private static bool IsPlatformResilienceHandler(DelegatingHandler handler)
    {
        Type type = handler.GetType();
        return string.Equals(type.Name, "ResilienceHandler", StringComparison.Ordinal) &&
            type.Assembly == typeof(ResilienceHandlerContext).Assembly;
    }
}
