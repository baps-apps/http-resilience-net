using System.Globalization;
using HttpResilience.NET.Configuration;
using HttpResilience.NET.Options;
using Microsoft.Extensions.Options;

namespace HttpResilience.NET.Internal;

/// <summary>
/// Validates <see cref="HttpResilienceOptions"/> deterministically, for every options name.
/// </summary>
/// <remarks>
/// Validation is written out rather than driven by data annotations for three reasons: annotations cannot
/// express a <see cref="TimeSpan"/> range or a relationship between two properties, they are scoped to a
/// single options name so they silently skip every per-client registration, and filtering their output
/// requires matching on message text. Every rule here states the property path, the offending value, the
/// expected range or relationship, and why it exists.
/// </remarks>
internal enum PipelineKind
{
    /// <summary>
    /// The root section, which no client uses directly. Every value is range-checked, but rules about a
    /// specific pipeline's budget are left to the per-client validators that know which pipeline runs.
    /// </summary>
    Root,

    /// <summary>Timeouts, retry and a circuit breaker.</summary>
    Standard,

    /// <summary>Timeouts, hedged attempts and a per-endpoint circuit breaker. Retry options are unused.</summary>
    Hedging
}

internal sealed class HttpResilienceOptionsValidator : IValidateOptions<HttpResilienceOptions>
{
    /// <summary>
    /// The longest queue either limiter may be given.
    /// </summary>
    /// <remarks>
    /// A queue is a place requests wait outside the total timeout budget, holding their content buffers. Past
    /// a certain depth it stops being backpressure and becomes an unbounded memory sink with an SLO nobody
    /// can measure, so there is a number beyond which the answer is capacity, not queue.
    /// </remarks>
    internal const int MaxQueueLimit = 1000;

    /// <summary>
    /// Validates the root options only. Per-client options are validated by
    /// <see cref="NamedPipelineOptionsValidator"/>, which knows which pipeline that client actually uses.
    /// </summary>
    public ValidateOptionsResult Validate(string? name, HttpResilienceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!string.IsNullOrEmpty(name) && name != Microsoft.Extensions.Options.Options.DefaultName)
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
    /// <param name="scope">
    /// The configuration path these values came from, used verbatim in messages so an operator is pointed at
    /// the right section rather than at an internal options name.
    /// </param>
    /// <param name="kind">
    /// Which pipeline the client uses, so budget rules are checked against the strategies that actually run.
    /// Retry options are bound but unused on the hedging pipeline.
    /// </param>
    internal static IReadOnlyList<string> Collect(
        HttpResilienceOptions options,
        string scope,
        PipelineKind kind)
    {
        var failures = new List<string>();

        // Connection settings are applied even when the pipeline is off, so they are always validated.
        ValidateConnection(options, scope, failures);

        // Before the Enabled gate: a renamed key is a mistake in the file whether or not the pipeline reads
        // it, and an operator who left it behind needs telling either way.
        ValidateRenamedKeys(options, scope, failures);

        // Also before the gate, and for a stronger version of the same reason: every client inherits the
        // root, so a guard switched off there is switched off for clients whose own Enabled says nothing.
        if (kind is PipelineKind.Root)
        {
            ValidateUnsafeMethodGuardsAreNotFleetWide(options, scope, failures);
        }

        if (!options.Enabled)
        {
            return failures;
        }

        ValidateTimeouts(options, scope, failures);
        ValidateCircuitBreaker(options, scope, failures);

        // Retry options are bound on every path but only executed by the standard pipeline. The root section
        // is range-checked without the budget rule, because whether a retry schedule has to fit the total
        // budget depends on which pipeline the client registering it uses.
        if (kind is not PipelineKind.Hedging)
        {
            ValidateRetry(options, scope, failures);
        }

        if (kind is PipelineKind.Standard)
        {
            ValidateRetryBudgetFitsTotalTimeout(options, scope, failures);
        }
        else if (kind is PipelineKind.Hedging)
        {
            ValidateHedgingBudgetFitsTotalTimeout(options, scope, failures);
        }

        ValidateRateLimiter(options, scope, failures);
        ValidateConcurrencyLimiter(options, scope, failures);
        ValidateHedging(options, scope, failures);
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
        if (o.Timeout.Attempt <= TimeSpan.Zero)
        {
            failures.Add(Fail(scope, "Timeout.Attempt", o.Timeout.Attempt,
                "greater than zero",
                "an attempt needs a positive budget or no request can ever complete."));
        }

        if (o.Timeout.Total <= TimeSpan.Zero)
        {
            failures.Add(Fail(scope, "Timeout.Total", o.Timeout.Total,
                "greater than zero",
                "the total budget bounds the whole logical request."));
        }

        // Microsoft's standard handler requires a strictly greater total, and rejects equality at runtime.
        if (o.Timeout.Attempt > TimeSpan.Zero && o.Timeout.Total > TimeSpan.Zero && o.Timeout.Attempt >= o.Timeout.Total)
        {
            failures.Add(Fail(scope, "Timeout.Attempt", o.Timeout.Attempt,
                $"strictly less than Timeout.Total ({Format(o.Timeout.Total)})",
                "the attempt timeout must leave room for at least one full attempt inside the total budget."));
        }

        if (o.Timeout.Client is { } client && o.Timeout.Total > TimeSpan.Zero && client <= o.Timeout.Total)
        {
            failures.Add(Fail(scope, "Timeout.Client", client,
                $"strictly greater than Timeout.Total ({Format(o.Timeout.Total)})",
                "this is HttpClient.Timeout, the outer backstop that covers limiter queue wait and the " +
                "response body transfer on top of the total budget. At or below the total budget it " +
                "truncates the pipeline instead of backing it up, and does so with a bare " +
                "TaskCanceledException carrying none of the pipeline's context."));
        }
    }

