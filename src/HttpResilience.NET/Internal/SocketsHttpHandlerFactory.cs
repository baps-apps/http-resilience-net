using Microsoft.Extensions.Http;

namespace HttpResilience.NET.Internal;

/// <summary>
/// Applies <see cref="HttpResilienceConnectionOptions"/> to the primary handler a client actually ends up with.
/// </summary>
internal static class SocketsHttpHandlerFactory
{
    /// <summary>
    /// Applies the connection settings to the client's primary handler, keeping a handler the consumer
    /// supplied rather than discarding whatever it carried, and failing rather than overwriting a connection
    /// setting the consumer gave it.
    /// </summary>
    public static void Install(HttpMessageHandlerBuilder builder, HttpResilienceConnectionOptions connection)
    {
        switch (builder.PrimaryHandler)
        {
            case SocketsHttpHandler existing:
                RejectConflictingSettings(builder.Name, existing, connection);
                Apply(existing, connection);
                break;

            case HttpClientHandler client:
                ReplaceUnconfiguredClientHandler(builder, client, connection);
                break;

            default:
                throw Unconfigurable(builder.Name, builder.PrimaryHandler?.GetType().Name ?? "(none)");
        }
    }

    /// <summary>
    /// Fails client creation when a supplied <see cref="SocketsHttpHandler"/> sets a connection setting that the
    /// schema would overwrite with a different value.
    /// </summary>
    private static void RejectConflictingSettings(
        string? clientName,
        SocketsHttpHandler handler,
        HttpResilienceConnectionOptions connection)
    {
        if (ConflictingSettings(handler, connection) is { Count: > 0 } conflicts)
        {
            throw new InvalidOperationException(
                $"HTTP client '{clientName}' has Connection:Enabled set, and its SocketsHttpHandler sets " +
                $"{string.Join(", ", conflicts)}. Connection:Enabled would overwrite those values. Set them in " +
                "the client's Connection section and remove them from the handler, or set Connection:Enabled to " +
                "false for this client.");
        }
    }

    /// <summary>
    /// The connection settings on <paramref name="handler"/> that differ from both a new
    /// <see cref="SocketsHttpHandler"/> and the value the schema resolves.
    /// </summary>
    /// <remarks>
    /// A value left at the runtime default cannot be told from one never set, so it is not a conflict.
    /// <c>MaxConnectionsPerServer</c> is written only when stated, so it conflicts only then. The redirect
    /// bound is not compared: a stated or hedged value is a destination bound and wins.
    /// </remarks>
    internal static List<string> ConflictingSettings(SocketsHttpHandler handler, HttpResilienceConnectionOptions connection)
    {
        var conflicts = new List<string>();
        using var defaults = new SocketsHttpHandler();

        Check(handler.ConnectTimeout, defaults.ConnectTimeout, connection.ConnectTimeout,
            nameof(HttpResilienceConnectionOptions.ConnectTimeout));
        Check(handler.PooledConnectionIdleTimeout, defaults.PooledConnectionIdleTimeout,
            connection.PooledConnectionIdleTimeout, nameof(HttpResilienceConnectionOptions.PooledConnectionIdleTimeout));
        Check(handler.PooledConnectionLifetime, defaults.PooledConnectionLifetime,
            connection.PooledConnectionLifetime, nameof(HttpResilienceConnectionOptions.PooledConnectionLifetime));
        Check(handler.EnableMultipleHttp2Connections, defaults.EnableMultipleHttp2Connections,
            connection.EnableMultipleHttp2Connections, nameof(HttpResilienceConnectionOptions.EnableMultipleHttp2Connections));

        if (connection.MaxConnectionsPerServer is { } max)
        {
            Check(handler.MaxConnectionsPerServer, defaults.MaxConnectionsPerServer, max,
                nameof(HttpResilienceConnectionOptions.MaxConnectionsPerServer));
        }

        return conflicts;

        void Check<T>(T actual, T runtimeDefault, T configured, string key)
            where T : IEquatable<T>
        {
            if (!actual.Equals(runtimeDefault) && !actual.Equals(configured))
            {
                conflicts.Add(FormattableString.Invariant($"{key} to {actual} (Connection:{key} is {configured})"));
            }
        }
    }

