using System.Globalization;
using Microsoft.Extensions.Options;

namespace HttpResilience.NET.Internal;

/// <summary>
/// The pipeline a set of options is validated for.
/// </summary>
internal enum PipelineKind
{
    /// <summary>
    /// The root section. Values every pipeline reads are range-checked; <c>Retry</c> and <c>Hedging</c> are
    /// checked only for the clients whose pipeline reads them, so a value no registered client uses is inert,
    /// not fatal.
    /// </summary>
    Root,

    /// <summary>Timeouts, retry and a circuit breaker.</summary>
    Standard,

    /// <summary>Timeouts, hedged attempts and a per-endpoint circuit breaker. Retry options are unused.</summary>
    Hedging
}

/// <summary>
/// Validates <see cref="HttpResilienceOptions"/>. Every message names the configuration path, the value,
/// the expected value and the reason.
/// </summary>
internal sealed class HttpResilienceOptionsValidator : IValidateOptions<HttpResilienceOptions>
{
    /// <summary>
    /// The longest queue either limiter may be given.
    /// </summary>
    internal const int MaxQueueLimit = 1000;

    /// <summary>
    /// The largest <c>ConcurrencyLimiter:Backstop</c>: ten times the default, so the bound that always exists stays
    /// a bound.
    /// </summary>
    internal const int MaxBackstop = 10_000;

    /// <summary>Polly's <c>[Range]</c> on every timeout strategy: 10 milliseconds to 24 hours.</summary>
    internal static readonly TimeSpan MinStrategyTimeout = TimeSpan.FromMilliseconds(10);

    /// <summary>Polly's upper bound on timeouts, sampling windows and break durations.</summary>
    internal static readonly TimeSpan MaxStrategyDuration = TimeSpan.FromDays(1);

    /// <summary>
    /// The largest finite value <c>HttpClient.Timeout</c> and <c>SocketsHttpHandler.ConnectTimeout</c> accept.
    /// </summary>
    internal static readonly TimeSpan MaxHttpTimeout = TimeSpan.FromMilliseconds(int.MaxValue);

    /// <summary>
    /// Validates the root options only. Per-client options are validated by
    /// <see cref="NamedPipelineOptionsValidator"/>, which knows which pipeline that client actually uses.
    /// </summary>
    public ValidateOptionsResult Validate(string? name, HttpResilienceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!string.IsNullOrEmpty(name) && name != Options.DefaultName)
        {
            return ValidateOptionsResult.Skip;
        }