    /// <summary>
    /// Keys that were renamed rather than aliased, refused by name so the rename is visible to an operator.
    /// </summary>
    /// <remarks>
    /// A renamed key that simply stops binding leaves a client on the default with nothing to say so, which
    /// is the failure mode the whole schema is built to avoid. Aliasing is worse for this particular key:
    /// <c>MaxAttempts</c> always counted retries, so anyone who read the old name literally has arithmetic
    /// that is off by one, and silently preserving their value preserves the mistake.
    /// </remarks>
    private static void ValidateRenamedKeys(HttpResilienceOptions o, string scope, List<string> failures)
    {
#pragma warning disable CS0618 // The tombstone exists to be read here and nowhere else.
        if (o.Retry.MaxAttempts is { } legacy)
        {
            failures.Add(Fail(scope, "Retry.MaxAttempts", legacy,
                $"the key Retry.MaxRetries instead, with the same value ({legacy})",
                "the key was renamed in 2.0. It always counted retries after the first attempt, not total " +
                "attempts, so this value sends " + (legacy + 1).ToString(CultureInfo.InvariantCulture) +
                " requests rather than " + legacy.ToString(CultureInfo.InvariantCulture) + ". It is refused " +
                "rather than aliased so that the arithmetic in your capacity plans and runbooks is re-read " +
                "rather than silently carried forward."));
        }
#pragma warning restore CS0618
    }

