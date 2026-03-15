using HttpResilience.NET.Options;

namespace HttpResilience.NET.Internal;

/// <summary>
/// Creates <see cref="SocketsHttpHandler"/> instances from <see cref="HttpResilienceOptions"/>.
/// Internal-only helper; not part of the public API.
/// </summary>
/// <remarks>
/// Only connection-pool and timeout options from <see cref="HttpResilienceOptions.Connection"/> are applied.
/// Redirect behavior (e.g. <see cref="SocketsHttpHandler.AllowAutoRedirect"/>), credentials, and other
/// security-related settings are left at .NET defaults. Configure these on the handler after creation if required,
/// or use a custom primary handler in your application.
/// </remarks>
internal static class SocketsHttpHandlerFactory
{
    /// <summary>
    /// Creates a <see cref="SocketsHttpHandler"/> configured from the given options.
    /// Redirect and authentication settings are not set; .NET defaults apply.
    /// </summary>
    /// <param name="options">HTTP resilience options (connection pooling, timeouts).</param>
    /// <returns>A configured SocketsHttpHandler.</returns>
    public static SocketsHttpHandler Create(HttpResilienceOptions options)
    {
        var conn = options.Connection;
        return new SocketsHttpHandler
        {
            MaxConnectionsPerServer = conn.MaxConnectionsPerServer,
            ConnectTimeout = TimeSpan.FromSeconds(conn.ConnectTimeoutSeconds),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(conn.PooledConnectionIdleTimeoutSeconds),
            PooledConnectionLifetime = TimeSpan.FromSeconds(conn.PooledConnectionLifetimeSeconds)
        };
    }
}
