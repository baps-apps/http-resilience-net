using System.Globalization;
using HttpResilience.NET.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HttpResilience.NET.Internal;

/// <summary>
/// States, once per client at startup, the traffic this client's circuit breaker needs before it can open.
/// </summary>
/// <remarks>
/// <see cref="CircuitBreakerOptions.MinimumThroughput"/> must be observed <b>in one replica</b> within
/// <see cref="CircuitBreakerOptions.SamplingDuration"/> before <see cref="CircuitBreakerOptions.FailureRatio"/>
/// is evaluated at all. The defaults -- 100 attempts over 30 seconds -- are the platform's, and they are right
/// for a busy client. For an internal API handling a few requests a second spread over several replicas they
/// mean the breaker can never open, and nothing says so: the client has a breaker in its configuration, a
/// breaker in its documentation, and no breaker in effect.
/// <para>
/// This is arithmetic over two configured values, so the package can do it and an operator usually has not.
/// Emitted at <see cref="LogLevel.Information"/> rather than <c>Warning</c> because, unlike events 6 and 10,
/// the configuration may be entirely correct -- a busy client meets this rate easily. It is a number to check
/// against known traffic, not a defect.
/// </para>
/// <para>
/// The rate is quoted in <b>attempts</b>, which is what the breaker counts. On the standard pipeline the
/// breaker sits inside the retry loop, so at the default <c>Retry:MaxRetries</c> of 2 one fully-failing
/// caller request contributes three observations; on the hedging pipeline there is no retry strategy and it
/// contributes one. The caller-request figure is quoted alongside it because that is the number a service's
/// own dashboards show. See <see cref="AttemptsPerRequest"/> for why the pipeline has to be known here.
/// </para>
/// </remarks>
internal sealed class CircuitBreakerReachNotice : IPostConfigureOptions<HttpResilienceOptions>
{
    private readonly string _optionsName;
    private readonly string _clientName;
    private readonly string _throughputPath;
    private readonly PipelineKind _kind;
    private readonly ILogger? _logger;
    private int _reported;

    /// <param name="optionsName">The named options this notice reports on.</param>
    /// <param name="clientName">The HTTP client's name, as it appears in the message.</param>
    /// <param name="scope">This client's configuration path, for the key the message names.</param>
    /// <param name="kind">
    /// Which pipeline this client runs, because it decides what turns one caller request into several
    /// breaker observations. See <see cref="AttemptsPerRequest"/>.
    /// </param>
    /// <param name="loggerFactory">Logging, if the application has any.</param>
    public CircuitBreakerReachNotice(
        string optionsName,
        string clientName,
        string scope,
        PipelineKind kind,
        ILoggerFactory? loggerFactory)
    {
        _optionsName = optionsName;
        _clientName = clientName;
        _throughputPath = $"{scope}:CircuitBreaker:MinimumThroughput";
        _kind = kind;
        _logger = loggerFactory?.CreateLogger("HttpResilience");
    }

    public void PostConfigure(string? name, HttpResilienceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (_logger is null || !string.Equals(name, _optionsName, StringComparison.Ordinal))
        {
            return;
        }

        // A client with no pipeline has no breaker to reason about, and a sampling window that is not
        // positive has already failed validation -- reporting on it would divide by zero on the way out.
        if (!options.Enabled || options.CircuitBreaker.SamplingDuration <= TimeSpan.Zero)
        {
            return;
        }

        if (Volatile.Read(ref _reported) != 0 || Interlocked.Exchange(ref _reported, 1) != 0)
        {
            return;
        }

        double attemptsPerSecond =
            options.CircuitBreaker.MinimumThroughput / options.CircuitBreaker.SamplingDuration.TotalSeconds;

        HttpResilienceLogging.CircuitBreakerReach(
            _logger,
            _clientName,
            Rate(attemptsPerSecond),
            Rate(attemptsPerSecond / AttemptsPerRequest(options)),
            options.CircuitBreaker.MinimumThroughput,
            options.CircuitBreaker.SamplingDuration.TotalSeconds,
            _throughputPath);
    }

    /// <summary>
    /// How many observations one fully-failing caller request contributes to <b>one</b> breaker.
    /// </summary>
    /// <remarks>
    /// Retries are what turn one caller request into several observations on the standard pipeline, so they
    /// only divide the figure while the retry strategy is actually running.
    /// <para>
    /// On the <b>hedging</b> pipeline the answer is 1, and reading <c>Retry</c> there was wrong in the
    /// direction that matters. The hedging pipeline has no retry strategy at all -- <c>Collect</c> skips
    /// <c>ValidateRetry</c> for it and <c>CollectInertConfiguration</c> refuses <c>Retry:*</c> in a hedged
    /// client's own section -- but the <i>root</i> section is still inherited, and its defaults are
    /// <c>Retry:Enabled: true</c> with <c>MaxRetries: 2</c>. So every hedged client divided by three and
    /// reported a caller-request rate a third of the truth, on the one message whose whole purpose is to
    /// hand an operator a number to check against known traffic. Under-reporting it is the harmful
    /// direction: it says the breaker engages on less traffic than it does, so a client that cannot open its
    /// breaker looks as though it can.
    /// </para>
    /// <para>
    /// Hedged attempts are not the retry case restated. They are dispatched to <i>different</i> endpoints,
    /// and the hedging pipeline keeps a breaker per endpoint, so a caller request contributes one observation
    /// to each breaker it reaches rather than <c>1 + MaxHedgedAttempts</c> to any single one -- and this
    /// figure is per breaker, because <c>MinimumThroughput</c> is.
    /// </para>
    /// </remarks>
    private int AttemptsPerRequest(HttpResilienceOptions options) =>
        _kind is PipelineKind.Hedging || !options.Retry.Enabled ? 1 : options.Retry.MaxRetries + 1;

    private static string Rate(double value) =>
        value.ToString(value < 10 ? "0.0" : "0", CultureInfo.InvariantCulture);
}
