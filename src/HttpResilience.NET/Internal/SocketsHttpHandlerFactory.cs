using HttpResilience.NET.Options;
using Microsoft.Extensions.Http;

namespace HttpResilience.NET.Internal;

/// <summary>
/// Applies <see cref="ConnectionOptions"/> to the primary handler a client actually ends up with.
/// </summary>
/// <remarks>
/// <c>ConfigurePrimaryHttpMessageHandler</c> is last-wins across registrations; <c>SetHandlerLifetime</c> is
/// not. A client can therefore end up with factory rotation disabled -- because this package disabled it --
/// around a handler nothing has given a <see cref="SocketsHttpHandler.PooledConnectionLifetime"/> to. A
/// consumer-supplied handler is the case that matters: constructed directly, its lifetime is the runtime
/// default of infinite, and with factory rotation off neither mechanism recycles the pool, so connections and
/// their DNS resolutions live as long as the process. Behind a moving service IP that is an outage nothing
/// reports. (The handler the factory itself supplies on .NET 10 is a <see cref="SocketsHttpHandler"/> with a
/// two-minute lifetime, so it is not the dangerous case -- but which handler a client ends up with is exactly
/// what registration order decides, which is the point.)
/// <para>
/// So this type never simply replaces what it finds, and it does not run at registration time. Driven from
/// <see cref="ConnectionHandlerFilter"/> it sees the handler the client actually ends up with: it configures
/// one a consumer supplied where it can, and refuses where it cannot.
/// </para>
/// </remarks>
internal static class SocketsHttpHandlerFactory
{
    /// <summary>
    /// Applies the connection settings to the client's primary handler, keeping a handler the consumer
    /// supplied rather than discarding whatever it carried.
    /// </summary>
    public static void Install(HttpMessageHandlerBuilder builder, ConnectionOptions connection)
    {
        switch (builder.PrimaryHandler)
        {
            case SocketsHttpHandler existing:
                // A consumer's handler may carry a client certificate, a proxy or an SSL callback -- none of
                // which this schema can express, and all of which replacing it would drop without a word.
                // What Apply overwrites is the four properties the schema always states -- ConnectTimeout,
                // PooledConnectionIdleTimeout, PooledConnectionLifetime, EnableMultipleHttp2Connections --
                // plus MaxConnectionsPerServer and AllowAutoRedirect only when the schema has something to say
                // about them. A consumer that has tuned any of the first four on its own handler should leave
                // Connection:Enabled false rather than have this silently disagree with it.
                //
                // This is also the factory's own default handler on .NET 10, so this branch is the normal path
                // rather than the exception, which is exactly why the two conditional properties are
                // conditional: "the consumer supplied it" and "the factory supplied it" are indistinguishable
                // here, so the guard has to come from whether the schema was asked, not from whose handler it is.
                Apply(existing, connection);
                break;

            case HttpClientHandler:
                // Not the factory's default any more -- on .NET 10 that is already a SocketsHttpHandler, so
                // this branch is only reached when a consumer supplied an HttpClientHandler explicitly. It
                // has no pooled-connection lifetime to set, so it has to go.
                builder.PrimaryHandler = Create(connection);
                break;

            default:
                throw Unconfigurable(builder.Name, builder.PrimaryHandler?.GetType().Name ?? "(none)");
        }
    }

    /// <summary>
    /// Stops the client following redirects, without touching anything else about the handler. Returns
    /// <see langword="false"/> when the primary handler has no redirect switch, so the caller can say so.
    /// </summary>
    /// <remarks>
    /// Used when a client bounds its destinations but has not opted into connection tuning -- every hedged
    /// client, and any client that states <c>Connection:AllowAutoRedirect</c> false.
    /// </remarks>
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
                // There is nothing here to set the flag on, and this used to return in silence -- a safety
                // bound that could not be applied and said nothing, which is the state every other notice in
                // this package exists to refuse.
                //
                // Reported by the caller rather than thrown, and the difference from Install's throw is
                // deliberate. Install runs when Connection:Enabled is set, where a handler with no
                // PooledConnectionLifetime means nothing recycles the pool -- a real defect on any handler.
                // Here, the handler that actually reaches this branch is a test stub, which resolves no
                // redirects and therefore cannot breach the bound at all. Throwing would fail every stubbed
                // hedged client in every consumer's test suite over a hazard those clients do not have, and
                // the way out of the throw would be to state Connection:AllowAutoRedirect true -- teaching
                // people to switch a security bound off to make tests compile. The case that is a genuine
                // hazard is a handler wrapping a SocketsHttpHandler of its own, which does resolve redirects;
                // it is rare, indistinguishable from the stub here, and worth a Warning naming the client.
                return false;
        }
    }

    public static SocketsHttpHandler Create(ConnectionOptions connection)
    {
        var handler = new SocketsHttpHandler();
        Apply(handler, connection);
        return handler;
    }

    private static void Apply(SocketsHttpHandler handler, ConnectionOptions connection)
    {
        handler.ConnectTimeout = connection.ConnectTimeout;
        handler.PooledConnectionIdleTimeout = connection.PooledConnectionIdleTimeout;
        handler.PooledConnectionLifetime = connection.PooledConnectionLifetime;
        handler.EnableMultipleHttp2Connections = connection.EnableMultipleHttp2Connections;

        // Left at the runtime default (unlimited) unless an operator has sized it for a specific dependency.
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
    /// <remarks>
    /// The resolved value is <see langword="true"/> for a standard client that stated nothing, and that value
    /// came from the pipeline kind rather than from a person. Writing it overwrote the one property on a
    /// consumer's own handler that is a security control: a handler hardened with
    /// <c>AllowAutoRedirect = false</c> had redirects switched back on by nothing more than
    /// <c>Connection:Enabled</c>, while the troubleshooting guide told its owner their settings were
    /// preserved. The runtime strips <c>Authorization</c> across a redirect and re-sends every custom
    /// credential header verbatim, so that is a credential-disclosure path, not a preference.
    /// <para>
    /// <see langword="false"/> is written whether it was stated or resolved. That is the hedged client's
    /// destination bound, and a bound a consumer's handler can defeat by construction is not a bound.
    /// </para>
    /// </remarks>
    private static void ApplyRedirectBound(SocketsHttpHandler handler, ConnectionOptions connection)
    {
        bool follows = connection.FollowsRedirects(enforcesAllowList: false);

        if (connection.AllowAutoRedirectStated || !follows)
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
