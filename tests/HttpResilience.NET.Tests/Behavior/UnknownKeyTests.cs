using HttpResilience.NET.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HttpResilience.NET.Tests.Behavior;

/// <summary>
/// A key the schema does not have binds to nothing, so a typo silently keeps the default. Startup fails instead,
/// naming the key and the nearest real one.
/// </summary>
/// <remarks>
/// The case that matters most: an operator sets <c>Retry:Enable: false</c> during an incident and retries stay on.
/// </remarks>
public class UnknownKeyTests
{
    private static ServiceProvider Build(Settings settings, Action<IServiceCollection>? extra = null)
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Build())
            .Build();

        var services = new ServiceCollection();
        extra?.Invoke(services);
        services.AddHttpResilience(configuration);
        services.AddHttpClient("Orders").AddHttpResilienceHandler();
        return services.BuildServiceProvider(validateScopes: true);
    }

    private static string Failures(Settings settings, Action<IServiceCollection>? extra = null) =>
        string.Join(" | ", StartupAssert.Fails(() => Build(settings, extra)).Failures);

    private static IEnumerable<string> UnknownKeyFailures(ServiceProvider provider)
    {
        try
        {
            foreach (IStartupValidator validator in provider.GetServices<IStartupValidator>())
            {
                validator.Validate();
            }

            return [];
        }
        catch (Exception exception)
        {
            IEnumerable<string> messages = exception is AggregateException aggregate
                ? aggregate.InnerExceptions.OfType<OptionsValidationException>().SelectMany(e => e.Failures)
                : exception is OptionsValidationException validation ? validation.Failures : [exception.Message];

            return messages.Where(m => m.Contains("unknown key", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// A misspelled key, at the root or in a client section, at either level of the schema, fails startup naming
    /// its full path.
    /// </summary>
    /// <remarks>Fails if the unknown-key rule is removed, or stops checking either section or either level.</remarks>
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData("Retry:MaxRetrys", "HttpResilience:Retry:MaxRetrys")]
    [InlineData("Timeouts:Total", "HttpResilience:Timeouts")]
    [InlineData("Clients:Orders:CircuitBreaker:FailureRation", "HttpResilience:Clients:Orders:CircuitBreaker:FailureRation")]
    [InlineData("Clients:Orders:Retry:Enable", "HttpResilience:Clients:Orders:Retry:Enable")]
    [InlineData("Clients:Orders:Timeoutt:Total", "HttpResilience:Clients:Orders:Timeoutt")]
    public void AMisspelledKey_FailsStartup_NamingItsPath(string key, string reportedPath)
    {
        string failures = Failures(Settings.Enabled().Set(key, "1"));

        Assert.Contains($"{reportedPath} -- unknown key", failures, StringComparison.Ordinal);
    }

    /// <summary>The message suggests the real key, which is usually the whole fix.</summary>
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData("Clients:Orders:Retry:Enable", "Enabled")]
    [InlineData("Retry:MaxRetrys", "MaxRetries")]
    [InlineData("Timeouts:Total", "Timeout")]
    public void TheMessage_SuggestsTheNearestRealKey(string key, string suggestion)
    {
        string failures = Failures(Settings.Enabled().Set(key, "1"));

        Assert.Contains($"Did you mean '{suggestion}'?", failures, StringComparison.Ordinal);
    }

    /// <summary>Configuration keys are case-insensitive and the binder honours them, so the rule must too.</summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public void AKnownKeyInAnotherCase_IsAccepted()
    {
        using ServiceProvider provider = Build(Settings.Enabled()
            .Set("retry:usejitter", "false")
            .Set("Clients:Orders:TIMEOUT:attempt", "00:00:05"));

        Assert.Empty(UnknownKeyFailures(provider));
        Assert.False(provider.GetRequiredService<IOptionsMonitor<HttpResilienceOptions>>().Get("Orders").Retry.UseJitter);
    }

    /// <summary>List entries are indexed children, not schema keys.</summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public void ListEntries_AreNotTreatedAsKeys()
    {
        using ServiceProvider provider = Build(Settings.Enabled()
            .Set("Clients:Orders:Retry:RetryableMethods:0", "GET")
            .Set("Clients:Orders:PipelineSelection:Mode", "ByAuthority")
            .Set("Clients:Orders:PipelineSelection:Authorities:0", "https://orders.test"));

        Assert.Empty(UnknownKeyFailures(provider));
    }

    /// <summary>
    /// Every key the configuration reference documents is accepted, at the root and in a client section. An
    /// independent list, so the rule's own view of the schema is checked against the documentation's.
    /// </summary>
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData("Enabled")]
    [InlineData("Timeout:Total")]
    [InlineData("Timeout:Attempt")]
    [InlineData("Timeout:Client")]
    [InlineData("Retry:Enabled")]
    [InlineData("Retry:MaxRetries")]
    [InlineData("Retry:BaseDelay")]
    [InlineData("Retry:BackoffType")]
    [InlineData("Retry:UseJitter")]
    [InlineData("Retry:UseRetryAfterHeader")]
    [InlineData("Retry:MaxDelay")]
    [InlineData("Retry:DisableForUnsafeHttpMethods")]
    [InlineData("Retry:RetryableMethods:0")]
    [InlineData("CircuitBreaker:FailureRatio")]
    [InlineData("CircuitBreaker:MinimumThroughput")]
    [InlineData("CircuitBreaker:SamplingDuration")]
    [InlineData("CircuitBreaker:BreakDuration")]
    [InlineData("Connection:Enabled")]
    [InlineData("Connection:MaxConnectionsPerServer")]
    [InlineData("Connection:PooledConnectionIdleTimeout")]
    [InlineData("Connection:PooledConnectionLifetime")]
    [InlineData("Connection:ConnectTimeout")]
    [InlineData("Connection:EnableMultipleHttp2Connections")]
    [InlineData("Connection:AllowAutoRedirect")]
    [InlineData("RateLimiter:Enabled")]
    [InlineData("RateLimiter:Algorithm")]
    [InlineData("RateLimiter:PermitLimit")]
    [InlineData("RateLimiter:Window")]
    [InlineData("RateLimiter:SegmentsPerWindow")]
    [InlineData("RateLimiter:TokenLimit")]
    [InlineData("RateLimiter:TokensPerPeriod")]
    [InlineData("RateLimiter:ReplenishmentPeriod")]
    [InlineData("RateLimiter:QueueLimit")]
    [InlineData("ConcurrencyLimiter:Enabled")]
    [InlineData("ConcurrencyLimiter:Limit")]
    [InlineData("ConcurrencyLimiter:QueueLimit")]
    [InlineData("ConcurrencyLimiter:Backstop")]
    [InlineData("Hedging:Delay")]
    [InlineData("Hedging:MaxHedgedAttempts")]
    [InlineData("Hedging:DisableForUnsafeHttpMethods")]
    [InlineData("PipelineSelection:Mode")]
    [InlineData("PipelineSelection:Authorities:0")]
    public void EveryDocumentedKey_IsNotReportedAsUnknown(string key)
    {
        // Values may be invalid for the key; only the unknown-key rule is under test. A list entry is a safe
        // method, which the root's eager unsafe-method guard accepts.
        string value = key.EndsWith(":0", StringComparison.Ordinal) ? "GET" : "x";
        using ServiceProvider root = Build(Settings.Enabled().Set(key, value));
        using ServiceProvider client = Build(Settings.Enabled().Set($"Clients:Orders:{key}", value));

        Assert.Empty(UnknownKeyFailures(root));
        Assert.Empty(UnknownKeyFailures(client));
    }

    /// <summary>The startup switches belong at the root; a client section stating one is told so.</summary>
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData("ValidateClientsOnStart")]
    [InlineData("AllowUnusedClientSections")]
    [InlineData("AllowUnknownKeys")]
    public void AStartupSwitch_IsAcceptedAtTheRoot_AndRejectedInAClientSection(string key)
    {
        using ServiceProvider atRoot = Build(Settings.Enabled().Set(key, "false"));
        Assert.Empty(UnknownKeyFailures(atRoot));

        string failures = Failures(Settings.Enabled().Set($"Clients:Orders:{key}", "false"));
        Assert.Contains($"HttpResilience:Clients:Orders:{key} -- unknown key", failures, StringComparison.Ordinal);
        Assert.Contains("root", failures, StringComparison.Ordinal);
    }

    /// <summary><c>Retry:MaxAttempts</c> keeps its renamed-key message and is not reported twice.</summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public void TheRenamedKey_IsReportedOnlyAsRenamed()
    {
        string failures = Failures(Settings.Enabled().Set("Retry:MaxAttempts", "2"));

        Assert.Contains("Retry:MaxRetries", failures, StringComparison.Ordinal);
        Assert.DoesNotContain("unknown key", failures, StringComparison.Ordinal);
    }

    /// <summary>
    /// One configuration file shared by services on different versions of this package may hold keys an older
    /// version does not know. The opt-out, in configuration or code, lets those services start.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public void AllowUnknownKeys_InConfiguration_LetsStartupProceed()
    {
        using ServiceProvider provider = Build(Settings.Enabled()
            .Set("AllowUnknownKeys", "true")
            .Set("Retry:SomeFutureKey", "1"));

        Assert.Empty(UnknownKeyFailures(provider));
    }

    /// <summary>
    /// A file that states it was written for a newer minor version of the schema may hold keys this version does
    /// not have yet (a minor version only adds optional keys), so they are a startup Warning naming them, not a
    /// failure: one shared file can be rolled out ahead of the services that read it.
    /// </summary>
    /// <remarks>
    /// Fails if the keys still fail startup, if they are not reported, or if the client section's keys are left
    /// out. Each is reported once, not once per client.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public void ANewerMinorSchemaVersion_TurnsUnknownKeysIntoAWarning()
    {
        var logs = new ListLoggerProvider();
        using ServiceProvider provider = Build(
            Settings.Enabled()
                .Set("SchemaVersion", NewerMinor)
                .Set("Retry:SomeFutureKey", "1")
                .ForClient("Orders", "Timeout:SomeOtherFutureKey", "1"),
            services =>
            {
                services.AddLogging(logging => logging.AddProvider(logs));
                services.AddHttpClient("Search").AddHttpResilienceHandler();
            });

        Assert.Empty(UnknownKeyFailures(provider));
        HostStartup.Run(provider);

        LogEntry notice = Assert.Single(logs.WithEvent(HttpResilience.NET.Internal.HttpResilienceLogging.ConfigurationCategory, 18));
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, notice.Level);
        Assert.Contains($"SchemaVersion {NewerMinor}", notice.Message, StringComparison.Ordinal);
        Assert.Contains("HttpResilience:Retry:SomeFutureKey", notice.Message, StringComparison.Ordinal);
        Assert.Contains("HttpResilience:Clients:Orders:Timeout:SomeOtherFutureKey", notice.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The schema version relaxes only the case it exists for. Stated at this version or an older one (or not at
    /// all), an unknown key is a typo and still fails startup.
    /// </summary>
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(null)]
    [InlineData("3.0")]
    [InlineData("2.4")]
    [InlineData("3")]
    public void TheCurrentOrAnOlderSchemaVersion_KeepsUnknownKeysFatal(string? declared)
    {
        string failures = Failures(Settings.Enabled().Set("SchemaVersion", declared).Set("Retry:MaxRetrys", "1"));

        Assert.Contains("HttpResilience:Retry:MaxRetrys -- unknown key", failures, StringComparison.Ordinal);
    }

    /// <summary>
    /// A newer major version may rename keys or change what they mean, so running on such a file is refused
    /// outright, whatever <c>AllowUnknownKeys</c> says. A value that is not a version is refused too.
    /// </summary>
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData("4.0", "newer major version")]
    [InlineData("99", "newer major version")]
    [InlineData("three", "is not a version")]
    [InlineData("3.x", "is not a version")]
    public void ANewerMajorOrMalformedSchemaVersion_FailsStartup(string declared, string expected)
    {
        string failures = Failures(Settings.Enabled().Set("SchemaVersion", declared).Set("AllowUnknownKeys", "true"));

        Assert.Contains($"HttpResilience:SchemaVersion -- value '{declared}'", failures, StringComparison.Ordinal);
        Assert.Contains(expected, failures, StringComparison.Ordinal);
    }

    /// <summary>The schema version describes the whole file, so it belongs at the root.</summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public void ASchemaVersionInAClientSection_IsRefused_NamingTheRoot()
    {
        string failures = Failures(Settings.Enabled().ForClient("Orders", "SchemaVersion", "3.0"));

        Assert.Contains("HttpResilience:Clients:Orders:SchemaVersion -- unknown key", failures, StringComparison.Ordinal);
        Assert.Contains("only at the root", failures, StringComparison.Ordinal);
    }

    /// <summary>With <c>AllowUnknownKeys</c> nothing is reported about unknown keys, newer schema or not.</summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public void AllowUnknownKeys_AlsoSilencesTheNewerSchemaWarning()
    {
        var logs = new ListLoggerProvider();
        using ServiceProvider provider = Build(
            Settings.Enabled().Set("SchemaVersion", NewerMinor).Set("AllowUnknownKeys", "true").Set("Retry:SomeFutureKey", "1"),
            services => services.AddLogging(logging => logging.AddProvider(logs)));

        HostStartup.Run(provider);

        Assert.Empty(logs.WithEvent(HttpResilience.NET.Internal.HttpResilienceLogging.ConfigurationCategory, 18));
    }

    /// <summary>A minor version this package's own major can reach but has not.</summary>
    private static string NewerMinor =>
        $"{HttpResilience.NET.Internal.SchemaVersionRules.Package.Major}.{HttpResilience.NET.Internal.SchemaVersionRules.Package.Minor + 1}";

    [Fact(Timeout = TestTimeouts.Default)]
    public void AllowUnknownKeys_InCode_LetsStartupProceed()
    {
        using ServiceProvider provider = Build(
            Settings.Enabled().Set("Retry:SomeFutureKey", "1"),
            services => services.Configure<HttpResilienceStartupOptions>(o => o.AllowUnknownKeys = true));

        Assert.Empty(UnknownKeyFailures(provider));
    }
}
