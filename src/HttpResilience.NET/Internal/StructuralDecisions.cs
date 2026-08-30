using HttpResilience.NET.Options;

namespace HttpResilience.NET.Internal;

/// <summary>
/// The options that decide <b>which handlers exist</b>, captured when the client was registered.
/// </summary>
/// <remarks>
/// Every other value the pipeline runs on is read from <c>IOptionsMonitor</c> when the pipeline is built,
/// which happens after every <c>Configure</c> and <c>PostConfigure</c> -- so a consumer changing a timeout or
/// a retry count changes what the pipeline runs, and what the options report is what is running because they
/// are the same object. Nothing has to be kept in step.
/// <para>
/// These few cannot work that way. <c>IHttpClientFactory</c> composes a client's handler chain from the
/// registrations made while the service collection was being built, so whether a concurrency limiter handler
/// exists, whether a keyed <c>RateLimiter</c> was registered, whether <c>SelectPipelineBy</c> was called and
/// whether factory handler rotation was disabled are all settled before any options are materialized. A later
/// change to one of them would be reported and not be in effect -- or worse: enabling the rate limiter would
/// have the pipeline resolve a keyed limiter that was never registered, and fail on the first request rather
/// than at startup.
/// </para>
/// <para>
/// So this is the residue of what used to be a full mirror of the options graph. It is deliberately a small,
/// explicit list rather than a generated one: a new option is a value option by default, which is the safe
/// default, and adding one here should be a decision someone makes on purpose.
/// </para>
/// </remarks>
internal readonly record struct StructuralDecisions(
    bool Enabled,
    bool RateLimiterEnabled,
    bool ConcurrencyLimiterEnabled,
    PipelineSelectionMode SelectionMode,
    bool ConnectionEnabled,
    bool AllowsAutoRedirect)
{
    public static StructuralDecisions From(HttpResilienceOptions options) => new(
        options.Enabled,
        options.RateLimiter.Enabled,
        options.ConcurrencyLimiter.Enabled,
        options.PipelineSelection.Mode,
        options.Connection.Enabled,
        options.Connection.AllowAutoRedirect is not false);

    /// <summary>
    /// Names each decision that differs, as the configuration paths an operator would edit.
    /// </summary>
    public IEnumerable<string> Differences(StructuralDecisions other)
    {
        if (Enabled != other.Enabled)
        {
            yield return $"Enabled ({other.Enabled} at registration, {Enabled} now)";
        }

        if (RateLimiterEnabled != other.RateLimiterEnabled)
        {
            yield return
                $"RateLimiter:Enabled ({other.RateLimiterEnabled} at registration, {RateLimiterEnabled} now)";
        }

        if (ConcurrencyLimiterEnabled != other.ConcurrencyLimiterEnabled)
        {
            yield return "ConcurrencyLimiter:Enabled " +
                $"({other.ConcurrencyLimiterEnabled} at registration, {ConcurrencyLimiterEnabled} now)";
        }

        if (SelectionMode != other.SelectionMode)
        {
            yield return
                $"PipelineSelection:Mode ({other.SelectionMode} at registration, {SelectionMode} now)";
        }

        if (ConnectionEnabled != other.ConnectionEnabled)
        {
            yield return
                $"Connection:Enabled ({other.ConnectionEnabled} at registration, {ConnectionEnabled} now)";
        }

        if (AllowsAutoRedirect != other.AllowsAutoRedirect)
        {
            yield return "Connection:AllowAutoRedirect " +
                $"({other.AllowsAutoRedirect} at registration, {AllowsAutoRedirect} now)";
        }
    }
}