    /// <summary>
    /// Replaces an <see cref="HttpClientHandler"/> with a configured <see cref="SocketsHttpHandler"/> only
    /// when it carries nothing the replacement would drop; otherwise fails client creation.
    /// </summary>
    /// <remarks>
    /// Its settings cannot be copied faithfully (its certificate callback receives the request, the
    /// <see cref="SocketsHttpHandler"/> one does not), and silently dropping a client certificate or a pinning
    /// callback is a security regression, so the only safe outcomes are an exact no-op or a refusal.
    /// </remarks>
    private static void ReplaceUnconfiguredClientHandler(
        HttpMessageHandlerBuilder builder,
        HttpClientHandler client,
        HttpResilienceConnectionOptions connection)
    {
        if (CustomizedSettings(client) is { Count: > 0 } customized)
        {
            throw new InvalidOperationException(
                $"HTTP client '{builder.Name}' has Connection:Enabled set, but its primary handler is " +
                $"{(client.GetType() == typeof(HttpClientHandler) ? "an HttpClientHandler" : $"a {client.GetType().Name}")} " +
                $"configured with {string.Join(", ", customized)}. Connection:Enabled replaces an " +
                "HttpClientHandler with a new SocketsHttpHandler, which would silently drop those settings " +
                "(a client certificate, a certificate-validation callback, a proxy or credentials among them). " +
                "Supply a SocketsHttpHandler carrying the same settings (SslOptions, Proxy, Credentials), " +
                "for example with ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { ... }) or " +
                "UseSocketsHttpHandler, or set Connection:Enabled to false for this client.");
        }

        client.Dispose();
        builder.PrimaryHandler = Create(connection);
    }

    /// <summary>
    /// The settings on <paramref name="client"/> that differ from a new <see cref="HttpClientHandler"/>.
    /// </summary>
    /// <remarks>
    /// An empty <see cref="HttpClientHandler.CookieContainer"/> shared with other code cannot be told apart
    /// from the default one, so only a container holding cookies or non-default limits is reported.
    /// </remarks>
    internal static List<string> CustomizedSettings(HttpClientHandler client)
    {
        var customized = new List<string>();
        using var defaults = new HttpClientHandler();

        if (client.GetType() != typeof(HttpClientHandler))
        {
            customized.Add($"a derived type ({client.GetType().Name}) whose overrides would be lost");
        }

        // ClientCertificates throws unless ClientCertificateOptions is Manual.
        if (client.ClientCertificateOptions != defaults.ClientCertificateOptions)
        {
            customized.Add(nameof(HttpClientHandler.ClientCertificateOptions));
        }
        else if (client.ClientCertificates.Count > 0)
        {
            customized.Add(nameof(HttpClientHandler.ClientCertificates));
        }

        Check(client.ServerCertificateCustomValidationCallback is not null,
            nameof(HttpClientHandler.ServerCertificateCustomValidationCallback));
        Check(client.SslProtocols != defaults.SslProtocols, nameof(HttpClientHandler.SslProtocols));
        Check(client.CheckCertificateRevocationList != defaults.CheckCertificateRevocationList,
            nameof(HttpClientHandler.CheckCertificateRevocationList));
        Check(client.Proxy is not null, nameof(HttpClientHandler.Proxy));
        Check(client.UseProxy != defaults.UseProxy, nameof(HttpClientHandler.UseProxy));
        Check(client.DefaultProxyCredentials is not null, nameof(HttpClientHandler.DefaultProxyCredentials));
        Check(client.Credentials is not null, nameof(HttpClientHandler.Credentials));
        Check(client.UseDefaultCredentials != defaults.UseDefaultCredentials,
            nameof(HttpClientHandler.UseDefaultCredentials));
        Check(client.PreAuthenticate != defaults.PreAuthenticate, nameof(HttpClientHandler.PreAuthenticate));
        Check(client.UseCookies != defaults.UseCookies, nameof(HttpClientHandler.UseCookies));
        Check(client.CookieContainer.Count > 0 ||
            client.CookieContainer.Capacity != defaults.CookieContainer.Capacity ||
            client.CookieContainer.PerDomainCapacity != defaults.CookieContainer.PerDomainCapacity ||
            client.CookieContainer.MaxCookieSize != defaults.CookieContainer.MaxCookieSize,
            nameof(HttpClientHandler.CookieContainer));
        Check(client.AutomaticDecompression != defaults.AutomaticDecompression,
            nameof(HttpClientHandler.AutomaticDecompression));
        Check(client.AllowAutoRedirect != defaults.AllowAutoRedirect, nameof(HttpClientHandler.AllowAutoRedirect));
        Check(client.MaxAutomaticRedirections != defaults.MaxAutomaticRedirections,
            nameof(HttpClientHandler.MaxAutomaticRedirections));
        Check(client.MaxConnectionsPerServer != defaults.MaxConnectionsPerServer,
            nameof(HttpClientHandler.MaxConnectionsPerServer));
        Check(client.MaxResponseHeadersLength != defaults.MaxResponseHeadersLength,
            nameof(HttpClientHandler.MaxResponseHeadersLength));
        Check(client.MeterFactory is not null, nameof(HttpClientHandler.MeterFactory));
        Check(client.Properties.Count > 0, nameof(HttpClientHandler.Properties));

        return customized;

        void Check(bool differs, string setting)
        {
            if (differs)
            {
                customized.Add(setting);
            }
        }
    }

