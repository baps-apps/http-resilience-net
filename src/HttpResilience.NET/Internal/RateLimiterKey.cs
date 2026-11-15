namespace HttpResilience.NET.Internal;

/// <summary>
/// The dependency-injection key for a client's rate limiter.
/// </summary>
/// <param name="ClientName">The client whose limiter this is.</param>
/// <param name="Kind">
/// Which of the client's limiters. A client may own a rate limiter and either a concurrency cap or the
/// displaced backstop.
/// </param>
internal readonly record struct RateLimiterKey(string ClientName, LimiterKind Kind = LimiterKind.Rate);
