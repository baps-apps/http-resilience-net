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
/// The startup line stating how much failing traffic a circuit breaker needs before it can open.
/// </summary>
public class CircuitBreakerReachTests
{
    private static IConfigurationRoot Configuration(Settings settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings.Build()).Build();

    private static string[] BreakerReachFor(Settings settings, bool hedged)
    {
        var sink = new ListLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddProvider(sink);
        });
        services.AddHttpResilience(Configuration(settings));

        IHttpClientBuilder builder = services.AddHttpClient("orders");
        _ = hedged ? builder.AddHttpResilienceHedgingHandler() : builder.AddHttpResilienceHandler();

        using ServiceProvider provider = services.BuildServiceProvider();
        HostStartup.Run(provider);

        return [.. sink.Records.Where(record =>
            record.Contains("circuit breaker for client", StringComparison.Ordinal))];
    }

    /// <summary>
    /// Whether a circuit breaker can ever open is arithmetic over two configured values and per-replica
    /// traffic. The package can do that division at startup and an operator usually has not.
    /// </summary>
    /// <remarks>
    /// Information rather than Warning: unlike the disabled-client and unsafe-method notices, the
    /// configuration reported here is frequently correct. What is not visible without it is that a client
    /// with the default thresholds and a few requests a second per replica has a breaker in its
    /// configuration, a breaker in its runbook, and no breaker in effect.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public void EveryEnabledClient_StatesTheTrafficItsBreakerNeeds()
    {
        var sink = new ListLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddProvider(sink);
        });

        // The platform defaults: 100 attempts over 30 seconds is 3.3 per second, in ONE replica.
        services.AddHttpResilience(Configuration(Settings.Enabled()
            .Set("CircuitBreaker:MinimumThroughput", "100")
            .Set("CircuitBreaker:SamplingDuration", "00:00:30")));
        services.AddHttpClient("orders").AddHttpResilienceHandler();

        using ServiceProvider provider = services.BuildServiceProvider();
        HostStartup.Run(provider);

        string[] lines =
            [.. sink.Records.Where(r => r.Contains("needs 3.3 failing", StringComparison.Ordinal))];
        Assert.Single(lines);
        Assert.StartsWith("[Information]", lines[0], StringComparison.Ordinal);
        Assert.Contains("orders", lines[0], StringComparison.Ordinal);

        // Quoted in caller requests as well, because that is the number a service's own dashboards show:
        // the breaker is inside the retry loop, so at MaxRetries 2 one failing request is three observations.
        Assert.Contains("about 1.1 failing caller requests", lines[0], StringComparison.Ordinal);
        Assert.Contains("MinimumThroughput 100", lines[0], StringComparison.Ordinal);
        Assert.Contains("per replica, not fleet-wide", lines[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// A hedged client's breaker-reach line quotes the caller-request rate as equal to the attempt rate,
    /// because the hedging pipeline has no retry strategy.
    /// </summary>
    /// <remarks>
    /// A hedged client inherits the root's retry defaults but never retries, so dividing by
    /// <c>MaxRetries + 1</c> would under-report the traffic its breaker needs (1.1 instead of 3.3), making an
    /// inert breaker look engaged. Fails if <c>CircuitBreakerReachNotice.AttemptsPerRequest</c> reads
    /// <c>Retry</c> for every pipeline.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public void AHedgedClientsBreakerReach_IsNotDividedByAnInheritedRetryCount()
    {
        // 100 attempts over 30s is 3.3 per second. The root states the retry defaults, which a hedged client
        // inherits and must not divide by.
        string[] lines = BreakerReachFor(
            Settings.Hedged()
                .Set("CircuitBreaker:MinimumThroughput", "100")
                .Set("CircuitBreaker:SamplingDuration", "00:00:30")
                .Set("Retry:Enabled", "true")
                .Set("Retry:MaxRetries", "2"),
            hedged: true);

        string line = Assert.Single(lines);
        Assert.Contains("needs 3.3 failing attempts per second", line, StringComparison.Ordinal);
        Assert.Contains("about 3.3 failing caller requests per second", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// The standard pipeline still divides, because there the breaker really does sit inside the retry loop.
    /// </summary>
    /// <remarks>
    /// The other half of the same fix: making the hedging figure right must not flatten the standard one,
    /// which is correct and is the reason the division exists.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public void AStandardClientsBreakerReach_IsStillDividedByItsRetryCount()
    {
        string[] lines = BreakerReachFor(
            Settings.Enabled()
                .Set("CircuitBreaker:MinimumThroughput", "100")
                .Set("CircuitBreaker:SamplingDuration", "00:00:30")
                .Set("Retry:Enabled", "true")
                .Set("Retry:MaxRetries", "2"),
            hedged: false);

        string line = Assert.Single(lines);
        Assert.Contains("needs 3.3 failing attempts per second", line, StringComparison.Ordinal);
        Assert.Contains("about 1.1 failing caller requests per second", line, StringComparison.Ordinal);
    }
}
