using HttpResilience.NET.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HttpResilience.NET.Tests.Options;

/// <summary>
/// Misconfiguration must fail at startup, for every client, with a message that says what to change.
/// </summary>
public class ValidationTests
{
    /// <summary>
    /// Runs what the host runs on startup, so these assertions prove the failure happens before traffic.
    /// </summary>
    private static async Task<string> AssertFailsAtStartupAsync(
        Settings settings,
        string? sectionName = null,
        bool hedged = false)
    {
        ServiceProvider? provider = null;
        Exception? captured = Record.Exception(() =>
        {
            // Registration validates eagerly, so most failures surface here rather than at StartupValidator.
            provider = ResilienceHarness.BuildProvider(settings, sectionName: sectionName, hedged: hedged);
            foreach (IStartupValidator validator in provider.GetServices<IStartupValidator>())
            {
                validator.Validate();
            }
        });

        if (provider is not null)
        {
            await provider.DisposeAsync();
        }

        Assert.NotNull(captured);
        Assert.True(
            captured is OptionsValidationException or AggregateException,
            $"Expected a startup validation failure but got {captured.GetType().Name}: {captured.Message}");

        return Describe(captured);
    }

    private static string Describe(Exception exception) => exception is AggregateException aggregate
        ? string.Join(Environment.NewLine, aggregate.Flatten().InnerExceptions.Select(Describe))
        : exception.Message;