    /// <summary>
    /// Stops the client following redirects, without touching anything else about the handler. Returns
    /// <see langword="false"/> when the primary handler has no redirect switch, so the caller can say so.
    /// </summary>
    public static bool TryDisableAutoRedirect(HttpMessageHandlerBuilder builder)
    {
        switch (builder.PrimaryHandler)
        {
            case SocketsHttpHandler sockets:
                sockets.AllowAutoRedirect = false;
                return true;

            case HttpClientHandler client:
                client.AllowAutoRedirect = false;
                return true;

            default:
                return false;
        }
    }

    public static SocketsHttpHandler Create(HttpResilienceConnectionOptions connection)
    {
        var handler = new SocketsHttpHandler();
        Apply(handler, connection);
        return handler;
    }

    private static void Apply(SocketsHttpHandler handler, HttpResilienceConnectionOptions connection)
    {
        handler.ConnectTimeout = connection.ConnectTimeout;
        handler.PooledConnectionIdleTimeout = connection.PooledConnectionIdleTimeout;
        handler.PooledConnectionLifetime = connection.PooledConnectionLifetime;
        handler.EnableMultipleHttp2Connections = connection.EnableMultipleHttp2Connections;

        if (connection.MaxConnectionsPerServer is { } max)
        {
            handler.MaxConnectionsPerServer = max;
        }

        ApplyRedirectBound(handler, connection);
    }

    /// <summary>
    /// Assigns <see cref="SocketsHttpHandler.AllowAutoRedirect"/> only when this schema has something to say
    /// about it.
    /// </summary>
    private static void ApplyRedirectBound(SocketsHttpHandler handler, HttpResilienceConnectionOptions connection)
    {
        bool follows = connection.FollowsRedirects(enforcesAllowList: false);

        if (OptionsComposition.IsRedirectStated(connection) || !follows)
        {
            handler.AllowAutoRedirect = follows;
        }
    }

    private static InvalidOperationException Unconfigurable(string? clientName, string handlerType) =>
        new($"HTTP client '{clientName}' has Connection:Enabled set, but its primary handler is a " +
            $"{handlerType} rather than a SocketsHttpHandler, so the connection settings cannot be applied " +
            "to it. This package disables IHttpClientFactory handler rotation when Connection:Enabled is " +
            "set, on the basis that PooledConnectionLifetime bounds connection age instead -- around a " +
            "handler that has no such setting, nothing would ever recycle the pool. Supply a " +
            "SocketsHttpHandler, or set Connection:Enabled to false for this client.");
}
