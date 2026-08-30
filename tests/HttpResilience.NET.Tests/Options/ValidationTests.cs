using HttpResilience.NET.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HttpResilience.NET.Tests.Options;

/// <summary>
/// Misconfiguration must fail at startup, for every client, with a message that says what to change.
/// A rule that only fires on the first live request is a production incident, not validation.
/// </summary>
public class ValidationTests
{
    /// <summary>
    /// Runs exactly what the host runs on startup, so these assertions prove the failure happens before the
    /// application accepts traffic rather than on some later code path.
    /// </summary>
    private static async Task<string> AssertFailsAtStartupAsync(Settings settings, string? sectionName = null)
    {
        ServiceProvider? provider = null;
        Exception? captured = Record.Exception(() =>
        {
            // Registration validates eagerly, so most failures surface here rather than at StartupValidator.
            provider = ResilienceHarness.BuildProvider(settings, sectionName: sectionName);
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

    [Fact]
    public async Task ValidConfiguration_Starts()
    {
        await AssertStartsAsync(Settings.Enabled());
    }

    [Fact]
    public async Task Disabled_SkipsPipelineValidation()
    {
        // A nonsensical retry count must not block startup when the pipeline is not used at all.
        await AssertStartsAsync(Settings.Empty().Set("Enabled", "false").Set("Retry:MaxRetries", "999"));
    }

    /// <summary>
    /// Zero used to pass validation and then throw from the underlying strategy on the first request.
    /// It is now rejected at startup, and the message names the supported alternative.
    /// </summary>
    [Fact]
    public async Task RetryMaxRetriesZero_FailsAtStartup_AndPointsAtRetryEnabled()
    {
        string message =
            await AssertFailsAtStartupAsync(Settings.Enabled().Set("Retry:MaxRetries", "0"));

        Assert.Contains("Retry.MaxRetries", message, StringComparison.Ordinal);
        Assert.Contains("Retry.Enabled", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AttemptTimeoutEqualToTotal_FailsAtStartup()
    {
        // The underlying handler requires a strictly greater total and rejects equality at runtime.
        string message = await AssertFailsAtStartupAsync(
            Settings.Enabled().Set("Timeout:Attempt", "00:00:30").Set("Timeout:Total", "00:00:30"));

        Assert.Contains("strictly less than Timeout.Total", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The client budget is the outer backstop, so a value at or below the total budget would truncate the
    /// pipeline it exists to sit outside of -- and would do it with a bare TaskCanceledException.
    /// </summary>
    [Fact]
    public async Task ClientTimeoutNotAboveTotal_FailsAtStartup()
    {
        string message = await AssertFailsAtStartupAsync(
            Settings.Enabled().Set("Timeout:Total", "00:00:20").Set("Timeout:Client", "00:00:20"));

        Assert.Contains("strictly greater than Timeout.Total", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SamplingDurationBelowTwiceAttemptTimeout_FailsAtStartup()
    {
        string message = await AssertFailsAtStartupAsync(
            Settings.Enabled()
                .Set("Timeout:Attempt", "00:00:10")
                .Set("Timeout:Total", "00:01:00")
                .Set("CircuitBreaker:SamplingDuration", "00:00:15"));

        Assert.Contains("CircuitBreaker.SamplingDuration", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The default retry schedule must actually fit the total budget, or the configured retries are fiction:
    /// the total timeout cuts them short and the operator never learns why.
    /// </summary>
    [Fact]
    public async Task RetryScheduleThatCannotFitTheTotalBudget_FailsAtStartup()
    {
        string message = await AssertFailsAtStartupAsync(
            Settings.Enabled()
                .Set("Retry:MaxRetries", "3")
                .Set("Retry:BaseDelay", "00:00:02")
                .Set("Retry:BackoffType", "Exponential")
                .Set("Timeout:Attempt", "00:00:10")
                .Set("Timeout:Total", "00:00:30"));

        Assert.Contains("cannot fit in the total budget", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RateLimiterWithoutAPermitLimit_FailsAtStartup()
    {
        string message =
            await AssertFailsAtStartupAsync(Settings.Enabled().Set("RateLimiter:Enabled", "true"));

        Assert.Contains("RateLimiter.PermitLimit", message, StringComparison.Ordinal);
        Assert.Contains("process-local", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConcurrencyLimiterWithoutALimit_FailsAtStartup()
    {
        string message =
            await AssertFailsAtStartupAsync(Settings.Enabled().Set("ConcurrencyLimiter:Enabled", "true"));

        Assert.Contains("ConcurrencyLimiter.Limit", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ByAuthorityWithoutAnAllowList_FailsAtStartup()
    {
        string message = await AssertFailsAtStartupAsync(
            Settings.Enabled().Set("PipelineSelection:Mode", "ByAuthority"));

        Assert.Contains("PipelineSelection.Authorities", message, StringComparison.Ordinal);
        Assert.Contains("memory-exhaustion", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnparseableAuthority_FailsAtStartup()
    {
        string message = await AssertFailsAtStartupAsync(
            Settings.Enabled()
                .Set("PipelineSelection:Mode", "ByAuthority")
                .Set("PipelineSelection:Authorities:0", "not a url"));

        Assert.Contains("PipelineSelection.Authorities", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The hedging pipeline has no retry strategy, so retry keys on a hedged client do nothing. Saying so is
    /// the same rule already applied to an allow-list with the wrong selection mode.
    /// </summary>
    /// <remarks>
    /// The worst case is an author writing <c>Retry:DisableForUnsafeHttpMethods: false</c> on a hedged client:
    /// a recorded intention about duplicating mutations that no mechanism honours. Fails if the check is
    /// removed. Root-level retry configuration is inherited by every client and must stay legal, which the
    /// test below asserts.
    /// </remarks>
    [Theory]
    [InlineData("Retry:MaxRetries", "3")]
    [InlineData("Retry:Enabled", "false")]
    [InlineData("Retry:DisableForUnsafeHttpMethods", "false")]
    public void RetryConfigurationOnAHedgedClient_FailsAtRegistration(string key, string value)
    {
        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(
            () => ResilienceHarness.BuildProvider(
                Settings.Hedged().ForClient("test", key, value),
                hedged: true));

        string message = string.Join(" ", exception.Failures);
        Assert.Contains("Retry", message, StringComparison.Ordinal);
        Assert.Contains("Hedging", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Root retry configuration is inherited by standard clients, so a hedged client sharing the root must
    /// not be the thing that fails the application's startup.
    /// </summary>
    [Fact]
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
    /// A non-standard method is accepted now that this list is the only way to retry one; an entry that is
    /// not a method at all is still rejected, because it could never match a request.
    /// </summary>
    [Theory]
    [InlineData("GET POST")]
    [InlineData("https://orders.internal")]
    [InlineData(" ")]
    [InlineData("")]
    public async Task RetryableMethodThatIsNotAMethodToken_FailsAtStartup(string method)
    {
        string message = await AssertFailsAtStartupAsync(
            Settings.Enabled().Set("Retry:RetryableMethods:0", method));

        Assert.Contains("Retry.RetryableMethods", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Per-client sections are the shape the documentation recommends, so they must be validated on exactly
    /// the same terms as the root -- at startup, with every rule applied.
    /// </summary>
    [Fact]
    public async Task PerClientSection_IsValidatedAtStartup()
    {
        string message = await AssertFailsAtStartupAsync(
            Settings.Enabled().ForClient("Orders", "Retry:MaxRetries", "0"),
            sectionName: "Orders");

        Assert.Contains("Retry.MaxRetries", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PerClientSection_ErrorMessageNamesTheSectionPath()
    {
        string message = await AssertFailsAtStartupAsync(
            Settings.Enabled().ForClient("Orders", "RateLimiter:Enabled", "true"),
            sectionName: "Orders");

        Assert.Contains("HttpResilience:Clients:Orders", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ErrorMessages_StateTheValue_TheExpectation_AndTheReason()
    {
        string message =
            await AssertFailsAtStartupAsync(Settings.Enabled().Set("CircuitBreaker:FailureRatio", "5"));

        Assert.Contains("CircuitBreaker.FailureRatio", message, StringComparison.Ordinal);
        Assert.Contains("value '5'", message, StringComparison.Ordinal);
        Assert.Contains("Expected greater than 0 and at most 1", message, StringComparison.Ordinal);
        Assert.Contains("Reason:", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectionSettings_AreValidatedEvenWhenThePipelineIsDisabled()
    {
        string message = await AssertFailsAtStartupAsync(
            Settings.Empty()
                .Set("Enabled", "false")
                .Set("Connection:Enabled", "true")
                .Set("Connection:MaxConnectionsPerServer", "0"));

        Assert.Contains("Connection.MaxConnectionsPerServer", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The estimate is what the operator is told the schedule needs, so it has to account for the jitter the
    /// default configuration applies. A nominal figure understates it and passes a schedule whose last retry
    /// is cut off by the total budget.
    /// </summary>
    [Fact]
    public async Task RetryScheduleThatOnlyFitsWithoutJitter_FailsAtStartup()
    {
        // 3 attempts x 1s + nominal exponential backoff of (2^2 - 1) x 2s = 6s -> 9s nominal, which fits 10s.
        // With jitter it does not, and the validator must say so rather than approving it.
        string message = await AssertFailsAtStartupAsync(Settings.Enabled()
            .Set("Timeout:Attempt", "00:00:01")
            .Set("Timeout:Total", "00:00:10")
            .Set("CircuitBreaker:SamplingDuration", "00:00:30")
            .Set("Retry:MaxRetries", "2")
            .Set("Retry:BaseDelay", "00:00:02")
            .Set("Retry:BackoffType", "Exponential")
            .Set("Retry:UseJitter", "true"));

        Assert.Contains("Timeout.Total", message, StringComparison.Ordinal);
        Assert.Contains("jitter", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheSameScheduleWithoutJitter_Starts()
    {
        await AssertStartsAsync(Settings.Enabled()
            .Set("Timeout:Attempt", "00:00:01")
            .Set("Timeout:Total", "00:00:10")
            .Set("CircuitBreaker:SamplingDuration", "00:00:30")
            .Set("Retry:MaxRetries", "2")
            .Set("Retry:BaseDelay", "00:00:02")
            .Set("Retry:BackoffType", "Exponential")
            .Set("Retry:UseJitter", "false"));
    }

    /// <summary>
    /// The standard handler always carries a concurrency limiter. A client cap above it is never reached:
    /// the excess is rejected by the inner limiter rather than queued by the outer one.
    /// </summary>
    [Fact]
    public async Task ConcurrencyLimitAboveTheBackstop_FailsAtStartup()
    {
        string message = await AssertFailsAtStartupAsync(Settings.Enabled()
            .Set("ConcurrencyLimiter:Enabled", "true")
            .Set("ConcurrencyLimiter:Limit", "2000")
            .Set("ConcurrencyLimiter:Backstop", "1000"));

        Assert.Contains("ConcurrencyLimiter.Limit", message, StringComparison.Ordinal);
        Assert.Contains("Backstop", message, StringComparison.Ordinal);
        Assert.Contains("rejected", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BackstopBelowOne_FailsAtStartup()
    {
        string message = await AssertFailsAtStartupAsync(
            Settings.Enabled().Set("ConcurrencyLimiter:Backstop", "0"));

        Assert.Contains("ConcurrencyLimiter.Backstop", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Equal is not "comfortably shorter": the attempt timeout would fire at the same instant the connect
    /// gave up, so a slow connect could never be reported as a connect failure.
    /// </summary>
    [Fact]
    public async Task ConnectTimeoutEqualToAttemptTimeout_FailsAtStartup()
    {
        string message = await AssertFailsAtStartupAsync(Settings.Enabled()
            .Set("Connection:Enabled", "true")
            .Set("Connection:ConnectTimeout", "00:00:10"));

        Assert.Contains("Connection.ConnectTimeout", message, StringComparison.Ordinal);
        Assert.Contains("strictly less than", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An idle timeout at or above the connection lifetime can never fire: the age bound retires the
    /// connection first. Left to bind silently, an operator raising it is changing a number with no effect.
    /// </summary>
    [Fact]
    public async Task IdleTimeoutAtOrAboveConnectionLifetime_FailsAtStartup()
    {
        string message = await AssertFailsAtStartupAsync(Settings.Enabled()
            .Set("Connection:Enabled", "true")
            .Set("Connection:PooledConnectionLifetime", "00:02:00")
            .Set("Connection:PooledConnectionIdleTimeout", "00:02:00"));

        Assert.Contains("Connection.PooledConnectionIdleTimeout", message, StringComparison.Ordinal);
        Assert.Contains("PooledConnectionLifetime", message, StringComparison.Ordinal);
    }

    /// <summary>Nothing reads the attempt count while retries are switched off.</summary>
    [Fact]
    public async Task RetryMaxRetriesZero_IsAcceptedWhenRetriesAreDisabled()
    {
        await AssertStartsAsync(Settings.Enabled()
            .Set("Retry:Enabled", "false")
            .Set("Retry:MaxRetries", "0"));
    }

    /// <summary>
    /// A client that <b>states</b> an authority list while its mode is None has written configuration nothing
    /// reads, and that fails at registration.
    /// </summary>
    /// <remarks>
    /// This test used to state the list at the <b>root</b> and assert the same failure, which is what locked
    /// in a defect: every client inherits the root, so a root list -- the way a fleet expresses one destination
    /// allow-list for its hedged clients, which
    /// <c>ConfigurationInheritanceTests.HedgedClientWithoutItsOwnList_StillInheritsTheRootAuthorities</c>
    /// depends on -- failed the registration of every standard client in the same process. Two documented
    /// features that could not be used together, and the message named the standard client's own section
    /// rather than the root the list was in.
    /// <para>
    /// The rule now reads the client's own section, like the three other statedness rules beside it. The
    /// inherited case is <c>ConfigurationInheritanceTests.ARootAuthorityList_DoesNotFailAStandardClient</c>.
    /// </para>
    /// </remarks>
    [Fact]
    public void AClientStatingAuthoritiesWithoutByAuthorityMode_FailsAtRegistration()
    {
        OptionsValidationException failure = Assert.Throws<OptionsValidationException>(() =>
            ResilienceHarness.BuildProvider(Settings.Enabled()
                .ForClient("test", "PipelineSelection:Authorities:0", "https://a.internal")));

        string message = string.Join(" ", failure.Failures);

        Assert.Contains("PipelineSelection:Authorities", message, StringComparison.Ordinal);
        Assert.Contains("silently does not happen", message, StringComparison.Ordinal);

        // And it says where an inherited list would have been left alone, because that is the case an
        // operator reading this message is most likely to be in.
        Assert.Contains("inherited from", message, StringComparison.Ordinal);
    }

    [Theory]
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

        Assert.Contains(key.Replace(':', '.'), message, StringComparison.Ordinal);
        Assert.Contains("memory", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Enum values are bound by name from configuration. Nothing in the binder consults System.Text.Json, so
    /// removing the JSON attributes must leave this working.
    /// </summary>
    [Theory]
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
    /// The shipped defaults must satisfy the shipped rules. Every cross-property rule here was added because
    /// a combination was unsafe, and a default pairing that trips one of them would mean the package cannot
    /// start without being configured -- or, worse, that the rule was weakened to let the defaults through.
    /// </summary>
    [Fact]
    public async Task TheDefaults_PassTheirOwnValidation()
    {
        await AssertStartsAsync(Settings.Empty()
            .Set("Enabled", "true")
            .Set("Connection:Enabled", "true"));
    }

    /// <summary>The same, for a hedged client, whose budget rules differ.</summary>
    [Fact]
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
