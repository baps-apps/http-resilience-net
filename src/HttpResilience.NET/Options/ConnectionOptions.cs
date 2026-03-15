using System.ComponentModel.DataAnnotations;

namespace HttpResilience.NET.Options;

/// <summary>
/// Connection and connection-pool options for the primary SocketsHttpHandler. Config key: "HttpResilienceOptions:Connection".
/// </summary>
public class ConnectionOptions
{
    /// <summary>
    /// Maximum number of concurrent TCP connections allowed to a single server (host:port).
    /// <para><b>Use case:</b> Increase for high-throughput clients; decrease to avoid overwhelming a single service. Default: 10.</para>
    /// </summary>
    [Range(1, 1000, ErrorMessage = "MaxConnectionsPerServer must be between 1 and 1000.")]
    public int MaxConnectionsPerServer { get; set; } = 10;

    /// <summary>
    /// How long (seconds) an idle connection can sit in the pool before being closed. Maps to SocketsHttpHandler.PooledConnectionIdleTimeout.
    /// <para><b>Use case:</b> Lower for bursty traffic; higher for steady traffic. Default: 120 (2 minutes).</para>
    /// </summary>
    [Range(1, 3600, ErrorMessage = "PooledConnectionIdleTimeoutSeconds must be between 1 and 3600.")]
    public int PooledConnectionIdleTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Maximum lifetime (seconds) of a connection in the pool before it is recycled. Maps to SocketsHttpHandler.PooledConnectionLifetime.
    /// <para><b>Use case:</b> Use to respect DNS TTL or load-balancer stickiness. Default: 600 (10 minutes).</para>
    /// </summary>
    [Range(1, 3600, ErrorMessage = "PooledConnectionLifetimeSeconds must be between 1 and 3600.")]
    public int PooledConnectionLifetimeSeconds { get; set; } = 600;

    /// <summary>
    /// Timeout (seconds) for establishing the TCP/TLS connection. Maps to SocketsHttpHandler.ConnectTimeout.
    /// <para><b>Use case:</b> Fail fast when the server is unreachable; 5–30 typical. Default: 21 (.NET default).</para>
    /// </summary>
    [Range(1, 120, ErrorMessage = "ConnectTimeoutSeconds must be between 1 and 120.")]
    public int ConnectTimeoutSeconds { get; set; } = 21;
}