        IReadOnlyList<string> failures = Collect(options, HttpResilienceConfigurationKeys.RootSection, PipelineKind.Root);
        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }

    /// <summary>
    /// Collects every rule violation, labelled with the configuration path an operator would edit.
    /// </summary>
    /// <param name="options">The bound options.</param>
    /// <param name="scope">The configuration path the values came from, used in messages.</param>
    /// <param name="kind">The pipeline the client uses.</param>
    internal static IReadOnlyList<string> Collect(
        HttpResilienceOptions options,
        string scope,
        PipelineKind kind)
    {
        var failures = new List<string>();

        ValidateConnection(options, scope, failures);

        if (!options.Enabled)
        {
            return failures;
        }

        ValidateTimeouts(options, scope, failures);
        ValidateCircuitBreaker(options, scope, failures);

        // Each pipeline's own section is judged where that pipeline runs: a bad root Hedging value must not fail
        // a service whose clients never hedge, and the same for Retry on hedged clients.
        if (kind is PipelineKind.Standard)
        {
            ValidateRetry(options, scope, failures);
        }

        if (kind is PipelineKind.Hedging)
        {
            ValidateHedging(options, scope, failures);
            ValidateHedgingBudgetFitsTotalTimeout(options, scope, failures);
        }

        ValidateRateLimiter(options, scope, failures);
        ValidateConcurrencyLimiter(options, scope, failures);
        ValidatePipelineSelection(options, scope, kind, failures);

        return failures;
    }

    /// <summary>
    /// The configuration path for a client's effective options.
    /// </summary>
    internal static string ScopeFor(string? clientSectionName) =>
        string.IsNullOrEmpty(clientSectionName)
            ? HttpResilienceConfigurationKeys.RootSection
            : $"{HttpResilienceConfigurationKeys.RootSection}:{HttpResilienceConfigurationKeys.ClientsSection}:{clientSectionName}";

    private static void ValidateTimeouts(HttpResilienceOptions o, string scope, List<string> failures)
    {
        if (o.Timeout.Attempt < MinStrategyTimeout || o.Timeout.Attempt > MaxStrategyDuration)
        {
            failures.Add(Fail(scope, "Timeout:Attempt", o.Timeout.Attempt,
                $"between {Format(MinStrategyTimeout)} and {Format(MaxStrategyDuration)}",
                "the platform's timeout strategy rejects anything outside that range when it builds the " +
                "pipeline, which would otherwise surface at the first request instead of at startup."));
        }

        if (o.Timeout.Total < MinStrategyTimeout || o.Timeout.Total > MaxStrategyDuration)
        {
            failures.Add(Fail(scope, "Timeout:Total", o.Timeout.Total,
                $"between {Format(MinStrategyTimeout)} and {Format(MaxStrategyDuration)}",
                "the platform's timeout strategy rejects anything outside that range when it builds the " +
                "pipeline, which would otherwise surface at the first request instead of at startup."));
        }

        if (o.Timeout.Attempt > TimeSpan.Zero && o.Timeout.Total > TimeSpan.Zero && o.Timeout.Attempt >= o.Timeout.Total)
        {
            failures.Add(Fail(scope, "Timeout:Attempt", o.Timeout.Attempt,
                $"strictly less than Timeout:Total ({Format(o.Timeout.Total)})",
                "the attempt timeout must leave room for at least one full attempt inside the total budget."));
        }

        if (o.Timeout.Client is { } client && o.Timeout.Total > TimeSpan.Zero && client <= o.Timeout.Total)
        {
            failures.Add(Fail(scope, "Timeout:Client", client,
                $"strictly greater than Timeout:Total ({Format(o.Timeout.Total)})",
                "this is HttpClient.Timeout, the outer backstop that covers limiter queue wait and the " +
                "response body transfer on top of the total budget. At or below the total budget it " +
                "truncates the pipeline instead of backing it up, and does so with a bare " +
                "TaskCanceledException carrying none of the pipeline's context."));
        }

        if (o.Timeout.Client is { } ceiling && ceiling > MaxHttpTimeout)
        {
            failures.Add(Fail(scope, "Timeout:Client", ceiling,
                $"at most {Format(MaxHttpTimeout)}",
                "HttpClient.Timeout rejects a longer finite value, so client creation would throw."));
        }
    }

    private static void ValidateRetry(HttpResilienceOptions o, string scope, List<string> failures)
    {
        ValidateRetryableMethods(o, scope, failures);

        // Disabled retry writes none of the schedule onto the platform (StandardPipelineConfigurator), so its
        // values are inert until it is switched on, when they are checked like any other.
        if (!o.Retry.Enabled)
        {
            return;
        }

        if (o.Retry.MaxRetries is < 1 or > 10)
        {
            failures.Add(Fail(scope, "Retry:MaxRetries", o.Retry.MaxRetries,
                "between 1 and 10",
                "set Retry:Enabled to false to disable retries; 0 is not a valid attempt count."));
        }

        if (o.Retry.BaseDelay < TimeSpan.Zero || o.Retry.BaseDelay > TimeSpan.FromMinutes(1))
        {
            failures.Add(Fail(scope, "Retry:BaseDelay", o.Retry.BaseDelay,
                "between 00:00:00 and 00:01:00",
                "a base delay beyond a minute exceeds any sensible total request budget."));
        }

        if (o.Retry.MaxDelay <= TimeSpan.Zero || o.Retry.MaxDelay > MaxStrategyDuration)
        {
            failures.Add(Fail(scope, "Retry:MaxDelay", o.Retry.MaxDelay,
                $"greater than 00:00:00 and at most {Format(MaxStrategyDuration)}",
                "it is the longest wait before a retry; zero would retry with no backoff at all."));
        }
        else if (o.Retry.BaseDelay >= TimeSpan.Zero && o.Retry.MaxDelay < o.Retry.BaseDelay)
        {
            failures.Add(Fail(scope, "Retry:MaxDelay", o.Retry.MaxDelay,
                $"at least Retry:BaseDelay ({Format(o.Retry.BaseDelay)})",
                "every wait would be cut to the cap, so Retry:BaseDelay would have no effect."));
        }

        if (!Enum.IsDefined(o.Retry.BackoffType))
        {
            failures.Add(Fail(scope, "Retry:BackoffType", o.Retry.BackoffType,
                "one of Constant, Linear, Exponential",
                "the value could not be mapped to a backoff strategy."));
        }
    }

    /// <summary>
    /// Checked whether or not retry is on: these say which requests may be repeated, and a contradictory or
    /// misspelled statement about that is fixed before anyone switches retry back on, not after.
    /// </summary>
    private static void ValidateRetryableMethods(HttpResilienceOptions o, string scope, List<string> failures)
    {
        if (o.Retry.RetryableMethods is { Count: > 0 } && !o.Retry.DisableForUnsafeHttpMethods)
        {
            failures.Add(Fail(scope, "Retry:DisableForUnsafeHttpMethods", false,
                "true whenever Retry:RetryableMethods is set",
                "an explicit allow-list replaces this guard entirely -- only the methods named in " +
                "Retry:RetryableMethods are retried -- so this flag is bound and never read. Two " +
                "statements about repeating mutating requests, one of which is not in force. Keep the " +
                "list, which is the narrower and reviewable one, and remove this."));
        }

        foreach (string method in o.Retry.RetryableMethods)
        {
            if (!HttpMethodPredicates.IsValidMethodToken(method))
            {
                failures.Add(Fail(scope, "Retry:RetryableMethods", method,
                    "a valid HTTP method, for example GET, POST or PURGE",
                    "the entry is not a method token, so it could never match a request and would " +
                    "silently disable retries for whatever was meant."));
            }
        }
    }

    /// <summary>
    /// The nominal worst case for the retry schedule: every attempt uses its full timeout and every backoff
    /// delay elapses at its configured (unjittered) length, cut to <c>Retry:MaxDelay</c> as the pipeline does.
    /// </summary>
    internal static (TimeSpan WorstCase, TimeSpan Backoff) EstimateRetrySchedule(HttpResilienceOptions o)
    {
        int retries = o.Retry.MaxRetries;
        double baseDelayTicks = o.Retry.BaseDelay.Ticks;
        double maxDelayTicks = o.Retry.MaxDelay > TimeSpan.Zero ? o.Retry.MaxDelay.Ticks : double.PositiveInfinity;

        double backoffTicks = 0;
        for (int retry = 1; retry <= retries; retry++)
        {
            double delay = o.Retry.BackoffType switch
            {
                HttpResilienceRetryBackoffType.Constant => baseDelayTicks,
                HttpResilienceRetryBackoffType.Linear => baseDelayTicks * retry,
                _ => baseDelayTicks * Math.Pow(2, retry - 1)
            };

            backoffTicks += Math.Min(delay, maxDelayTicks);
        }

        double totalTicks = (o.Timeout.Attempt.Ticks * (double)(retries + 1)) + backoffTicks;
        return (ToTimeSpan(totalTicks), ToTimeSpan(backoffTicks));

        static TimeSpan ToTimeSpan(double ticks) =>
            ticks >= long.MaxValue ? TimeSpan.MaxValue : TimeSpan.FromTicks((long)ticks);
    }

    /// <summary>
    /// Hedged attempts start after a delay and each gets its own attempt timeout, so the last one must still
    /// be able to complete inside the total budget.
    /// </summary>
    private static void ValidateHedgingBudgetFitsTotalTimeout(HttpResilienceOptions o, string scope, List<string> failures)
    {
        if (o.Timeout.Attempt <= TimeSpan.Zero || o.Timeout.Total <= TimeSpan.Zero ||
            o.Hedging.MaxHedgedAttempts is < 1 or > 10 || o.Hedging.Delay < TimeSpan.Zero)
        {
            return;
        }

        // Hedging:Delay has no upper bound of its own, so this sum is what rejects an absurd one.
        TimeSpan lastHedgeStarts = SaturatingMultiply(o.Hedging.Delay, o.Hedging.MaxHedgedAttempts);
        TimeSpan worstCase = SaturatingAdd(lastHedgeStarts, o.Timeout.Attempt);
        if (worstCase > o.Timeout.Total)
        {
            failures.Add(Fail(scope, "Timeout:Total", o.Timeout.Total,
                $"at least {Format(worstCase)}",
                $"the last of {o.Hedging.MaxHedgedAttempts} hedged attempts starts after " +
                $"{Format(lastHedgeStarts)} and needs a further " +
                $"{Format(o.Timeout.Attempt)} to complete, which does not fit in the total budget."));
        }
    }

    private static void ValidateCircuitBreaker(HttpResilienceOptions o, string scope, List<string> failures)
    {
        if (o.CircuitBreaker.FailureRatio is <= 0 or > 1)
        {
            failures.Add(Fail(scope, "CircuitBreaker:FailureRatio", o.CircuitBreaker.FailureRatio,
                "greater than 0 and at most 1",
                "the value is a proportion of requests in the sampling window."));
        }

        if (o.CircuitBreaker.MinimumThroughput < 2)
        {
            failures.Add(Fail(scope, "CircuitBreaker:MinimumThroughput", o.CircuitBreaker.MinimumThroughput,
                "at least 2",
                "a failure ratio cannot be computed from fewer than two observations."));
        }

        if (o.CircuitBreaker.SamplingDuration < TimeSpan.FromMilliseconds(500))
        {
            failures.Add(Fail(scope, "CircuitBreaker:SamplingDuration", o.CircuitBreaker.SamplingDuration,
                "at least 00:00:00.500",
                "shorter windows cannot hold enough observations to be meaningful."));
        }

        if (o.CircuitBreaker.BreakDuration < TimeSpan.FromMilliseconds(500))
        {
            failures.Add(Fail(scope, "CircuitBreaker:BreakDuration", o.CircuitBreaker.BreakDuration,
                "at least 00:00:00.500",
                "a shorter break gives the dependency no time to recover before trial traffic resumes."));
        }

        if (o.CircuitBreaker.SamplingDuration > MaxStrategyDuration)
        {
            failures.Add(Fail(scope, "CircuitBreaker:SamplingDuration", o.CircuitBreaker.SamplingDuration,
                $"at most {Format(MaxStrategyDuration)}",
                "the platform's circuit breaker rejects a longer window when it builds the pipeline."));
        }

        if (o.CircuitBreaker.BreakDuration > MaxStrategyDuration)
        {
            failures.Add(Fail(scope, "CircuitBreaker:BreakDuration", o.CircuitBreaker.BreakDuration,
                $"at most {Format(MaxStrategyDuration)}",
                "the platform's circuit breaker rejects a longer break when it builds the pipeline."));
        }

        // An attempt timeout outside its range is reported by its own rule; this one would only repeat it.
        if (o.Timeout.Attempt > TimeSpan.Zero && o.Timeout.Attempt <= MaxStrategyDuration &&
            o.CircuitBreaker.SamplingDuration < o.Timeout.Attempt * 2)
        {
            failures.Add(Fail(scope, "CircuitBreaker:SamplingDuration", o.CircuitBreaker.SamplingDuration,
                $"at least double Timeout:Attempt ({Format(o.Timeout.Attempt * 2)})",
                "a sampling window shorter than two attempt timeouts cannot observe enough completed attempts " +
                "for the failure ratio to mean anything."));
        }
    }

    private static void ValidateRateLimiter(HttpResilienceOptions o, string scope, List<string> failures)
    {
        if (!o.RateLimiter.Enabled)
        {
            return;
        }

        if (!Enum.IsDefined(o.RateLimiter.Algorithm))
        {
            failures.Add(Fail(scope, "RateLimiter:Algorithm", o.RateLimiter.Algorithm,
                "one of FixedWindow, SlidingWindow, TokenBucket",
                "the value could not be mapped to a rate-limiting algorithm."));
            return;
        }

        if (o.RateLimiter.QueueLimit is < 0 or > MaxQueueLimit)
        {
            failures.Add(Fail(scope, "RateLimiter:QueueLimit", o.RateLimiter.QueueLimit,
                $"between 0 and {MaxQueueLimit}",
                "0 fails fast; every queued request holds its HttpRequestMessage and content buffer in " +
                "memory while it waits, so a deep queue is a memory risk as well as a latency one. A queue " +
                "this long means the downstream needs more capacity or the caller needs to shed load."));
        }

        if (o.RateLimiter.Algorithm is HttpResilienceRateLimitAlgorithm.TokenBucket)
        {
            if (o.RateLimiter.TokenLimit is not > 0)
            {
                failures.Add(Fail(scope, "RateLimiter:TokenLimit", o.RateLimiter.TokenLimit,
                    "greater than 0 when RateLimiter:Algorithm is TokenBucket",
                    "bucket capacity is a capacity contract with the downstream and has no safe default."));
            }

            if (o.RateLimiter.TokensPerPeriod is not > 0)
            {
                failures.Add(Fail(scope, "RateLimiter:TokensPerPeriod", o.RateLimiter.TokensPerPeriod,
                    "greater than 0 when RateLimiter:Algorithm is TokenBucket",
                    "without a replenishment rate the bucket drains once and never refills."));
            }

            if (o.RateLimiter.ReplenishmentPeriod <= TimeSpan.Zero)
            {
                failures.Add(Fail(scope, "RateLimiter:ReplenishmentPeriod", o.RateLimiter.ReplenishmentPeriod,
                    "greater than zero",
                    "tokens are added once per period."));
            }
        }
        else
        {
            if (o.RateLimiter.PermitLimit is not > 0)
            {
                failures.Add(Fail(scope, "RateLimiter:PermitLimit", o.RateLimiter.PermitLimit,
                    "greater than 0 when RateLimiter:Enabled is true",
                    "the permitted rate is a contract with a specific downstream and has no safe default. " +
                    "Remember this limiter is process-local: the fleet-wide rate is replicas x PermitLimit."));
            }

            if (o.RateLimiter.Window <= TimeSpan.Zero)
            {
                failures.Add(Fail(scope, "RateLimiter:Window", o.RateLimiter.Window,
                    "greater than zero",
                    "permits are counted per window."));
            }

            if (o.RateLimiter.Algorithm is HttpResilienceRateLimitAlgorithm.SlidingWindow && o.RateLimiter.SegmentsPerWindow < 1)
            {
                failures.Add(Fail(scope, "RateLimiter:SegmentsPerWindow", o.RateLimiter.SegmentsPerWindow,
                    "at least 1",
                    "a sliding window is divided into this many segments."));
            }
        }
    }

    private static void ValidateConcurrencyLimiter(HttpResilienceOptions o, string scope, List<string> failures)
    {
        if (o.ConcurrencyLimiter.Backstop < 1)
        {
            failures.Add(Fail(scope, "ConcurrencyLimiter:Backstop", o.ConcurrencyLimiter.Backstop,
                "at least 1",
                "the platform's resilience handler always carries a concurrency limiter, and this is its " +
                "permit count. There is no value that switches it off."));
        }
        else if (o.ConcurrencyLimiter.Backstop > MaxBackstop)
        {
            failures.Add(Fail(scope, "ConcurrencyLimiter:Backstop", o.ConcurrencyLimiter.Backstop,
                $"at most {MaxBackstop}",
                "the backstop is the concurrency bound that always exists, per pipeline and per process; above " +
                "this it no longer bounds anything a dependency can absorb. Under HTTP/1.1 each request in " +
                "flight holds its own connection. For more concurrency across destinations, split the client " +
                "per authority (PipelineSelection:Mode ByAuthority)."));
        }

        if (!o.ConcurrencyLimiter.Enabled)
        {
            return;
        }

        if (o.ConcurrencyLimiter.Limit is { } limit && limit > o.ConcurrencyLimiter.Backstop &&
            o.ConcurrencyLimiter.Backstop >= 1)
        {
            failures.Add(Fail(scope, "ConcurrencyLimiter:Limit", limit,
                $"at most ConcurrencyLimiter:Backstop ({o.ConcurrencyLimiter.Backstop})",
                "a cap above the backstop is never reached: the excess is rejected by the platform's inner " +
                "limiter with RateLimiterRejectedException rather than queued by this one. Raise the " +
                "backstop as well if the client really needs this much concurrency."));
        }

        if (o.ConcurrencyLimiter.Limit is not > 0)
        {
            failures.Add(Fail(scope, "ConcurrencyLimiter:Limit", o.ConcurrencyLimiter.Limit,
                "greater than 0 when ConcurrencyLimiter:Enabled is true",
                "the cap reflects how much of this process's own capacity may wait on one dependency, " +
                "which no shared default can guess."));
        }

        if (o.ConcurrencyLimiter.QueueLimit is < 0 or > MaxQueueLimit)
        {
            failures.Add(Fail(scope, "ConcurrencyLimiter:QueueLimit", o.ConcurrencyLimiter.QueueLimit,
                $"between 0 and {MaxQueueLimit}",
                "0 fails fast; every queued request holds its HttpRequestMessage and content buffer in " +
                "memory while it waits for a slot, and the wait happens outside Timeout:Total."));
        }
    }

    private static void ValidateHedging(HttpResilienceOptions o, string scope, List<string> failures)
    {
        if (o.Hedging.MaxHedgedAttempts is < 1 or > 10)
        {
            failures.Add(Fail(scope, "Hedging:MaxHedgedAttempts", o.Hedging.MaxHedgedAttempts,
                "between 1 and 10",
                "each hedged attempt is a real request on the wire, so this directly multiplies outbound load."));
        }

        if (o.Hedging.Delay < TimeSpan.Zero)
        {
            failures.Add(Fail(scope, "Hedging:Delay", o.Hedging.Delay,
                "zero or greater",
                "the delay is how long the primary attempt is given before a hedged one starts."));
        }
    }

    private static void ValidatePipelineSelection(
        HttpResilienceOptions o,
        string scope,
        PipelineKind kind,
        List<string> failures)
    {
        if (!Enum.IsDefined(o.PipelineSelection.Mode))
        {
            failures.Add(Fail(scope, "PipelineSelection:Mode", o.PipelineSelection.Mode,
                "either None or ByAuthority",
                "the value could not be mapped to a selection mode."));
            return;
        }

        bool required = o.PipelineSelection.Mode is HttpResiliencePipelineSelectionMode.ByAuthority ||
            kind is PipelineKind.Hedging;

        if (kind is PipelineKind.Standard && o.PipelineSelection.Mode is HttpResiliencePipelineSelectionMode.None)
        {
            return;
        }

        if (!required && o.PipelineSelection.Authorities is not { Count: > 0 })
        {
            return;
        }

        if (o.PipelineSelection.Authorities is not { Count: > 0 })
        {
            failures.Add(Fail(scope, "PipelineSelection:Authorities", "(empty)",
                kind is PipelineKind.Hedging
                    ? "a non-empty list for a client registered with AddHttpResilienceHedgingHandler"
                    : "a non-empty list when PipelineSelection:Mode is ByAuthority",
                "without an allow-list, every distinct authority a request reaches permanently allocates a new " +
                "pipeline, circuit breaker and metric series, which is a memory-exhaustion risk when target " +
                "hosts can be influenced by request data."));
            return;
        }

        foreach (string authority in o.PipelineSelection.Authorities)
        {
            if (!PipelineKeySelector.TryNormalizeAuthority(authority, out _))
            {
                failures.Add(Fail(scope, "PipelineSelection:Authorities", authority,
                    "an absolute authority such as https://orders.internal or https://billing.internal:8443",
                    "the entry could not be parsed, so it would never match a request and the host would " +
                    "silently fall back to the shared pipeline."));
            }
        }
    }

    private static void ValidateConnection(HttpResilienceOptions o, string scope, List<string> failures)
    {
        if (!o.Connection.Enabled)
        {
            return;
        }

        if (o.Connection.MaxConnectionsPerServer is { } max && max < 1)
        {
            failures.Add(Fail(scope, "Connection:MaxConnectionsPerServer", max,
                "at least 1, or null to keep the runtime default of unlimited",
                "a low cap throttles throughput below the resilience pipeline, where the queueing is invisible " +
                "to retry and timeout telemetry."));
        }

        if (o.Connection.ConnectTimeout <= TimeSpan.Zero)
        {
            failures.Add(Fail(scope, "Connection:ConnectTimeout", o.Connection.ConnectTimeout,
                "greater than zero",
                "a connect attempt needs a positive budget."));
        }
        else if (o.Connection.ConnectTimeout > MaxHttpTimeout)
        {
            failures.Add(Fail(scope, "Connection:ConnectTimeout", o.Connection.ConnectTimeout,
                $"at most {Format(MaxHttpTimeout)}",
                "SocketsHttpHandler rejects a longer finite value, so client creation would throw."));
        }

        if (o.Connection.PooledConnectionLifetime <= TimeSpan.Zero)
        {
            failures.Add(Fail(scope, "Connection:PooledConnectionLifetime", o.Connection.PooledConnectionLifetime,
                "greater than zero",
                "connection age is what bounds DNS staleness once factory handler rotation is disabled."));
        }

        if (o.Connection.PooledConnectionIdleTimeout <= TimeSpan.Zero)
        {
            failures.Add(Fail(scope, "Connection:PooledConnectionIdleTimeout", o.Connection.PooledConnectionIdleTimeout,
                "greater than zero",
                "idle connections are closed after this long."));
        }

        if (o.Connection.PooledConnectionIdleTimeout > TimeSpan.Zero &&
            o.Connection.PooledConnectionLifetime > TimeSpan.Zero &&
            o.Connection.PooledConnectionIdleTimeout >= o.Connection.PooledConnectionLifetime)
        {
            failures.Add(Fail(scope, "Connection:PooledConnectionIdleTimeout", o.Connection.PooledConnectionIdleTimeout,
                $"strictly less than Connection:PooledConnectionLifetime ({Format(o.Connection.PooledConnectionLifetime)})",
                "at or above the connection lifetime this setting can never fire -- the age bound retires the " +
                "connection before the idle bound is reached -- so an operator changing it would be changing " +
                "a number with no effect."));
        }

        if (o.Enabled && o.Connection.ConnectTimeout >= o.Timeout.Attempt && o.Timeout.Attempt > TimeSpan.Zero)
        {
            failures.Add(Fail(scope, "Connection:ConnectTimeout", o.Connection.ConnectTimeout,
                $"strictly less than Timeout:Attempt ({Format(o.Timeout.Attempt)})",
                "at or above the attempt budget the attempt timeout fires while the connection is still " +
                "being established, so a slow connect could never be reported as a connect failure -- it " +
                "would always surface as an attempt timeout instead."));
        }
    }

    private static string Fail(string scope, string property, object? value, string expected, string why) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{scope} -- {property}: value '{Describe(value)}' is invalid. Expected {expected}. Reason: {why}");

    private static string Describe(object? value) => value switch
    {
        null => "(not set)",
        TimeSpan ts => Format(ts),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "(not set)"
    };

    private static string Format(TimeSpan value) => value.ToString("c", CultureInfo.InvariantCulture);

    // Rules that combine durations see values no range rule has rejected yet, so they saturate at
    // TimeSpan.MaxValue instead of throwing OverflowException out of validation. Both take non-negative inputs.
    private static TimeSpan SaturatingMultiply(TimeSpan value, int factor) =>
        value.Ticks > long.MaxValue / factor ? TimeSpan.MaxValue : value * factor;

    private static TimeSpan SaturatingAdd(TimeSpan left, TimeSpan right) =>
        left.Ticks > long.MaxValue - right.Ticks ? TimeSpan.MaxValue : left + right;
}
