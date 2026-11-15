using System.Collections.Concurrent;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HttpResilience.NET.Internal;

/// <summary>
/// Applies <see cref="HttpResilienceConnectionOptions"/> to the client's final primary handler.
/// </summary>
internal sealed class ConnectionHandlerFilter : IHttpMessageHandlerBuilderFilter
{
    private readonly ConcurrentDictionary<string, byte> _reported = new(StringComparer.Ordinal);
    private readonly HttpResilienceRegistration _registration;
    private readonly IOptionsMonitor<HttpResilienceOptions> _options;
    private readonly ILogger? _logger;

    public ConnectionHandlerFilter(
        HttpResilienceRegistration registration,
        IOptionsMonitor<HttpResilienceOptions> options,
        ILoggerFactory? loggerFactory = null)
    {
        _registration = registration;
        _options = options;
        _logger = loggerFactory?.CreateLogger(HttpResilienceLogging.ConfigurationCategory);
    }

    public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next)
    {
        ArgumentNullException.ThrowIfNull(next);

        return builder =>
        {
            next(builder);

            // The filter sees every client in the container. Only registered clients opted in: the unnamed
            // default client shares its name with the root options and would otherwise read them.
            if (builder.Name is not { } clientName || _registration.KindOf(clientName) is null)
            {
                return;
            }

            HttpResilienceConnectionOptions connection = _options.Get(clientName).Connection;

            if (connection.Enabled)
            {
                SocketsHttpHandlerFactory.Install(builder, connection);
            }
            else if (connection.AllowAutoRedirect is false)
            {
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