    /// <summary>
    /// Refuses either safe-method guard being switched off at the root section.
    /// </summary>
    /// <remarks>
    /// The package's guarantee is that repeating a mutating request takes an explicit, <b>per-client</b>
    /// opt-in. A root-level <c>DisableForUnsafeHttpMethods: false</c> is neither: it is one key, in one file,
    /// that decides every standard client in the process may deliver POST, PUT, PATCH, DELETE and every
    /// unrecognized method to its origin more than once -- and clients registered afterwards inherit it
    /// without stating anything.
    /// <para>
    /// Whether repeating a request is safe is a property of one endpoint's idempotency handling. There is no
    /// such thing as a fleet-wide answer to it, which is why there is no fleet-wide way to say yes.
    /// </para>
    /// <para>
    /// <c>Retry:RetryableMethods</c> stays inheritable from the root, but only in the <b>narrowing</b>
    /// direction, and the loop below enforces that. A root list of <c>["GET"]</c> restricts every client and
    /// is strictly safer than the default, which is the case the inheritance model exists for; a root list
    /// naming an <i>unsafe</i> method reaches every standard client in the process by exactly the route the
    /// two flags are refused for, so unsafe entries are refused here too and belong per client.
    /// </para>
    /// </remarks>
    private static void ValidateUnsafeMethodGuardsAreNotFleetWide(
        HttpResilienceOptions o,
        string scope,
        List<string> failures)
    {
        if (!o.Retry.DisableForUnsafeHttpMethods)
        {
            failures.Add(Fail(scope, "Retry.DisableForUnsafeHttpMethods", false,
                $"true at the root section, and false only under " +
                $"{HttpResilienceConfigurationKeys.RootSection}:{HttpResilienceConfigurationKeys.ClientsSection}:{{name}} " +
                "for the one client that needs it",
                "at the root this removes the guard for every standard client in the process at once, " +
                "including clients registered later that state nothing. Whether a mutating request may be " +
                "repeated is a property of one endpoint's idempotency handling, so it belongs in that " +
                "client's own section -- and Retry.RetryableMethods there says which methods rather than " +
                "'every method we do not recognize as safe'."));
        }

        if (!o.Hedging.DisableForUnsafeHttpMethods)
        {
            failures.Add(Fail(scope, "Hedging.DisableForUnsafeHttpMethods", false,
                $"true at the root section, and false only under " +
                $"{HttpResilienceConfigurationKeys.RootSection}:{HttpResilienceConfigurationKeys.ClientsSection}:{{name}} " +
                "for the one client that needs it",
                "at the root this removes the guard for every hedged client in the process at once. " +
                "Hedged attempts are simultaneous, so unlike retries they give an origin's idempotency key " +
                "no serialization to rely on: this is the more dangerous of the two flags, not the less."));
        }

        // The list's root copy of the same rule. A root allow-list may narrow -- ["GET"] restricts every
        // client to retrying GETs -- but an unsafe entry here reaches every standard client in the process,
        // including clients registered later that state nothing, which is the decision the two flags above
        // are refused for. Registration checks this too, against the raw section, for the reason given on
        // RejectFleetWideUnsafeMethodGuards; this copy catches a Configure<HttpResilienceOptions> in code.
        // Malformed entries are left to ValidateRetry's token rule, which names them more usefully.
        foreach (string method in o.Retry.RetryableMethods ?? [])
        {
            if (!HttpMethodPredicates.IsValidMethodToken(method) || HttpMethodPredicates.IsSafe(method))
            {
                continue;
            }

            failures.Add(Fail(scope, "Retry.RetryableMethods", method,
                "only the safe methods GET, HEAD, OPTIONS and TRACE at the root section, and any other " +
                $"method under {HttpResilienceConfigurationKeys.RootSection}:{HttpResilienceConfigurationKeys.ClientsSection}:{{name}} " +
                "for the one client that needs it",
                "every client inherits the root, including clients registered later that state nothing, so " +
                "this one entry decides that the method may be delivered to an origin more than once across " +
                "the whole process. A root list may narrow what is retried; widening it is a property of one " +
                "endpoint's idempotency handling, not of a fleet."));
        }
    }

