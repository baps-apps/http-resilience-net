namespace HttpResilience.NET.Options;

/// <summary>
/// Connection-pool settings applied to the client's primary <see cref="System.Net.Http.SocketsHttpHandler"/>,
/// plus the redirect bound. Off by default.
/// </summary>
/// <remarks>
/// Independent of <see cref="HttpResilienceOptions.Enabled"/>: switching the resilience pipeline off during an
/// incident does not revert the client to default connection behavior.
/// <para>
/// Left at <see cref="Enabled"/> <see langword="false"/>, a client keeps what <c>IHttpClientFactory</c> gives
/// it, which on .NET 10 is a <see cref="System.Net.Http.SocketsHttpHandler"/> with a two-minute pooled
/// connection lifetime, rotated every two minutes. That is already sound. Switch this on when you need a
/// setting the factory does not express -- <see cref="ConnectTimeout"/>,
/// <see cref="MaxConnectionsPerServer"/>, <see cref="EnableMultipleHttp2Connections"/> -- or when you want the
/// pool's age to be a number your configuration states rather than one it inherits.
/// </para>
/// <para>
/// Switching it on also sets the factory's handler lifetime to infinite, because
/// <see cref="PooledConnectionLifetime"/> bounds connection age instead; leaving both on would cycle
/// connection pools twice as often. If your client supplies its own primary handler, it must be a
/// <see cref="System.Net.Http.SocketsHttpHandler"/> -- it is kept and configured, not replaced, so a client
/// certificate, proxy or TLS callback survives -- or startup fails with a message saying why.
/// </para>
/// <para>
/// <b>What "configured" overwrites on a handler you supplied</b>, precisely: <see cref="ConnectTimeout"/>,
/// <see cref="PooledConnectionIdleTimeout"/>, <see cref="PooledConnectionLifetime"/> and
/// <see cref="EnableMultipleHttp2Connections"/> unconditionally; <see cref="MaxConnectionsPerServer"/> only
/// when the schema states it; and <see cref="AllowAutoRedirect"/> only when the schema states it or resolves
/// it to <see langword="false"/>. Everything else on the handler is untouched. If you have tuned any of the
/// first four yourself, leave <see cref="Enabled"/> false rather than have this disagree with you silently.
/// </para>
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
public sealed class ConnectionOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether this library configures the primary handler at all. Defaults to
    /// <see langword="false"/>, which leaves .NET runtime defaults in place.
    /// </summary>
    /// <remarks>
    /// Cannot be switched on after the client is registered -- it also disables factory handler rotation, and
    /// that is settled while the service collection is built -- so a later change fails startup.
    /// </remarks>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of simultaneous connections per origin, or <see langword="null"/> (the
    /// default) to keep the runtime default of unlimited.
    /// </summary>
    /// <remarks>
    /// Leave it unset unless you have sized it for a specific dependency. A low value throttles throughput
    /// silently: requests queue inside the connection pool, <i>below</i> the resilience pipeline, so the wait
    /// shows up as latency with nothing in retry or timeout telemetry to explain it. Under HTTP/2 it counts
    /// connections rather than streams, so the same number means something quite different.
    /// </remarks>
    public int? MaxConnectionsPerServer { get; set; }

    /// <summary>
    /// Gets or sets how long an idle pooled connection is kept. Defaults to 1 minute. Must be strictly less
    /// than <see cref="PooledConnectionLifetime"/>.
    /// </summary>
    /// <remarks>
    /// At or above the lifetime this setting can never fire, because the age bound retires the connection
    /// first -- so startup rejects that rather than let an operator tune a number with no effect.
    /// </remarks>
    public TimeSpan PooledConnectionIdleTimeout { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Gets or sets the maximum age of a pooled connection, which is what bounds DNS staleness. Defaults to
    /// 2 minutes.
    /// </summary>
    /// <remarks>
    /// This is the setting that matters in Kubernetes and anywhere else endpoints move: a connection is closed
    /// and re-established at this age, which is when DNS is resolved again. Too long and the client keeps
    /// talking to a pod that has been replaced.
    /// </remarks>
    public TimeSpan PooledConnectionLifetime { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Gets or sets the TCP and TLS connect timeout. Must be strictly shorter than
    /// <see cref="TimeoutOptions.Attempt"/>, so a failed connect leaves room for a retry. Defaults to
    /// 3 seconds.
    /// </summary>
    /// <remarks>
    /// The budget covers the TLS handshake as well as the TCP connect. A value tuned for a warm same-zone path
    /// will lose the race on a cold cross-region one, or on a loaded node, and the loss is reported as a
    /// connect failure that a safe method then retries -- adding load at the moment least able to absorb it.
    /// Raise it together with <see cref="TimeoutOptions.Attempt"/> for a genuinely distant dependency.
    /// </remarks>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Gets or sets a value indicating whether more than one HTTP/2 connection may be opened to a single
    /// origin, so a high-throughput client is not capped by one connection's concurrent-stream limit.
    /// Defaults to <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// The runtime default is <see langword="false"/>. This package turns it on because a single HTTP/2
    /// connection caps concurrency at the server's <c>SETTINGS_MAX_CONCURRENT_STREAMS</c> -- often 100 -- and
    /// a busy client hitting that ceiling queues invisibly.
    /// </remarks>
    public bool EnableMultipleHttp2Connections { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether a 3xx response is followed automatically, or
    /// <see langword="null"/> (the default) to let the pipeline decide.
    /// </summary>
    /// <remarks>
    /// Unset, this is <see langword="true"/> -- the runtime default -- for a standard client, and
    /// <see langword="false"/> for a hedged one. The difference is whether the client has declared a closed
    /// set of destinations: <c>AddHedgedHttpResilience</c> requires an authority allow-list, and an allow-list
    /// a redirect can step around is not an allow-list.
    /// <para>
    /// <b>Why this matters beyond destination control.</b> The runtime strips the <c>Authorization</c> header
    /// on every redirect, so bearer tokens do not travel. It does <b>not</b> strip anything else: an
    /// <c>X-Api-Key</c>, <c>X-Functions-Key</c> or any other custom credential header is re-sent verbatim to
    /// the redirect target, including a cross-origin one. For internal service-to-service auth -- usually a
    /// custom header, not a bearer token -- a redirect off an allow-listed host is a credential-disclosure
    /// path. This is why OWASP's SSRF guidance says to disable redirects rather than validate the first URL
    /// and trust the rest.
    /// </para>
    /// <para>
    /// Set it to <see langword="true"/> on a hedged client whose destination genuinely redirects. When the
    /// effective value is <see langword="false"/> it is applied even with <see cref="Enabled"/> off, because a
    /// safety bound that an unrelated connection-pool switch can disable is not a bound.
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

    /// <summary>
    /// Whether <see cref="AllowAutoRedirect"/> was written down -- in configuration or in the <c>configure</c>
    /// delegate -- as opposed to resolved from the pipeline kind.
    /// </summary>
    /// <remarks>
    /// Recorded before the value is resolved, because resolving it destroys the difference, and the difference
    /// is what decides whether a consumer's own primary handler is overwritten. A client that supplied a
    /// <see cref="System.Net.Http.SocketsHttpHandler"/> with <c>AllowAutoRedirect = false</c> and switched
    /// <see cref="Enabled"/> on had that reversed to <see langword="true"/> without a word: the resolved value
    /// for a standard client is the runtime default, and nothing recorded that no person had asked for it.
    /// Redirect following is the one property in this schema that is a security control rather than a
    /// performance one -- the runtime re-sends custom credential headers across a redirect -- so it is the one
    /// property that is never written on someone else's handler unless this schema was asked to.
    /// <para>
    /// <b>Statedness is recorded at bind time, so it sees configuration and the <c>configure</c> delegate and
    /// nothing later.</b> A consumer's <c>PostConfigure</c> assigns the value, not the flag, so it cannot make
    /// an unstated setting stated. Measured, and both outcomes are the ones to want: post-configuring
    /// <see langword="false"/> already fails startup, because
    /// <see cref="HttpResilience.NET.Internal.StructuralDecisions"/> holds
    /// <see cref="AllowAutoRedirect"/> and a late change to it is refused with a message. Post-configuring
    /// <see langword="true"/> on a client that stated nothing is a no-op against a handler the consumer set to
    /// <see langword="false"/> themselves -- two contradictory statements by the same consumer, resolved in
    /// favour of the one on their own handler. Stating it in configuration or in <c>configure</c> is the
    /// supported way to say it, and that does reach the handler.
    /// </para>
    /// </remarks>
    internal bool AllowAutoRedirectStated { get; set; }

    /// <summary>
    /// The effective value for a pipeline that does or does not enforce a closed destination set.
    /// </summary>
    internal bool FollowsRedirects(bool enforcesAllowList) => AllowAutoRedirect ?? !enforcesAllowList;
}