    private static async Task AssertStartsAsync(Settings settings, string? sectionName = null)
    {
        await using ServiceProvider provider = ResilienceHarness.BuildProvider(settings, sectionName: sectionName);
        foreach (IStartupValidator validator in provider.GetServices<IStartupValidator>())
        {
            validator.Validate();
        }
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ValidConfiguration_Starts()
    {
        await AssertStartsAsync(Settings.Enabled());
    }

    /// <summary>
    /// A value outside what the platform accepts fails at startup, naming the key, rather than when the client
    /// is first built.
    /// </summary>
    /// <remarks>
    /// The limits are the platform's own: Polly's <c>[Range]</c> on the timeout and circuit breaker options,
    /// <c>HttpClient.Timeout</c> and <c>SocketsHttpHandler.ConnectTimeout</c>'s <c>int.MaxValue</c> milliseconds.
    /// Fails if a rule is removed, or if a message names a property path instead of the configuration key.
    /// </remarks>
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData("Timeout:Attempt", "00:00:00.005")]
    [InlineData("Timeout:Total", "1.00:00:01")]
    [InlineData("CircuitBreaker:SamplingDuration", "1.00:00:01")]
    [InlineData("CircuitBreaker:BreakDuration", "1.00:00:01")]
    [InlineData("Timeout:Client", "30.00:00:00")]
    [InlineData("Connection:ConnectTimeout", "30.00:00:00")]
    public async Task AValueOutsideThePlatformsRange_FailsAtStartup_NamingTheKey(string key, string value)
    {
        Settings settings = Settings.Enabled().Set(key, value);
        if (key.StartsWith("Connection:", StringComparison.Ordinal))
        {
            // Connection settings apply even to a disabled client, which skips the Attempt comparison.
            settings = settings.Set("Connection:Enabled", "true").Set("Enabled", "false");
        }

        string message = await AssertFailsAtStartupAsync(settings);

        Assert.Contains($"HttpResilience -- {key}: value '", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Each rejection rule fails startup, naming its key: an out-of-range value, an undefined enum value, or a
    /// required value missing for the configured algorithm.
    /// </summary>
    /// <remarks>
    /// One case per rule; every other value is valid, so the named key is the one that failed. Fails if a rule
    /// is removed or stops naming its key.
    /// </remarks>
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(false, "Retry:BackoffType", "7")]
    [InlineData(false, "CircuitBreaker:MinimumThroughput", "1")]
    [InlineData(false, "CircuitBreaker:SamplingDuration", "00:00:00.400")]
    [InlineData(false, "CircuitBreaker:BreakDuration", "00:00:00.400")]
    [InlineData(false, "PipelineSelection:Mode", "7")]
    [InlineData(false, "RateLimiter:Algorithm", "7", "RateLimiter:Enabled", "true", "RateLimiter:PermitLimit", "10")]
    [InlineData(false, "RateLimiter:Window", "00:00:00", "RateLimiter:Enabled", "true", "RateLimiter:PermitLimit", "10")]
    [InlineData(false, "RateLimiter:SegmentsPerWindow", "0", "RateLimiter:Enabled", "true", "RateLimiter:PermitLimit", "10",
        "RateLimiter:Algorithm", "SlidingWindow")]
    [InlineData(false, "RateLimiter:TokenLimit", "0", "RateLimiter:Enabled", "true", "RateLimiter:Algorithm", "TokenBucket",
        "RateLimiter:TokensPerPeriod", "5")]
    [InlineData(false, "RateLimiter:TokensPerPeriod", "0", "RateLimiter:Enabled", "true", "RateLimiter:Algorithm", "TokenBucket",
        "RateLimiter:TokenLimit", "10")]
    [InlineData(false, "RateLimiter:ReplenishmentPeriod", "00:00:00", "RateLimiter:Enabled", "true",
        "RateLimiter:Algorithm", "TokenBucket", "RateLimiter:TokenLimit", "10", "RateLimiter:TokensPerPeriod", "5")]
    [InlineData(false, "Connection:ConnectTimeout", "00:00:00", "Connection:Enabled", "true")]
    [InlineData(false, "Connection:PooledConnectionLifetime", "00:00:00", "Connection:Enabled", "true")]
    [InlineData(false, "Connection:PooledConnectionIdleTimeout", "00:00:00", "Connection:Enabled", "true")]
    [InlineData(true, "Hedging:Delay", "-00:00:01", "PipelineSelection:Authorities:0", "http://origin.test")]
    public async Task EachRejectionRule_FailsAtStartup_NamingItsKey(
        bool hedged,
        string key,
        string value,
        params string[] others)
    {
        Settings settings = Settings.Enabled().Set(key, value);
        for (int i = 0; i < others.Length; i += 2)
        {
            settings.Set(others[i], others[i + 1]);
        }

        string message = await AssertFailsAtStartupAsync(settings, hedged: hedged);

        // Root rules are reported under HttpResilience; Retry and Hedging only per client.
        Assert.Contains($" -- {key}: value '", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The worst-case retry schedule (events 14 and the hedging budget) sums each backoff shape correctly, with
    /// every delay capped at <c>Retry:MaxDelay</c>.
    /// </summary>
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(HttpResilienceRetryBackoffType.Constant, 5_000, 300)]
    [InlineData(HttpResilienceRetryBackoffType.Linear, 5_000, 600)]
    [InlineData(HttpResilienceRetryBackoffType.Exponential, 5_000, 700)]
    [InlineData(HttpResilienceRetryBackoffType.Exponential, 150, 400)]
    public void TheRetryScheduleEstimate_SumsEachBackoffShape(
        HttpResilienceRetryBackoffType backoff,
        int maxDelayMs,
        int expectedBackoffMs)
    {
        var options = new HttpResilienceOptions();
        options.Timeout.Attempt = TimeSpan.FromSeconds(1);
        options.Retry.MaxRetries = 3;
        options.Retry.BaseDelay = TimeSpan.FromMilliseconds(100);
        options.Retry.MaxDelay = TimeSpan.FromMilliseconds(maxDelayMs);
        options.Retry.BackoffType = backoff;

        (TimeSpan worstCase, TimeSpan total) =
            HttpResilience.NET.Internal.HttpResilienceOptionsValidator.EstimateRetrySchedule(options);

        Assert.Equal(TimeSpan.FromMilliseconds(expectedBackoffMs), total);
        Assert.Equal(TimeSpan.FromSeconds(4) + TimeSpan.FromMilliseconds(expectedBackoffMs), worstCase);
    }

    /// <summary>
    /// The platform's limits themselves are accepted: the startup rules are no stricter than the platform.
    /// </summary>
    /// <remarks>
    /// Fails if a bound is off by one in the strict direction. The request proves the platform accepts the same
    /// values when it builds the pipeline.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ThePlatformsLimits_AreAccepted_AtStartupAndByThePipeline()
    {
        Settings settings = Settings.Enabled()
            .Set("Timeout:Attempt", "00:00:00.010")
            .Set("Timeout:Total", "1.00:00:00")
            .Set("CircuitBreaker:SamplingDuration", "1.00:00:00")
            .Set("CircuitBreaker:BreakDuration", "1.00:00:00");

        await AssertStartsAsync(settings);

        await using ResilienceHarness harness = ResilienceHarness.Create(
            settings, new RecordingHandler(System.Net.HttpStatusCode.OK));
        using HttpResponseMessage response = await harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task Disabled_SkipsPipelineValidation()
    {
        // A nonsensical retry count must not block startup when the pipeline is not used at all.
        await AssertStartsAsync(Settings.Empty().Set("Enabled", "false").Set("Retry:MaxRetries", "999"));
    }

    /// <summary>
    /// Zero would throw from the underlying strategy on the first request, so it is rejected at startup with
    /// a message naming the supported alternative.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task RetryMaxRetriesZero_FailsAtStartup_AndPointsAtRetryEnabled()
    {
        string message =
            await AssertFailsAtStartupAsync(Settings.Enabled().Set("Retry:MaxRetries", "0"));

        Assert.Contains("Retry:MaxRetries", message, StringComparison.Ordinal);
        Assert.Contains("Retry:Enabled", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A cap below the base delay would cut every wait to the cap, so <c>Retry:BaseDelay</c> would have no effect.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task RetryMaxDelayBelowBaseDelay_FailsAtStartup()
    {
        string message = await AssertFailsAtStartupAsync(Settings.Enabled()
            .Set("Retry:BaseDelay", "00:00:02")
            .Set("Retry:MaxDelay", "00:00:01"));

        Assert.Contains("Retry:MaxDelay", message, StringComparison.Ordinal);
        Assert.Contains("Retry:BaseDelay", message, StringComparison.Ordinal);
    }

    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData("00:00:00")]
    [InlineData("-00:00:01")]
    [InlineData("1.00:00:01")]
    public async Task RetryMaxDelayOutOfRange_FailsAtStartup(string value)
    {
        string message = await AssertFailsAtStartupAsync(Settings.Enabled()
            .Set("Retry:BaseDelay", "00:00:00")
            .Set("Retry:MaxDelay", value));

        Assert.Contains("Retry:MaxDelay", message, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task AttemptTimeoutEqualToTotal_FailsAtStartup()
    {
        // The underlying handler requires a strictly greater total and rejects equality at runtime.
        string message = await AssertFailsAtStartupAsync(
            Settings.Enabled().Set("Timeout:Attempt", "00:00:30").Set("Timeout:Total", "00:00:30"));

        Assert.Contains("strictly less than Timeout:Total", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The client budget is the outer bound; at or below the total budget it would truncate the pipeline.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ClientTimeoutNotAboveTotal_FailsAtStartup()
    {
        string message = await AssertFailsAtStartupAsync(
            Settings.Enabled().Set("Timeout:Total", "00:00:20").Set("Timeout:Client", "00:00:20"));

        Assert.Contains("strictly greater than Timeout:Total", message, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task SamplingDurationBelowTwiceAttemptTimeout_FailsAtStartup()
    {
        string message = await AssertFailsAtStartupAsync(
            Settings.Enabled()
                .Set("Timeout:Attempt", "00:00:10")
                .Set("Timeout:Total", "00:01:00")
                .Set("CircuitBreaker:SamplingDuration", "00:00:15"));

        Assert.Contains("CircuitBreaker:SamplingDuration", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The default retry schedule must fit the total budget.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task RateLimiterWithoutAPermitLimit_FailsAtStartup()
    {
        string message =
            await AssertFailsAtStartupAsync(Settings.Enabled().Set("RateLimiter:Enabled", "true"));

        Assert.Contains("RateLimiter:PermitLimit", message, StringComparison.Ordinal);
        Assert.Contains("process-local", message, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ConcurrencyLimiterWithoutALimit_FailsAtStartup()
    {
        string message =
            await AssertFailsAtStartupAsync(Settings.Enabled().Set("ConcurrencyLimiter:Enabled", "true"));

        Assert.Contains("ConcurrencyLimiter:Limit", message, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ByAuthorityWithoutAnAllowList_FailsAtStartup()
    {
        string message = await AssertFailsAtStartupAsync(
            Settings.Enabled().Set("PipelineSelection:Mode", "ByAuthority"));

        Assert.Contains("PipelineSelection:Authorities", message, StringComparison.Ordinal);
        Assert.Contains("memory-exhaustion", message, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task UnparseableAuthority_FailsAtStartup()
    {
        string message = await AssertFailsAtStartupAsync(
            Settings.Enabled()
                .Set("PipelineSelection:Mode", "ByAuthority")
                .Set("PipelineSelection:Authorities:0", "not a url"));

        Assert.Contains("PipelineSelection:Authorities", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An absurdly long duration is reported, never thrown. Rules that combine two durations run alongside
    /// the range rules that reject each one, so the combination must not overflow first.
    /// </summary>
    /// <remarks>
    /// Fails with <c>OverflowException</c> instead of a validation message if <c>Timeout:Attempt * 2</c> (the
    /// sampling-window rule) is computed unguarded.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ANearMaximumAttemptTimeout_IsReported_NotThrown()
    {
        string message = await AssertFailsAtStartupAsync(
            Settings.Enabled().Set("Timeout:Attempt", "10675199.00:00:00"));

        Assert.Contains("Timeout:Attempt: value '10675199.00:00:00' is invalid", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same for a hedged client's delay, which has no range of its own: the budget rule
    /// (<c>Delay * MaxHedgedAttempts + Timeout:Attempt</c> within <c>Timeout:Total</c>) reports it.
    /// </summary>
    /// <remarks>Fails with <c>OverflowException</c> if the budget is computed unguarded.</remarks>
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData("10675199.02:48:05", "1")]  // the addition overflows
    [InlineData("10675199.00:00:00", "10")] // the multiplication overflows
    public void ANearMaximumHedgingDelay_IsReported_NotThrown(string delay, string maxHedgedAttempts)
    {
        OptionsValidationException exception = StartupAssert.Fails(() => ResilienceHarness.BuildProvider(
            Settings.Hedged()
                .ForClient("test", "Hedging:Delay", delay)
                .ForClient("test", "Hedging:MaxHedgedAttempts", maxHedgedAttempts),
            hedged: true));

        string message = string.Join(" ", exception.Failures);
        Assert.Contains("Timeout:Total", message, StringComparison.Ordinal);
        Assert.Contains("hedged attempts", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The hedging pipeline has no retry strategy, so retry keys on a hedged client fail.
    /// </summary>
    /// <remarks>
    /// Worst case: <c>Retry:DisableForUnsafeHttpMethods: false</c> on a hedged client, an intention nothing
    /// honours. Root retry configuration must stay legal (tested below).
    /// </remarks>
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData("Retry:MaxRetries", "3")]
    [InlineData("Retry:Enabled", "false")]
    [InlineData("Retry:DisableForUnsafeHttpMethods", "false")]
    public void RetryConfigurationOnAHedgedClient_FailsAtStartup(string key, string value)
    {
        OptionsValidationException exception = StartupAssert.Fails(() => ResilienceHarness.BuildProvider(
                Settings.Hedged().ForClient("test", key, value),
                hedged: true));

        string message = string.Join(" ", exception.Failures);
        Assert.Contains("Retry", message, StringComparison.Ordinal);
        Assert.Contains("Hedging", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Root retry configuration is inherited by standard clients, so a hedged client sharing the root must not
    /// fail.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task RootRetryConfiguration_DoesNotFailAHedgedClient()
    {
        await using ServiceProvider provider = ResilienceHarness.BuildProvider(
            Settings.Hedged().Set("Retry:MaxRetries", "3"),
            hedged: true);

        foreach (IStartupValidator validator in provider.GetServices<IStartupValidator>())
        {
            validator.Validate();
        }
    }

    /// <summary>
    /// A non-standard method is accepted (this list is the only way to retry one); an entry that is not a
    /// method at all is rejected.
    /// </summary>
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData("GET POST")]
    [InlineData("https://orders.internal")]
    [InlineData(" ")]
    [InlineData("")]
    public async Task RetryableMethodThatIsNotAMethodToken_FailsAtStartup(string method)
    {
        string message = await AssertFailsAtStartupAsync(
            Settings.Enabled().Set("Retry:RetryableMethods:0", method));

        Assert.Contains("Retry:RetryableMethods", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Per-client sections are validated on the same terms as the root.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task PerClientSection_IsValidatedAtStartup()
    {
        string message = await AssertFailsAtStartupAsync(
            Settings.Enabled().ForClient("Orders", "Retry:MaxRetries", "0"),
            sectionName: "Orders");

        Assert.Contains("Retry:MaxRetries", message, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task PerClientSection_ErrorMessageNamesTheSectionPath()
    {
        string message = await AssertFailsAtStartupAsync(
            Settings.Enabled().ForClient("Orders", "RateLimiter:Enabled", "true"),
            sectionName: "Orders");

        Assert.Contains("HttpResilience:Clients:Orders", message, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ErrorMessages_StateTheValue_TheExpectation_AndTheReason()
    {
        string message =
            await AssertFailsAtStartupAsync(Settings.Enabled().Set("CircuitBreaker:FailureRatio", "5"));

        Assert.Contains("CircuitBreaker:FailureRatio", message, StringComparison.Ordinal);
        Assert.Contains("value '5'", message, StringComparison.Ordinal);
        Assert.Contains("Expected greater than 0 and at most 1", message, StringComparison.Ordinal);
        Assert.Contains("Reason:", message, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ConnectionSettings_AreValidatedEvenWhenThePipelineIsDisabled()
    {
        string message = await AssertFailsAtStartupAsync(
            Settings.Empty()
                .Set("Enabled", "false")
                .Set("Connection:Enabled", "true")
                .Set("Connection:MaxConnectionsPerServer", "0"));

        Assert.Contains("Connection:MaxConnectionsPerServer", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The standard handler always carries a concurrency limiter, so a client cap above it is unreachable.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ConcurrencyLimitAboveTheBackstop_FailsAtStartup()
    {
        string message = await AssertFailsAtStartupAsync(Settings.Enabled()
            .Set("ConcurrencyLimiter:Enabled", "true")
            .Set("ConcurrencyLimiter:Limit", "2000")
            .Set("ConcurrencyLimiter:Backstop", "1000"));

        Assert.Contains("ConcurrencyLimiter:Limit", message, StringComparison.Ordinal);
        Assert.Contains("Backstop", message, StringComparison.Ordinal);
        Assert.Contains("rejected", message, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task BackstopBelowOne_FailsAtStartup()
    {
        string message = await AssertFailsAtStartupAsync(
            Settings.Enabled().Set("ConcurrencyLimiter:Backstop", "0"));

        Assert.Contains("ConcurrencyLimiter:Backstop", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The backstop is the bound that always exists, so it cannot be raised until it bounds nothing.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task BackstopAboveTheCeiling_FailsAtStartup()
    {
        string message = await AssertFailsAtStartupAsync(
            Settings.Enabled().Set("ConcurrencyLimiter:Backstop", "10001"));

        Assert.Contains("ConcurrencyLimiter:Backstop", message, StringComparison.Ordinal);
        Assert.Contains("value '10001'", message, StringComparison.Ordinal);
        Assert.Contains("at most 10000", message, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task BackstopAtTheCeiling_IsAccepted() =>
        await AssertStartsAsync(Settings.Enabled().Set("ConcurrencyLimiter:Backstop", "10000"));

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task BackstopAboveTheCeiling_InAClientSection_NamesTheClient()
    {
        string message = await AssertFailsAtStartupAsync(
            Settings.Enabled().ForClient("Orders", "ConcurrencyLimiter:Backstop", "10001"),
            sectionName: "Orders");

        Assert.Contains("HttpResilience:Clients:Orders", message, StringComparison.Ordinal);
        Assert.Contains("ConcurrencyLimiter:Backstop", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Equal is not shorter: the attempt timeout would fire as the connect gives up, hiding connect failures.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ConnectTimeoutEqualToAttemptTimeout_FailsAtStartup()
    {
        string message = await AssertFailsAtStartupAsync(Settings.Enabled()
            .Set("Connection:Enabled", "true")
            .Set("Connection:ConnectTimeout", "00:00:10"));

        Assert.Contains("Connection:ConnectTimeout", message, StringComparison.Ordinal);
        Assert.Contains("strictly less than", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An idle timeout at or above the connection lifetime never fires.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task IdleTimeoutAtOrAboveConnectionLifetime_FailsAtStartup()
    {
        string message = await AssertFailsAtStartupAsync(Settings.Enabled()
            .Set("Connection:Enabled", "true")
            .Set("Connection:PooledConnectionLifetime", "00:02:00")
            .Set("Connection:PooledConnectionIdleTimeout", "00:02:00"));

        Assert.Contains("Connection:PooledConnectionIdleTimeout", message, StringComparison.Ordinal);
        Assert.Contains("PooledConnectionLifetime", message, StringComparison.Ordinal);
    }

    /// <summary>Nothing reads the attempt count while retries are switched off.</summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task RetryMaxRetriesZero_IsAcceptedWhenRetriesAreDisabled()
    {
        await AssertStartsAsync(Settings.Enabled()
            .Set("Retry:Enabled", "false")
            .Set("Retry:MaxRetries", "0"));
    }

    /// <summary>
    /// Nor the schedule: a disabled retry writes none of it onto the platform, so a value Polly would reject
    /// starts, and the client makes one attempt.
    /// </summary>
    /// <remarks>
    /// Fails if the delays are validated while retry is off (startup fails), or written onto the platform anyway
    /// (Polly's own range, at most one day, rejects them when the pipeline is built).
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ADisabledRetrysSchedule_IsNeitherValidatedNorApplied()
    {
        Settings settings = Settings.Enabled()
            .Set("Retry:Enabled", "false")
            .Set("Retry:BaseDelay", "2.00:00:00")
            .Set("Retry:MaxDelay", "2.00:00:00");

        await AssertStartsAsync(settings);

        await using ResilienceHarness harness = ResilienceHarness.Create(settings);
        using HttpResponseMessage response = await harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, harness.Origin.Count);
    }

    /// <summary>
    /// What retry may repeat is still checked while it is off: a misspelled method is fixed before retry is
    /// switched back on.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ADisabledRetrysMethodList_IsStillValidated()
    {
        string message = await AssertFailsAtStartupAsync(Settings.Enabled()
            .Set("Retry:Enabled", "false")
            .Set("Retry:RetryableMethods:0", "G ET"));

        Assert.Contains("Retry:RetryableMethods", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A root <c>Hedging</c> value is read only by hedged clients, so a service whose clients never hedge starts
    /// and sends, while a hedged client inheriting the same value fails naming it.
    /// </summary>
    /// <remarks>
    /// Fails if <c>Hedging</c> is validated for standard clients or for the root alone.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ARootHedgingValue_IsJudgedOnlyWhereAClientHedges()
    {
        Settings settings = Settings.Hedged().Set("Hedging:MaxHedgedAttempts", "50");

        await AssertStartsAsync(settings);
        await using (ResilienceHarness harness = ResilienceHarness.Create(settings, new RecordingHandler(System.Net.HttpStatusCode.OK)))
        {
            using HttpResponseMessage response = await harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        }

        OptionsValidationException failure = StartupAssert.Fails(
            () => ResilienceHarness.BuildProvider(settings, hedged: true));
        Assert.Contains("Hedging:MaxHedgedAttempts", string.Join(" ", failure.Failures), StringComparison.Ordinal);
    }

    /// <summary>The same for a root <c>Retry</c> value, which only standard clients read.</summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ARootRetryValue_IsJudgedOnlyWhereAClientRetries()
    {
        Settings settings = Settings.Hedged().Set("Retry:BaseDelay", "00:05:00");

        await using (ServiceProvider provider = ResilienceHarness.BuildProvider(settings, hedged: true))
        {
            foreach (IStartupValidator validator in provider.GetServices<IStartupValidator>())
            {
                validator.Validate();
            }

            using HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("test");
        }

        string message = await AssertFailsAtStartupAsync(settings);
        Assert.Contains("Retry:BaseDelay", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A client that <b>states</b> an authority list while its mode is None has written configuration nothing
    /// reads, and that fails at registration.
    /// </summary>
    /// <remarks>
    /// The rule reads the client's own section. A root list is inherited and allowed (see
    /// <c>ConfigurationInheritanceTests.ARootAuthorityList_DoesNotFailAStandardClient</c>), because it is how a
    /// fleet gives its hedged clients one allow-list.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public void AClientStatingAuthoritiesWithoutByAuthorityMode_FailsAtStartup()
    {
        OptionsValidationException failure = StartupAssert.Fails(() => ResilienceHarness.BuildProvider(Settings.Enabled()
                .ForClient("test", "PipelineSelection:Authorities:0", "https://a.internal")));

        string message = string.Join(" ", failure.Failures);

        Assert.Contains("PipelineSelection:Authorities", message, StringComparison.Ordinal);
        Assert.Contains("silently does not happen", message, StringComparison.Ordinal);

        // It also says an inherited list would be left alone.
        Assert.Contains("inherited from", message, StringComparison.Ordinal);
    }

    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData("RateLimiter:QueueLimit")]
    [InlineData("ConcurrencyLimiter:QueueLimit")]
    public async Task QueueLimitAboveTheCap_FailsAtStartup(string key)
    {
        string message = await AssertFailsAtStartupAsync(Settings.Enabled()
            .Set("RateLimiter:Enabled", "true")
            .Set("RateLimiter:PermitLimit", "10")
            .Set("ConcurrencyLimiter:Enabled", "true")
            .Set("ConcurrencyLimiter:Limit", "10")
            .Set(key, "100000"));

        Assert.Contains(key, message, StringComparison.Ordinal);
        Assert.Contains("memory", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Enum values are bound by name; the binder does not use System.Text.Json.
    /// </summary>
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData("Retry:BackoffType", "Linear")]
    [InlineData("RateLimiter:Algorithm", "SlidingWindow")]
    [InlineData("PipelineSelection:Mode", "ByAuthority")]
    public async Task EnumValues_BindByName(string key, string value)
    {
        Settings settings = Settings.Enabled().Set(key, value);
        if (key.StartsWith("RateLimiter", StringComparison.Ordinal))
        {
            settings.Set("RateLimiter:Enabled", "true").Set("RateLimiter:PermitLimit", "10");
        }

        if (key.StartsWith("PipelineSelection", StringComparison.Ordinal))
        {
            settings.Set("PipelineSelection:Authorities:0", "http://origin.test");
        }

        await AssertStartsAsync(settings);
    }

    /// <summary>
    /// The shipped defaults must satisfy the shipped rules.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task TheDefaults_PassTheirOwnValidation()
    {
        await AssertStartsAsync(Settings.Empty()
            .Set("Enabled", "true")
            .Set("Connection:Enabled", "true"));
    }

    /// <summary>The same, for a hedged client, whose budget rules differ.</summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task TheDefaults_PassTheirOwnValidation_OnTheHedgingPipeline()
    {
        await using ServiceProvider provider = ResilienceHarness.BuildProvider(
            Settings.Empty()
                .Set("Enabled", "true")
                .Set("Connection:Enabled", "true")
                .Set("PipelineSelection:Authorities:0", "http://origin.test"),
            hedged: true);

        foreach (IStartupValidator validator in provider.GetServices<IStartupValidator>())
        {
            validator.Validate();
        }
    }
}