    private static void ValidateRetry(HttpResilienceOptions o, string scope, List<string> failures)
    {
        // 0 is deliberately rejected: the underlying strategy requires at least 1, so a 0 here used to pass
        // validation and then throw on the first request. Retry.Enabled is the supported off switch, and
        // once it is off nothing reads the count -- failing startup on it would reject a value that has no
        // effect, which is the opposite of what this validator is for.
        if (o.Retry.Enabled && o.Retry.MaxRetries is < 1 or > 10)
        {
            failures.Add(Fail(scope, "Retry.MaxRetries", o.Retry.MaxRetries,
                "between 1 and 10",
                "set Retry.Enabled to false to disable retries; 0 is not a valid attempt count."));
        }

        if (o.Retry.BaseDelay < TimeSpan.Zero || o.Retry.BaseDelay > TimeSpan.FromMinutes(1))
        {
            failures.Add(Fail(scope, "Retry.BaseDelay", o.Retry.BaseDelay,
                "between 00:00:00 and 00:01:00",
                "a base delay beyond a minute exceeds any sensible total request budget."));
        }

        if (!Enum.IsDefined(o.Retry.BackoffType))
        {
            failures.Add(Fail(scope, "Retry.BackoffType", o.Retry.BackoffType,
                "one of Constant, Linear, Exponential",
                "the value could not be mapped to a backoff strategy."));
        }

        // An allow-list wins outright in StandardPipelineConfigurator.ConfigureRetry, so the flag beside it
        // is bound and never read. That is the same class of inert configuration as an Authorities list
        // under Mode: None or Retry keys on a hedged client, both of which already fail startup -- and the
        // dangerous direction is identical: an author has written down a decision about duplicating mutating
        // requests, and it is not the decision in force.
        if (o.Retry.RetryableMethods is { Count: > 0 } && !o.Retry.DisableForUnsafeHttpMethods)
        {
            failures.Add(Fail(scope, "Retry.DisableForUnsafeHttpMethods", false,
                "true whenever Retry.RetryableMethods is set",
                "an explicit allow-list replaces this guard entirely -- only the methods named in " +
                "Retry.RetryableMethods are retried -- so this flag is bound and never read. Two " +
                "statements about repeating mutating requests, one of which is not in force. Keep the " +
                "list, which is the narrower and reviewable one, and remove this."));
        }

        // An empty list is deliberately accepted and means "no allow-list": ConfigureRetry falls through to
        // the safe-method guard, so GET, HEAD, OPTIONS and TRACE are still retried and nothing else is. That
        // is the documented way for a client to step out from under an inherited root list, and it is the
        // behavior a client section stating [] already had -- the binder leaves the property null once
        // ResetListsStatedBy has cleared it, so the rule that used to reject this could never fire from a
        // configuration file, only from the 'configure' delegate. It also described the wrong outcome: an
        // empty list does not disable retries. Removing it makes one behavior reachable by both routes.
        if (o.Retry.RetryableMethods is { } methods)
        {
            foreach (string method in methods)
            {
                // A syntactically valid method rather than a member of a known set: unrecognized methods are
                // no longer retried by default, so this list is the only way to opt one in and rejecting
                // PURGE or MOVE here would leave a real configuration with no expressible form.
                if (!HttpMethodPredicates.IsValidMethodToken(method))
                {
                    failures.Add(Fail(scope, "Retry.RetryableMethods", method,
                        "a valid HTTP method, for example GET, POST or PURGE",
                        "the entry is not a method token, so it could never match a request and would " +
                        "silently disable retries for whatever was meant."));
                }
            }
        }
    }

