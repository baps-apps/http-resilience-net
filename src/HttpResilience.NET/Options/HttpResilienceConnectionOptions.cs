namespace HttpResilience.NET;

/// <summary>
/// Connection-pool settings for the client's primary <see cref="System.Net.Http.SocketsHttpHandler"/>, plus
/// the redirect bound. Off by default.
/// </summary>
/// <remarks>
/// Applied independently of <see cref="HttpResilienceOptions.Enabled"/>.
/// <para>
/// When <see cref="Enabled"/> is <see langword="true"/>:
/// </para>
/// <list type="bullet">
/// <item><description>The factory's handler rotation is turned off; <see cref="PooledConnectionLifetime"/> bounds connection age instead. A finite <c>SetHandlerLifetime</c> on the client fails client creation.</description></item>
/// <item><description>A primary handler you supplied is kept and configured, so certificates, proxies and TLS callbacks survive. It must be a <see cref="System.Net.Http.SocketsHttpHandler"/>. An <see cref="System.Net.Http.HttpClientHandler"/> with nothing set is replaced by one; an <see cref="System.Net.Http.HttpClientHandler"/> carrying any setting (a client certificate, a certificate callback, a proxy, credentials) fails client creation, naming the settings, rather than losing them. Any other type fails client creation.</description></item>
/// <item><description>On your handler, <see cref="ConnectTimeout"/>, <see cref="PooledConnectionIdleTimeout"/>, <see cref="PooledConnectionLifetime"/> and <see cref="EnableMultipleHttp2Connections"/> are always overwritten; <see cref="MaxConnectionsPerServer"/> and <see cref="AllowAutoRedirect"/> only when configured.</description></item>
/// </list>
/// </remarks>
/// <example>
/// <code language="json">
/// {
///   "HttpResilience": {
///     "Connection": {
///       "Enabled": true,
///       "PooledConnectionLifetime": "00:02:00",
///       "PooledConnectionIdleTimeout": "00:01:00",
///       "ConnectTimeout": "00:00:03"
///     }
///   }
/// }
/// </code>
/// </example>
public sealed class HttpResilienceConnectionOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether this package configures the primary handler. Defaults to
    /// <see langword="false"/>, which keeps the <c>IHttpClientFactory</c> defaults.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the maximum simultaneous connections per origin. Defaults to <see langword="null"/>,
    /// which keeps the runtime default (unlimited).
    /// </summary>
    /// <remarks>
    /// A low value queues requests inside the connection pool, below the resilience pipeline, where the wait
    /// appears only as latency. Under HTTP/2 it limits connections, not streams.
    /// </remarks>
    public int? MaxConnectionsPerServer { get; set; }

    /// <summary>
    /// Gets or sets how long an idle pooled connection is kept. Must be less than
    /// <see cref="PooledConnectionLifetime"/>. Defaults to 1 minute.
    /// </summary>
    public TimeSpan PooledConnectionIdleTimeout { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Gets or sets the maximum age of a pooled connection. Defaults to 2 minutes.
    /// </summary>
    /// <remarks>
    /// Connections are re-established, and DNS re-resolved, at this age. Keep it short where endpoints move,
    /// such as Kubernetes.
    /// </remarks>
    public TimeSpan PooledConnectionLifetime { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Gets or sets the TCP and TLS connect timeout. Must be less than <see cref="HttpResilienceTimeoutOptions.Attempt"/>.
    /// Defaults to 3 seconds.
    /// </summary>
    /// <remarks>
    /// Raise it, together with <see cref="HttpResilienceTimeoutOptions.Attempt"/>, for a cross-region dependency.
    /// </remarks>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Gets or sets a value indicating whether more than one HTTP/2 connection may be opened per origin.
    /// Defaults to <see langword="true"/> (the runtime default is <see langword="false"/>).
    /// </summary>
    /// <remarks>
    /// One HTTP/2 connection is capped by the server's concurrent-stream limit, often 100.
    /// </remarks>
    public bool EnableMultipleHttp2Connections { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether 3xx responses are followed. Defaults to
    /// <see langword="null"/>: followed by standard clients, not followed by hedged clients.
    /// </summary>
    /// <remarks>
    /// A hedged client enforces an authority allow-list, and a followed redirect would bypass it. Redirects
    /// also re-send custom credential headers such as <c>X-Api-Key</c> to the new host; only
    /// <c>Authorization</c> is stripped.
    /// <para>
    /// A value of <see langword="false"/> is applied even when <see cref="Enabled"/> is off. Set
    /// <see langword="true"/> on a hedged client whose destination legitimately redirects.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code language="json">
    /// {
    ///   "HttpResilience": {
    ///     "Clients": { "Search": { "Connection": { "AllowAutoRedirect": true } } }
    ///   }
    /// }
    /// </code>
    /// </example>
    public bool? AllowAutoRedirect { get; set; }
}
