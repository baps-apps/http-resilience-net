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
/// The error.type tag the telemetry enricher adds.
/// </summary>
public class ErrorTypeTagTests
{
    private static IConfigurationRoot Configuration(Settings settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings.Build()).Build();

    /// <summary>
    /// The one tag this package adds, asserted on the instrument Polly publishes. Response outcomes are
    /// already tagged by the platform and must not be tagged twice.
    /// </summary>
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(false, "503")]
    [InlineData(true, "System.Net.Http.HttpRequestException")]
    public async Task ErrorTypeTag_LandsExactlyOnce_OnThePollyInstrumentThatExists(
        bool originThrows,
        string expectedErrorType)
    {
        var instruments = new HashSet<string>(StringComparer.Ordinal);
        var errorTypes = new HashSet<string>(StringComparer.Ordinal);
        var duplicated = new List<string>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == HttpResilienceTelemetryExtensions.PollyMeterName)
            {
                lock (instruments)
                {
                    instruments.Add(instrument.Name);
                }

                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<int>((instrument, _, tags, _) =>
        {
            int seen = 0;
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                if (tag.Key != "error.type" || tag.Value is not string value)
                {
                    continue;
                }

                seen++;
                lock (errorTypes)
                {
                    errorTypes.Add($"{instrument.Name}|{value}");
                }
            }

            if (seen > 1)
            {
                lock (duplicated)
                {
                    duplicated.Add($"{instrument.Name} carried error.type {seen} times");
                }
            }
        });
        listener.Start();

        var services = new ServiceCollection();
        services.AddHttpResilience(Configuration(Settings.Enabled()));
        services.AddHttpResilienceTelemetry();
        RecordingHandler origin = originThrows
            ? new RecordingHandler((_, _, _) =>
                Task.FromException<HttpResponseMessage>(new HttpRequestException("connection refused")))
            : new RecordingHandler(HttpStatusCode.ServiceUnavailable);

        services.AddHttpClient("t").AddHttpResilienceHandler().ConfigurePrimaryHttpMessageHandler(() => origin);

        await using (ServiceProvider provider = services.BuildServiceProvider())
        {
            try
            {
                await provider.GetRequiredService<IHttpClientFactory>().CreateClient("t")
                    .GetAsync("http://origin.test/x", TestContext.Current.CancellationToken);
            }
            catch (HttpRequestException)
            {
                // The exhausted-retries outcome for the exception case; the measurements are the assertion.
            }
        }

        listener.Dispose();

        Assert.Contains("resilience.polly.strategy.events", instruments);
        Assert.Contains("resilience.polly.pipeline.duration", instruments);
        Assert.Contains("resilience.polly.strategy.attempt.duration", instruments);
        Assert.Contains($"resilience.polly.strategy.events|{expectedErrorType}", errorTypes);

        // The platform tags response outcomes itself, so adding the key here too put it on twice.
        Assert.Empty(duplicated);
    }
}
