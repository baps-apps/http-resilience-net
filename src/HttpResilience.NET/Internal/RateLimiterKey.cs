namespace HttpResilience.NET.Internal;

/// <summary>
/// The dependency-injection key for a client's rate limiter.
/// </summary>
/// <remarks>
/// A type this package owns rather than the bare client name. Keyed service keys share one namespace per
/// service type, and <see cref="System.Threading.RateLimiting.RateLimiter"/> is a BCL type a consumer is
/// entirely likely to register under a domain name of its own -- keying an inbound limiter by policy name is
/// what ASP.NET Core's own rate limiting encourages, and "Orders" or "Search" is the obvious name on both
/// sides. <c>AddKeyedSingleton</c> is not <c>TryAdd</c>, so a collision on a bare string silently replaces
/// the limiter the resilience pipeline enforces -- in either direction, with no exception, no log and no
/// validation failure. The configured <c>PermitLimit</c> then becomes dead configuration that
/// <see cref="UnusedClientSectionValidator"/> cannot see, because the section is read.
/// <para>
/// The key is deliberately <b>not</b> public. A consumer that wants to read a client's limiter statistics
/// should read <c>http.resilience.limiter.available_permits</c>, which is the supported path and the one the
/// runbook alerts on.
/// </para>
/// </remarks>
/// <param name="ClientName">The client whose limiter this is.</param>
/// <param name="Kind">
/// Which of the client's limiters. Defaults to <see cref="LimiterKind.Rate"/>, so the rate limiter -- the
/// only one that existed when this key was introduced -- is still addressed by client name alone. A client
/// may own two: a rate limiter and either a concurrency cap or the backstop a rate limiter displaced.
/// </param>
internal readonly record struct RateLimiterKey(string ClientName, LimiterKind Kind = LimiterKind.Rate);
