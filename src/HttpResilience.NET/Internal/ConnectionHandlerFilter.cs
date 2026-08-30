using System.Collections.Concurrent;
using HttpResilience.NET.Options;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HttpResilience.NET.Internal;

/// <summary>
/// Applies <see cref="ConnectionOptions"/> to the client's final primary handler.
/// </summary>
/// <remarks>
/// A handler-builder filter rather than <c>ConfigurePrimaryHttpMessageHandler</c>, because that is last-wins:
/// a package that replaced the handler at registration time would lose it to any later registration -- a
/// client certificate, a proxy, a test stub -- and leave factory rotation disabled around a pool nothing gave
/// a lifetime to. Every filter runs after every registration, so ordering cannot defeat this.
/// </remarks>
internal sealed class ConnectionHandlerFilter : IHttpMessageHandlerBuilderFilter
{
    // One line per client, not one per handler construction. IHttpClientFactory rebuilds a client's handler
    // chain every time the handler lifetime expires, and this filter runs again each time. Same reason
    // ResilienceHandlerCountFilter keeps a set rather than an Interlocked flag: one filter, many clients.
    private readonly ConcurrentDictionary<string, byte> _reported = new(StringComparer.Ordinal);
    private readonly IOptionsMonitor<HttpResilienceOptions> _options;
    private readonly ILogger? _logger;

    public ConnectionHandlerFilter(
        IOptionsMonitor<HttpResilienceOptions> options,
        ILoggerFactory? loggerFactory = null)
    {
        _options = options;
        _logger = loggerFactory?.CreateLogger("HttpResilience");
    }

    public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next)
    {
        ArgumentNullException.ThrowIfNull(next);

        return builder =>
        {
            next(builder);

            // The registered options are the registration snapshot, so this is the same ConnectionOptions
            // instance the validator checked. A client this package never registered has Enabled false.
            ConnectionOptions connection = _options.Get(builder.Name).Connection;

            if (connection.Enabled)
            {
                SocketsHttpHandlerFactory.Install(builder, connection);
            }
            else if (connection.AllowAutoRedirect is false)
            {
                // The redirect bound holds without the rest of the connection tuning, so it is applied on its
                // own. Nothing else is touched and the handler is never replaced -- this is not the opt-in.
                if (!SocketsHttpHandlerFactory.TryDisableAutoRedirect(builder))
                {
                    ReportRedirectBoundNotApplied(builder);
                }
            }
        };
    }

    /// <summary>
    /// Says so when the bound could not be applied, rather than leaving the gap silent.
    /// </summary>
    /// <remarks>
    /// See <see cref="SocketsHttpHandlerFactory.TryDisableAutoRedirect"/> for why this is a Warning and not
    /// the exception the <c>Connection:Enabled</c> path throws.
    /// </remarks>
    private void ReportRedirectBoundNotApplied(HttpMessageHandlerBuilder builder)
    {
        if (_logger is null ||
            builder.Name is not { } clientName ||
            !_reported.TryAdd(clientName, 0))
        {
            return;
        }

        HttpResilienceLogging.RedirectBoundNotApplied(
            _logger, clientName, builder.PrimaryHandler?.GetType().Name ?? "(none)");
    }
}
