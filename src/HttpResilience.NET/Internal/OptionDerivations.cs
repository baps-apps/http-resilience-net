namespace HttpResilience.NET.Internal;

/// <summary>
/// Values derived from the public option types. They live here, not on those types, so the public types carry
/// the configuration schema and nothing else (<c>PublicOptionTypes_CarryOnlyTheSchema</c>).
/// </summary>
internal static class OptionDerivations
{
    /// <summary>What <c>Timeout:Client</c> adds to <c>Timeout:Total</c> when it is not stated.</summary>
    public static readonly TimeSpan DefaultClientAllowance = TimeSpan.FromSeconds(30);

    /// <summary>The <c>HttpClient.Timeout</c> a client gets: <c>Timeout:Client</c>, or the total plus the allowance.</summary>
    public static TimeSpan EffectiveClientTimeout(this HttpResilienceTimeoutOptions timeout) =>
        timeout.Client ?? timeout.Total + DefaultClientAllowance;

    /// <summary>The key that sets the limiter's rate, for messages that tell an operator what to raise.</summary>
    public static string PermitKey(this HttpResilienceRateLimiterOptions rateLimiter) =>
        rateLimiter.Algorithm is HttpResilienceRateLimitAlgorithm.TokenBucket
            ? "RateLimiter:TokensPerPeriod"
            : "RateLimiter:PermitLimit";

    /// <summary>
    /// Whether redirects are followed: the stated value, else not on a client that enforces an authority
    /// allow-list (a redirect would leave it), else the runtime default.
    /// </summary>
    public static bool FollowsRedirects(this HttpResilienceConnectionOptions connection, bool enforcesAllowList) =>
        connection.AllowAutoRedirect ?? !enforcesAllowList;
}
