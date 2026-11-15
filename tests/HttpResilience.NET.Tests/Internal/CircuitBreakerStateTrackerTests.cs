using HttpResilience.NET.Internal;
using Microsoft.Extensions.DependencyInjection;

namespace HttpResilience.NET.Tests.Internal;

/// <summary>
/// Breaker callbacks fire from whichever request thread caused the transition, and several pipelines report
/// into one tracker at once.
/// </summary>
public class CircuitBreakerStateTrackerTests
{
    [Fact(Timeout = TestTimeouts.Default)]
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
    /// Enumeration runs while the gauge is being collected, so it must not throw when a concurrent
    /// transition writes a new key.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
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
        }, TestContext.Current.CancellationToken);

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
    /// The gauge reads this dictionary concurrently with the writes.
    /// </summary>
    /// <remarks>
    /// <c>HttpResilienceMetrics</c> mutates an authority-to-host cache inside the observation callback while
    /// enumerating the tracker, and scrapes coincide with transitions. A throwing gauge callback typically drops
    /// the whole scrape, so it must not throw.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task TheGauge_IsReadableWhileTransitionsLand()
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        services.AddSingleton<CircuitBreakerStateTracker>();
        services.AddSingleton<HttpResilienceMetrics>();
        await using ServiceProvider provider = services.BuildServiceProvider();

        var tracker = provider.GetRequiredService<CircuitBreakerStateTracker>();
        _ = provider.GetRequiredService<HttpResilienceMetrics>();

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
        }, TestContext.Current.CancellationToken);

        int collections = 0;
        while (!stop.IsCancellationRequested)
        {
            _ = collector.Collect("http_resilience.circuit_breaker.state");
            collections++;
        }

        await writer;

        Assert.True(collections > 0, "the collection loop never ran, so this proved nothing.");
    }
}
