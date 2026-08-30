using HttpResilience.NET.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HttpResilience.NET.Tests.Internal;

/// <summary>
/// Breaker callbacks fire from whichever request thread caused the transition, and several pipelines report
/// into one tracker at once.
/// </summary>
public class CircuitBreakerStateTrackerTests
{
    [Fact]
    public async Task ConcurrentReports_AreAllVisible_AndNoneAreLost()
    {
        var tracker = new CircuitBreakerStateTracker();
        CircuitKey[] keys = [.. Enumerable.Range(0, 32).Select(i => new CircuitKey("client", $"https://h{i}.test"))];

        // Every key is written many times from many threads, and the last write for each is Open.
        await Task.WhenAll(keys.Select(key => Task.Run(() =>
        {
            for (int i = 0; i < 200; i++)
            {
                tracker.Report(key, CircuitState.Closed);
                tracker.Report(key, CircuitState.HalfOpen);
            }

            tracker.Report(key, CircuitState.Open);
        })));

        Assert.Equal(keys.Length, tracker.Enumerate().Count());
        Assert.All(keys, key => Assert.Equal(CircuitState.Open, tracker.GetState(key)));
    }

    /// <summary>
    /// Enumeration runs while a health check is being answered, so it must not throw when a concurrent
    /// transition writes a new key.
    /// </summary>
    [Fact]
    public async Task EnumerationDuringConcurrentWrites_DoesNotThrow()
    {
        var tracker = new CircuitBreakerStateTracker();
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        Task writer = Task.Run(() =>
        {
            for (int i = 0; !stop.IsCancellationRequested; i++)
            {
                tracker.Report(new CircuitKey("client", $"https://h{i % 64}.test"), CircuitState.Open);
            }
        });

        while (!stop.IsCancellationRequested)
        {
            foreach ((CircuitKey key, CircuitState state) in tracker.Enumerate())
            {
                _ = key.ToString() + state;
            }
        }

        await writer;
    }

    /// <summary>
    /// The two <i>readers</i> of this dictionary, driven concurrently with the writes: the metrics gauge and
    /// the health check.
    /// </summary>
    /// <remarks>
    /// The two tests above cover the tracker itself. Neither covers its readers, and the gauge is the one with
    /// state of its own -- <c>HttpResilienceMetrics</c> keeps an authority-to-host parse cache that it mutates
    /// from inside the observation callback while enumerating the tracker. A collection lands whenever the
    /// metrics backend scrapes, which is exactly when breakers are transitioning, and neither the gauge nor
    /// the health check may throw out of that.
    /// <para>
    /// A gauge callback that throws is not a loud failure: <c>MeterListener.RecordObservableInstruments</c>
    /// surfaces it to the collector, which for most exporters means the whole scrape is dropped -- so the
    /// symptom is a dashboard that goes blank during an incident, which is when it is needed.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task GaugeAndHealthCheck_AreReadableWhileTransitionsLand()
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        services.AddSingleton<CircuitBreakerStateTracker>();
        services.AddSingleton<HttpResilienceMetrics>();
        await using ServiceProvider provider = services.BuildServiceProvider();

        var tracker = provider.GetRequiredService<CircuitBreakerStateTracker>();
        _ = provider.GetRequiredService<HttpResilienceMetrics>();
        var check = new HttpResilienceHealthCheck(tracker);

        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        using var collector = new GaugeCollector(provider);

        Task writer = Task.Run(() =>
        {
            CircuitState[] states = [CircuitState.Open, CircuitState.HalfOpen, CircuitState.Closed];
            for (int i = 0; !stop.IsCancellationRequested; i++)
            {
                // New authorities keep arriving, so the gauge's parse cache is written during collection too.
                tracker.Report(new CircuitKey("client", $"https://h{i}.test"), states[i % states.Length]);
            }
        });

        int collections = 0;
        while (!stop.IsCancellationRequested)
        {
            _ = collector.Collect("http.resilience.circuit_breaker.state");
            _ = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);
            collections++;
        }

        await writer;

        Assert.True(collections > 0, "the collection loop never ran, so this proved nothing.");
    }
}
