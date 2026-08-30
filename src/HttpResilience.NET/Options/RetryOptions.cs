namespace HttpResilience.NET.Options;

/// <summary>
/// Retry behavior for transient failures. Only the four HTTP methods RFC 9110 calls safe are retried unless
/// you say otherwise.
/// </summary>
/// <remarks>
/// <b>Retries multiply outbound traffic.</b> A client at 100 requests per second with the default
/// <see cref="MaxRetries"/> of 2 puts up to 300 per second on the wire while a dependency is failing, and
/// that multiplier applies independently in every replica -- 20 pods make it 6,000. Read
/// <c>docs/OPERATIONS.md</c> before raising it.
/// </remarks>
/// <example>
/// The defaults, stated explicitly:
/// <code language="json">
/// {
///   "HttpResilience": {
///     "Retry": {
///       "Enabled": true,
///       "MaxRetries": 2,
///       "BaseDelay": "00:00:00.500",
///       "BackoffType": "Exponential",
///       "UseJitter": true
///     }
///   }
/// }
/// </code>
/// </example>
public sealed class RetryOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether retries are attempted at all. Defaults to
    /// <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// Set this to <see langword="false"/> to keep timeouts and the circuit breaker while switching retries
    /// off -- the usual reason is a dependency that is already overloaded, where retrying makes the incident
    /// worse. This is the supported off switch; setting <see cref="MaxRetries"/> to 0 is rejected at
    /// startup.
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the number of retries <i>after</i> the first attempt, so the origin sees at most
    /// <c>1 + MaxRetries</c> requests. Must be between 1 and 10. Defaults to 2, i.e. three requests.
    /// </summary>
    /// <remarks>
    /// Startup validation rejects a schedule that cannot fit in <see cref="TimeoutOptions.Total"/>, so raising
    /// this usually means raising the total budget too. The message tells you the figure it needs.
    /// <para>
    /// Called <c>MaxAttempts</c> before 2.0. It always counted retries rather than attempts -- it is assigned
    /// to Polly's <c>MaxRetryAttempts</c> -- so the old name said three requests were two. It was renamed
    /// rather than aliased, because an alias would have silently preserved the arithmetic of everyone who
    /// read the old name literally and got it wrong.
    /// </para>
    /// </remarks>
    public int MaxRetries { get; set; } = 2;

    /// <summary>
    /// Removed in 2.0; use <see cref="MaxRetries"/>. Bound only so that a configuration file still carrying
    /// the old key fails startup instead of being ignored.
    /// </summary>
    /// <remarks>
    /// A renamed key that simply stops binding is the exact failure mode this package exists to prevent:
    /// the client runs on the default and nothing says so. This property is a tombstone -- reading it is
    /// never meaningful, and startup validation rejects any configuration that sets it.
    /// </remarks>
    [Obsolete("Renamed to MaxRetries: the value always counted retries, not attempts. Setting it fails startup.")]
    public int? MaxAttempts { get; set; }

    /// <summary>
    /// Gets or sets the base delay that <see cref="BackoffType"/> scales. Defaults to 500 milliseconds.
    /// </summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Gets or sets how the delay grows between attempts. Defaults to
    /// <see cref="RetryBackoffType.Exponential"/>.
    /// </summary>
    public RetryBackoffType BackoffType { get; set; } = RetryBackoffType.Exponential;

    /// <summary>
    /// Gets or sets a value indicating whether the delay is randomised. Defaults to <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// Keep this on. Without jitter every replica retries on the same schedule, so the retries arrive at the
    /// dependency as a synchronised wave at exactly the moment it is least able to absorb one.
    /// </remarks>
    public bool UseJitter { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether a <c>Retry-After</c> response header overrides the computed
    /// delay. Defaults to <see langword="true"/>. Parsing is the platform's.
    /// </summary>
    /// <remarks>
    /// There is no cap on the value an origin may name. The wait is still bounded --
    /// <see cref="TimeoutOptions.Total"/> wraps the retry loop, so a <c>Retry-After: 3600</c> surfaces as a
    /// timeout rather than an hour-long request -- but the request holds its concurrency slot or rate-limit
    /// permit meanwhile. Turn this off for an origin you do not trust to name a sane value.
    /// </remarks>
    public bool UseRetryAfterHeader { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether retries are restricted to the four methods RFC 9110 defines as
    /// safe -- GET, HEAD, OPTIONS and TRACE. Defaults to <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// <b>This is an allow-list, not a deny-list of the five familiar mutating verbs.</b> The name matches the
    /// platform's own setting; the behavior deliberately does not. The platform's
    /// <c>DisableForUnsafeHttpMethods</c> excludes POST, PATCH, PUT, DELETE and CONNECT, and therefore retries
    /// any method it has not heard of -- a WebDAV <c>MOVE</c> or <c>PROPPATCH</c>, a cache <c>PURGE</c>, any
    /// <c>new HttpMethod("...")</c> your code passes. Every one of those mutates. With this
    /// <see langword="true"/>, nothing outside the safe four is repeated, recognised or not.
    /// <para>
    /// Retrying a non-idempotent request delivers the same body to the origin more than once, which is how
    /// duplicate payments and duplicate writes happen. Do not switch this off wholesale -- name the methods
    /// in <see cref="RetryableMethods"/> instead, so the decision is per method and visible in review.
    /// </para>
    /// <para>
    /// Two rules bound how it can be switched off, and both fail startup rather than binding quietly. It
    /// cannot be <see langword="false"/> in the <b>root</b> section: every client inherits the root, so one
    /// key there would decide that every standard client in the process -- including clients registered
    /// afterwards that state nothing -- may repeat a mutating request, and whether that is safe is a property
    /// of one endpoint's idempotency handling. And it cannot be <i>stated at all</i> in a client section
    /// beside a populated <see cref="RetryableMethods"/>, which replaces it outright: two written statements
    /// about duplicating mutating requests, one of which is not in force.
    /// </para>
    /// <para>
    /// That second rule runs in both directions, and the direction that reads as harmless is the one that is
    /// not. <see langword="false"/> beside a list is refused by the options validator; <see langword="true"/>
    /// beside a list is refused at registration, and it is the worse of the two -- the statement being
    /// discarded is the protective one, written by whoever is closest to the endpoint, in the section they
    /// own. To narrow a client back to safe methods under an inherited list, give it an empty
    /// <see cref="RetryableMethods"/> rather than this flag.
    /// </para>
    /// <para>
    /// A client with this <see langword="false"/> logs one <c>Warning</c> at host start, naming the client
    /// and this key, for the same reason a client with the pipeline switched off does: the state is
    /// indistinguishable from a mistake until an origin has been billed twice.
    /// </para>
    /// </remarks>
    public bool DisableForUnsafeHttpMethods { get; set; } = true;

    /// <summary>
    /// Gets or sets an explicit list of retryable HTTP methods. When set, only these are retried and
    /// <see cref="DisableForUnsafeHttpMethods"/> is ignored. Defaults to <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// The supported way to retry a non-idempotent method, and the only way to retry one this package does not
    /// recognize as safe. Use it when the endpoint deduplicates on an idempotency key -- and make sure it
    /// really does.
    /// <para>
    /// <b>A retried request must carry replayable content.</b> A retry re-sends the same
    /// <see cref="System.Net.Http.HttpRequestMessage"/>, so a buffered body -- <c>StringContent</c>,
    /// <c>ByteArrayContent</c>, <c>JsonContent</c> -- replays correctly and a single-pass one does not.
    /// Measured against a real endpoint, a <c>StreamContent</c> over a non-seekable stream retried three times
    /// delivers the body once and then <b>an empty body twice</b>, with no exception thrown. Buffer it first
    /// with <c>await content.LoadIntoBufferAsync()</c>, or build fresh content per attempt and send the
    /// attempts yourself.
    /// </para>
    /// <para>
    /// A client section <b>replaces</b> this list rather than adding to the root's, so a client can narrow an
    /// inherited list. A client that states no list of its own inherits the root's. An <b>empty</b> list means
    /// "no allow-list": the client returns to the default safe-method guard, which is how it steps out from
    /// under an inherited list without naming the four safe methods itself. It does not disable retries --
    /// <see cref="Enabled"/> is the off switch.
    /// </para>
    /// <para>
    /// The <b>root</b> section may name only safe methods. A root list may narrow what every client retries,
    /// which is what one shared statement should be able to say; naming an unsafe method there decides that
    /// every standard client in the process, including clients registered afterwards that state nothing, may
    /// deliver a mutating body to its origin more than once -- the same fleet-wide decision
    /// <see cref="DisableForUnsafeHttpMethods"/> is refused for at the root. Unsafe entries belong under
    /// <c>HttpResilience:Clients:{name}</c> and are refused at the root.
    /// </para>
    /// </remarks>
    /// <example>
    /// Retrying POST for one client, because that endpoint honours an idempotency key:
    /// <code language="json">
    /// {
    ///   "HttpResilience": {
    ///     "Clients": {
    ///       "Payments": { "Retry": { "RetryableMethods": [ "GET", "POST" ] } }
    ///     }
    ///   }
    /// }
    /// </code>
    /// </example>
    public List<string>? RetryableMethods { get; set; }
}
