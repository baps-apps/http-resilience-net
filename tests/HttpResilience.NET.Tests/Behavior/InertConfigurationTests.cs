using System.Diagnostics.Metrics;
using System.Net.Http.Headers;
using System.Net;
using System.Threading.RateLimiting;
using BclConcurrencyLimiterOptions = System.Threading.RateLimiting.ConcurrencyLimiterOptions;
using HttpResilience.NET.Internal;
using HttpResilience.NET.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.RateLimiting;
using Polly.Timeout;

namespace HttpResilience.NET.Tests.Behavior;

/// <summary>
/// Configuration a client states but its pipeline never reads fails registration; shared root values do not.
/// </summary>
public class InertConfigurationTests
{
    private static IConfigurationRoot Configuration(Settings settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings.Build()).Build();

    /// <summary>
    /// The root section is not a pipeline. Holding it to the standard pipeline's retry-budget rule failed
    /// startup for applications whose clients are all hedged and never run a retry at all.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task RootSection_IsNotHeldToTheStandardRetryBudget()
    {
        // 3 attempts of 2s plus 1.5s of backoff needs 7.5s, which does not fit the 6s root budget -- but no
        // client on this application uses the retry strategy.
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled()
                .Set("Timeout:Total", "00:00:06")
                .Set("Timeout:Attempt", "00:00:02")
                .Set("Retry:MaxRetries", "2")
                .Set("Retry:BaseDelay", "00:00:00.500")
                .Set("Retry:BackoffType", "Exponential")
                .Set("CircuitBreaker:SamplingDuration", "00:00:30")
                .Set("Hedging:Delay", "00:00:00.500")
                .Set("PipelineSelection:Authorities:0", "http://origin.test"),
            hedged: true);

        HttpResponseMessage response = await harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    /// <summary>
    /// A standard client stating hedging keys is the mirror of a hedged client stating retry keys, and the
    /// dangerous entry is identical: a written decision about duplicating mutating requests that the pipeline
    /// has no strategy to read.
    /// </summary>
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData("Hedging:MaxHedgedAttempts", "5")]
    [InlineData("Hedging:DisableForUnsafeHttpMethods", "false")]
    public void HedgingConfigurationOnAStandardClient_FailsAtStartup(string key, string value)
    {
        var services = new ServiceCollection();
        services.AddHttpResilience(Configuration(Settings.Enabled().ForClient("orders", key, value)));

        OptionsValidationException exception = StartupAssert.Fails(services, () => services.AddHttpClient("orders").AddHttpResilienceHandler());

        string message = string.Join(" ", exception.Failures);
        Assert.Contains("HttpResilience:Clients:orders -- Hedging", message, StringComparison.Ordinal);
        Assert.Contains("bound and never read", message, StringComparison.Ordinal);
        Assert.Contains("AddHttpResilienceHedgingHandler", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Root hedging values are inherited by hedged clients, so a standard client sharing that root must not
    /// be the thing that fails startup -- the same asymmetry the retry half already had.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task RootHedgingConfiguration_DoesNotFailAStandardClient()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled().Set("Hedging:MaxHedgedAttempts", "2"));

        await harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, harness.Origin.Count);
    }
}
