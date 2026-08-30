using System.Net;
using System.Threading.RateLimiting;
using HttpResilience.NET.Internal;
using HttpResilience.NET.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics;

namespace HttpResilience.NET.Tests.Internal;

/// <summary>
/// The limiter gauges must survive a scrape that lands while the container is tearing down.
/// </summary>
/// <remarks>
/// <see cref="HttpResilienceMetrics"/> holds a strong reference to every rate limiter it publishes statistics
/// for. Both it and the limiters are container singletons, and the container disposes singletons in reverse
/// order of instantiation -- an order neither the meter nor the limiters control. So a collection that lands
/// between the limiter's disposal and the meter's calls <c>GetStatistics()</c> on a disposed limiter, which
/// throws <see cref="ObjectDisposedException"/>.
/// <para>
/// An exception out of an observable-instrument callback is not silently absorbed: it surfaces in whichever
/// <c>MeterListener</c> is collecting -- an OpenTelemetry reader logs it as an instrument failure, and a
/// hand-rolled listener sees it thrown out of <c>RecordObservableInstruments</c>. Shutdown is exactly when an
/// operator is least able to tell a real fault from a teardown artefact, so the gauge must not manufacture
/// one.
/// </para>
/// </remarks>
public class LimiterGaugeDisposalTests
{
    /// <summary>
    /// Production change that would make this fail: calling <c>GetStatistics()</c> without the disposal
    /// guard in <c>HttpResilienceMetrics.ObserveLimiters</c>.
    /// </summary>
    [Fact]
    public void ADisposedLimiter_DoesNotThrowOutOfTheGaugeCallback()
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        services.AddSingleton<CircuitBreakerStateTracker>();
        services.AddSingleton<HttpResilienceMetrics>();

        using ServiceProvider provider = services.BuildServiceProvider();
        var metrics = provider.GetRequiredService<HttpResilienceMetrics>();

        var limiter = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = 1,
            Window = TimeSpan.FromHours(1),
            QueueLimit = 0
        });

        metrics.Track("doomed", LimiterKind.Rate, limiter);
        limiter.Dispose();

        using var collector = new GaugeCollector(provider);

        // Fails with ObjectDisposedException without the guard.
        IReadOnlyList<Measurement> permits =
            collector.Collect("http.resilience.limiter.available_permits");

        Assert.Empty(permits);
    }

    /// <summary>
    /// The guard must drop only the limiter that is gone. A container with one live client and one disposed
    /// limiter still has a number to report for the live one.
    /// </summary>
    [Fact]
    public void ALiveLimiter_IsStillReported_AlongsideADisposedOne()
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        services.AddSingleton<CircuitBreakerStateTracker>();
        services.AddSingleton<HttpResilienceMetrics>();

        using ServiceProvider provider = services.BuildServiceProvider();
        var metrics = provider.GetRequiredService<HttpResilienceMetrics>();

        using var live = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = 7,
            Window = TimeSpan.FromHours(1),
            QueueLimit = 0
        });

        var doomed = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = 1,
            Window = TimeSpan.FromHours(1),
            QueueLimit = 0
        });

        metrics.Track("live", LimiterKind.Rate, live);
        metrics.Track("doomed", LimiterKind.Rate, doomed);
        doomed.Dispose();

        using var collector = new GaugeCollector(provider);

        Measurement permits = Assert.Single(
            collector.Collect("http.resilience.limiter.available_permits"));

        Assert.Equal(7, permits.Value);
        Assert.Equal("live", permits.Tag("http.client.name"));
    }
}
