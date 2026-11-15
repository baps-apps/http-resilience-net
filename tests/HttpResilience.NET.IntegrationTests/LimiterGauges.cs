using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;

namespace HttpResilience.NET.IntegrationTests;

/// <summary>Reads this package's limiter gauges for one container.</summary>
internal static class LimiterGauges
{
    /// <summary>Requests waiting in any of the container's limiter queues right now.</summary>
    /// <remarks>
    /// Scoped to the container's <see cref="IMeterFactory"/>: the meter name is process-wide, so a parallel
    /// test's gauges would otherwise be counted too.
    /// </remarks>
    public static long Queued(IServiceProvider services)
    {
        IMeterFactory scope = services.GetRequiredService<IMeterFactory>();
        long queued = 0;

        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == HttpResilienceTelemetryExtensions.MeterName &&
                    ReferenceEquals(instrument.Meter.Scope, scope) &&
                    instrument.Name == "http_resilience.limiter.queued_requests")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<int>((_, value, _, _) => queued += value);
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => queued += value);
        listener.Start();
        listener.RecordObservableInstruments();

        return queued;
    }
}

/// <summary>Waits for a condition that, once true, stays true, failing instead of hanging.</summary>
internal static class Eventually
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    public static async Task UntilAsync(Func<bool> condition, Func<string> describe)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(Limit);

        while (!condition())
        {
            try
            {
                await Task.Delay(10, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                Assert.Fail($"Not reached within {Limit}: {describe()}");
            }
        }
    }
}