    private static void ValidateRetryBudgetFitsTotalTimeout(HttpResilienceOptions o, string scope, List<string> failures)
    {
        if (!o.Retry.Enabled || o.Timeout.Attempt <= TimeSpan.Zero || o.Timeout.Total <= TimeSpan.Zero ||
            o.Retry.MaxRetries is < 1 or > 10)
        {
            return;
        }

        TimeSpan worstCase = EstimateWorstCaseDuration(o);
        if (worstCase > o.Timeout.Total)
        {
            failures.Add(Fail(scope, "Timeout.Total", o.Timeout.Total,
                $"at least {Format(worstCase)}",
                $"{o.Retry.MaxRetries + 1} attempts of {Format(o.Timeout.Attempt)} plus " +
                $"{Format(worstCase - (o.Timeout.Attempt * (o.Retry.MaxRetries + 1)))} of {o.Retry.BackoffType} backoff " +
                (o.Retry.UseJitter ? $" (including {JitterHeadroom:0.0}x headroom for jitter)" : string.Empty) +
                " cannot fit in the total budget, so the configured retries would be cut short and never " +
                "run. This covers the configured schedule only: with Retry.UseRetryAfterHeader on, an " +
                "origin's Retry-After replaces the computed delay and can spend the whole budget by itself, " +
                "so a schedule that passes here can still be truncated by the server."));
        }
    }

    /// <summary>
    /// The headroom applied to nominal backoff when <see cref="RetryOptions.UseJitter"/> is on.
    /// </summary>
    /// <remarks>
    /// Polly's jitter is decorrelated rather than a symmetric spread, so an individual delay can land above
    /// the nominal figure this validator computes. Reserving headroom is the difference between telling an
    /// operator the schedule fits and telling them something that is true: without it, the last retry of an
    /// approved schedule can be cut off by the total timeout and never run.
    /// </remarks>
    internal const double JitterHeadroom = 1.5;

    /// <summary>
    /// Worst case: every attempt burns its full timeout and every backoff delay elapses, with headroom for
    /// jitter when it is enabled.
    /// </summary>
    internal static TimeSpan EstimateWorstCaseDuration(HttpResilienceOptions o)
    {
        int attempts = o.Retry.MaxRetries + 1;
        double baseDelayTicks = o.Retry.BaseDelay.Ticks;
        int retries = o.Retry.MaxRetries;

        double backoffTicks = o.Retry.BackoffType switch
        {
            RetryBackoffType.Constant => baseDelayTicks * retries,
            RetryBackoffType.Linear => baseDelayTicks * retries * (retries + 1) / 2.0,
            // 1x + 2x + 4x ... = (2^n - 1)x
            _ => baseDelayTicks * (Math.Pow(2, retries) - 1)
        };

        if (o.Retry.UseJitter)
        {
            backoffTicks *= JitterHeadroom;
        }

        double totalTicks = (o.Timeout.Attempt.Ticks * (double)attempts) + backoffTicks;
        return totalTicks >= long.MaxValue ? TimeSpan.MaxValue : TimeSpan.FromTicks((long)totalTicks);
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

        TimeSpan worstCase = (o.Hedging.Delay * o.Hedging.MaxHedgedAttempts) + o.Timeout.Attempt;
        if (worstCase > o.Timeout.Total)
        {
            failures.Add(Fail(scope, "Timeout.Total", o.Timeout.Total,
                $"at least {Format(worstCase)}",
                $"the last of {o.Hedging.MaxHedgedAttempts} hedged attempts starts after " +
                $"{Format(o.Hedging.Delay * o.Hedging.MaxHedgedAttempts)} and needs a further " +
                $"{Format(o.Timeout.Attempt)} to complete, which does not fit in the total budget."));
        }
    }

    private static void ValidateCircuitBreaker(HttpResilienceOptions o, string scope, List<string> failures)
    {
        if (o.CircuitBreaker.FailureRatio is <= 0 or > 1)
        {
            failures.Add(Fail(scope, "CircuitBreaker.FailureRatio", o.CircuitBreaker.FailureRatio,
                "greater than 0 and at most 1",
                "the value is a proportion of requests in the sampling window."));
        }

        if (o.CircuitBreaker.MinimumThroughput < 2)
        {
            failures.Add(Fail(scope, "CircuitBreaker.MinimumThroughput", o.CircuitBreaker.MinimumThroughput,
                "at least 2",
                "a failure ratio cannot be computed from fewer than two observations."));
        }

        if (o.CircuitBreaker.SamplingDuration < TimeSpan.FromMilliseconds(500))
        {
            failures.Add(Fail(scope, "CircuitBreaker.SamplingDuration", o.CircuitBreaker.SamplingDuration,
                "at least 00:00:00.500",
                "shorter windows cannot hold enough observations to be meaningful."));
        }

        if (o.CircuitBreaker.BreakDuration < TimeSpan.FromMilliseconds(500))
        {
            failures.Add(Fail(scope, "CircuitBreaker.BreakDuration", o.CircuitBreaker.BreakDuration,
                "at least 00:00:00.500",
                "a shorter break gives the dependency no time to recover before trial traffic resumes."));
        }

        // Enforced by the standard handler at runtime. Checking it here turns a first-request failure in
        // production into a startup failure with a message that says what to change.
        if (o.Timeout.Attempt > TimeSpan.Zero && o.CircuitBreaker.SamplingDuration < o.Timeout.Attempt * 2)
        {
            failures.Add(Fail(scope, "CircuitBreaker.SamplingDuration", o.CircuitBreaker.SamplingDuration,
                $"at least double Timeout.Attempt ({Format(o.Timeout.Attempt * 2)})",
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
            failures.Add(Fail(scope, "RateLimiter.Algorithm", o.RateLimiter.Algorithm,
                "one of FixedWindow, SlidingWindow, TokenBucket",
                "the value could not be mapped to a rate-limiting algorithm."));
            return;
        }

        if (o.RateLimiter.QueueLimit is < 0 or > MaxQueueLimit)
        {
            failures.Add(Fail(scope, "RateLimiter.QueueLimit", o.RateLimiter.QueueLimit,
                $"between 0 and {MaxQueueLimit}",
                "0 fails fast; every queued request holds its HttpRequestMessage and content buffer in " +
                "memory while it waits, so a deep queue is a memory risk as well as a latency one. A queue " +
                "this long means the downstream needs more capacity or the caller needs to shed load."));
        }

        if (o.RateLimiter.Algorithm is RateLimitAlgorithm.TokenBucket)
        {
            if (o.RateLimiter.TokenLimit is not > 0)
            {
                failures.Add(Fail(scope, "RateLimiter.TokenLimit", o.RateLimiter.TokenLimit,
                    "greater than 0 when RateLimiter.Algorithm is TokenBucket",
                    "bucket capacity is a capacity contract with the downstream and has no safe default."));
            }

            if (o.RateLimiter.TokensPerPeriod is not > 0)
            {
                failures.Add(Fail(scope, "RateLimiter.TokensPerPeriod", o.RateLimiter.TokensPerPeriod,
                    "greater than 0 when RateLimiter.Algorithm is TokenBucket",
                    "without a replenishment rate the bucket drains once and never refills."));
            }

            if (o.RateLimiter.ReplenishmentPeriod <= TimeSpan.Zero)
            {
                failures.Add(Fail(scope, "RateLimiter.ReplenishmentPeriod", o.RateLimiter.ReplenishmentPeriod,
                    "greater than zero",
                    "tokens are added once per period."));
            }
        }
        else
        {
            if (o.RateLimiter.PermitLimit is not > 0)
            {
                failures.Add(Fail(scope, "RateLimiter.PermitLimit", o.RateLimiter.PermitLimit,
                    "greater than 0 when RateLimiter.Enabled is true",
                    "the permitted rate is a contract with a specific downstream and has no safe default. " +
                    "Remember this limiter is process-local: the fleet-wide rate is replicas x PermitLimit."));
            }

            if (o.RateLimiter.Window <= TimeSpan.Zero)
            {
                failures.Add(Fail(scope, "RateLimiter.Window", o.RateLimiter.Window,
                    "greater than zero",
                    "permits are counted per window."));
            }

            if (o.RateLimiter.Algorithm is RateLimitAlgorithm.SlidingWindow && o.RateLimiter.SegmentsPerWindow < 1)
            {
                failures.Add(Fail(scope, "RateLimiter.SegmentsPerWindow", o.RateLimiter.SegmentsPerWindow,
                    "at least 1",
                    "a sliding window is divided into this many segments."));
            }
        }
    }

    private static void ValidateConcurrencyLimiter(HttpResilienceOptions o, string scope, List<string> failures)
    {
        // The backstop is applied whether or not the client's own cap is, so it is validated either way.
        if (o.ConcurrencyLimiter.Backstop < 1)
        {
            failures.Add(Fail(scope, "ConcurrencyLimiter.Backstop", o.ConcurrencyLimiter.Backstop,
                "at least 1",
                "the platform's resilience handler always carries a concurrency limiter, and this is its " +
                "permit count. There is no value that switches it off."));
        }

        if (!o.ConcurrencyLimiter.Enabled)
        {
            return;
        }

        if (o.ConcurrencyLimiter.Limit is { } limit && limit > o.ConcurrencyLimiter.Backstop &&
            o.ConcurrencyLimiter.Backstop >= 1)
        {
            failures.Add(Fail(scope, "ConcurrencyLimiter.Limit", limit,
                $"at most ConcurrencyLimiter.Backstop ({o.ConcurrencyLimiter.Backstop})",
                "a cap above the backstop is never reached: the excess is rejected by the platform's inner " +
                "limiter with RateLimiterRejectedException rather than queued by this one. Raise the " +
                "backstop as well if the client really needs this much concurrency."));
        }

        if (o.ConcurrencyLimiter.Limit is not > 0)
        {
            failures.Add(Fail(scope, "ConcurrencyLimiter.Limit", o.ConcurrencyLimiter.Limit,
                "greater than 0 when ConcurrencyLimiter.Enabled is true",
                "the cap reflects how much of this process's own capacity may wait on one dependency, " +
                "which no shared default can guess."));
        }

        if (o.ConcurrencyLimiter.QueueLimit is < 0 or > MaxQueueLimit)
        {
            failures.Add(Fail(scope, "ConcurrencyLimiter.QueueLimit", o.ConcurrencyLimiter.QueueLimit,
                $"between 0 and {MaxQueueLimit}",
                "0 fails fast; every queued request holds its HttpRequestMessage and content buffer in " +
                "memory while it waits for a slot, and the wait happens outside Timeout.Total."));
        }
    }

    private static void ValidateHedging(HttpResilienceOptions o, string scope, List<string> failures)
    {
        if (o.Hedging.MaxHedgedAttempts is < 1 or > 10)
        {
            failures.Add(Fail(scope, "Hedging.MaxHedgedAttempts", o.Hedging.MaxHedgedAttempts,
                "between 1 and 10",
                "each hedged attempt is a real request on the wire, so this directly multiplies outbound load."));
        }

        if (o.Hedging.Delay < TimeSpan.Zero)
        {
            failures.Add(Fail(scope, "Hedging.Delay", o.Hedging.Delay,
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
            failures.Add(Fail(scope, "PipelineSelection.Mode", o.PipelineSelection.Mode,
                "either None or ByAuthority",
                "the value could not be mapped to a selection mode."));
            return;
        }

        // The hedging handler keeps a circuit breaker, a concurrency limiter and a metric series per
        // authority whatever the selection mode, so a hedged client needs the allow-list even under
        // PipelineSelectionMode.None. The standard handler only partitions when asked to.
        bool required = o.PipelineSelection.Mode is PipelineSelectionMode.ByAuthority ||
            kind is PipelineKind.Hedging;

        // An authority list is this client's business only when its pipeline reads one. On a standard client
        // under Mode: None the list is inert -- and far more often inherited than stated, because a root list
        // is how a fleet expresses one allow-list for its hedged clients. Judging the *bound* value here made
        // that root list fail the registration of every standard client in the process, with a message naming
        // the standard client's own section rather than the root the list is actually in: two documented,
        // individually tested features that could not be used together.
        //
        // The inert case still worth reporting is a client that states a list itself, and telling that apart
        // from an inherited one needs the raw section rather than the bound value. That is
        // CollectInertConfiguration's job, where the same reasoning already keeps root Retry keys from
        // failing a hedged client and root Hedging keys from failing a standard one.
        if (kind is PipelineKind.Standard && o.PipelineSelection.Mode is PipelineSelectionMode.None)
        {
            return;
        }

        if (!required && o.PipelineSelection.Authorities is not { Count: > 0 })
        {
            return;
        }

        if (o.PipelineSelection.Authorities is not { Count: > 0 })
        {
            failures.Add(Fail(scope, "PipelineSelection.Authorities", "(empty)",
                kind is PipelineKind.Hedging
                    ? "a non-empty list for a client registered with AddHedgedHttpResilience"
                    : "a non-empty list when PipelineSelection.Mode is ByAuthority",
                "without an allow-list, every distinct authority a request reaches permanently allocates a new " +
                "pipeline, circuit breaker and metric series, which is a memory-exhaustion risk when target " +
                "hosts can be influenced by request data."));
            return;
        }

        foreach (string authority in o.PipelineSelection.Authorities)
        {
            if (!PipelineKeySelector.TryNormalizeAuthority(authority, out _))
            {
                failures.Add(Fail(scope, "PipelineSelection.Authorities", authority,
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
            failures.Add(Fail(scope, "Connection.MaxConnectionsPerServer", max,
                "at least 1, or null to keep the runtime default of unlimited",
                "a low cap throttles throughput below the resilience pipeline, where the queueing is invisible " +
                "to retry and timeout telemetry."));
        }

        if (o.Connection.ConnectTimeout <= TimeSpan.Zero)
        {
            failures.Add(Fail(scope, "Connection.ConnectTimeout", o.Connection.ConnectTimeout,
                "greater than zero",
                "a connect attempt needs a positive budget."));
        }

        if (o.Connection.PooledConnectionLifetime <= TimeSpan.Zero)
        {
            failures.Add(Fail(scope, "Connection.PooledConnectionLifetime", o.Connection.PooledConnectionLifetime,
                "greater than zero",
                "connection age is what bounds DNS staleness once factory handler rotation is disabled."));
        }

        if (o.Connection.PooledConnectionIdleTimeout <= TimeSpan.Zero)
        {
            failures.Add(Fail(scope, "Connection.PooledConnectionIdleTimeout", o.Connection.PooledConnectionIdleTimeout,
                "greater than zero",
                "idle connections are closed after this long."));
        }

        if (o.Connection.PooledConnectionIdleTimeout > TimeSpan.Zero &&
            o.Connection.PooledConnectionLifetime > TimeSpan.Zero &&
            o.Connection.PooledConnectionIdleTimeout >= o.Connection.PooledConnectionLifetime)
        {
            failures.Add(Fail(scope, "Connection.PooledConnectionIdleTimeout", o.Connection.PooledConnectionIdleTimeout,
                $"strictly less than Connection.PooledConnectionLifetime ({Format(o.Connection.PooledConnectionLifetime)})",
                "at or above the connection lifetime this setting can never fire -- the age bound retires the " +
                "connection before the idle bound is reached -- so an operator changing it would be changing " +
                "a number with no effect."));
        }

        if (o.Enabled && o.Connection.ConnectTimeout >= o.Timeout.Attempt && o.Timeout.Attempt > TimeSpan.Zero)
        {
            failures.Add(Fail(scope, "Connection.ConnectTimeout", o.Connection.ConnectTimeout,
                $"strictly less than Timeout.Attempt ({Format(o.Timeout.Attempt)})",
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
}
