using System.Diagnostics.Metrics;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using HttpResilience.NET.Tests.Infrastructure;

namespace HttpResilience.NET.Tests.Internal;

/// <summary>
/// <see cref="HttpResilienceTelemetryExtensions.PollyMeterName"/> re-declares a name Polly owns; this test
/// fails if Polly renames its meter.
/// </summary>
public partial class TelemetryMeterTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task PollyMeterName_MatchesTheMeterPollyActuallyPublishesOn()
    {
        List<string> meters = [];
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == HttpResilienceTelemetryExtensions.PollyMeterName)
                {
                    lock (meters)
                    {
                        meters.Add(instrument.Meter.Name);
                    }

                    l.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.Start();

        await using ResilienceHarness harness = ResilienceHarness.Create(Settings.Enabled());
        await harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEmpty(meters);
    }

    /// <summary>
    /// The meter carries the package version, so an exporter can tell which version emitted a series.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task TheMeter_CarriesThePackageVersion()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(Settings.Enabled());
        using var collector = new GaugeCollector(harness.Services);
        (await harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken)).Dispose();

        string? expected = typeof(HttpResilienceTelemetryExtensions).Assembly.GetName().Version is { } v
            ? $"{v.Major}.{v.Minor}.{v.Build}"
            : null;

        Assert.All(collector.Instruments, instrument => Assert.Equal(expected, instrument.Meter.Version));
        Assert.NotEmpty(collector.Instruments);
    }

    /// <summary>
    /// Every instrument name in the XML documentation on
    /// <see cref="HttpResilienceTelemetryExtensions.MeterName"/> is one the meter actually publishes, and
    /// every instrument it publishes is documented there.
    /// </summary>
    /// <remarks>
    /// That documentation ships in the package and is what consumers read in IntelliSense when writing
    /// queries. Both directions are asserted: an undocumented instrument is as much a gap as a documented one
    /// that does not exist.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task TheDocumentedInstrumentNames_AreTheOnesTheMeterPublishes()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(Settings.Enabled());
        using var collector = new GaugeCollector(harness.Services);

        // The gauges are created in the HttpResilienceMetrics constructor, which the pipeline configurator
        // resolves when the pipeline is first built -- so one request publishes all three.
        (await harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken)).Dispose();

        IReadOnlyList<string> published = collector.PublishedInstruments;
        Assert.Equal(3, published.Count);

        string documentation = MeterNameDocumentation();

        // Tag keys share the http.resilience prefix and are documented in the same block, so they are named
        // here rather than inferred.
        string[] documentedTags = ["http_resilience.authority", "http_resilience.limiter.kind"];

        foreach (string name in published)
        {
            Assert.Contains(name, documentation, StringComparison.Ordinal);
        }

        foreach (Match match in ResilienceMetricToken().Matches(documentation))
        {
            string token = match.Value.TrimEnd('.');
            Assert.True(
                published.Contains(token) || documentedTags.Contains(token),
                $"The XML documentation on {nameof(HttpResilienceTelemetryExtensions.MeterName)} names " +
                $"'{token}', which is neither an instrument the meter publishes " +
                $"({string.Join(", ", published)}) nor a documented tag key. A consumer querying it gets an " +
                "empty graph.");
        }
    }

    /// <summary>
    /// The documentation text for <c>MeterName</c>, read from the XML file that ships in the package.
    /// </summary>
    private static string MeterNameDocumentation()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "HttpResilience.NET.xml");
        Assert.True(
            File.Exists(path),
            $"'{path}' is missing. GenerateDocumentationFile is enabled on the library project and the file " +
            "is copied beside the test assembly; without it this test cannot check what consumers read.");

        const string MemberName =
            "F:Microsoft.Extensions.DependencyInjection.HttpResilienceTelemetryExtensions.MeterName";

        XElement? member = XDocument.Load(path)
            .Descendants("member")
            .FirstOrDefault(e => (string?)e.Attribute("name") == MemberName);

        Assert.True(member is not null, $"No XML documentation found for '{MemberName}'.");
        return member!.Value;
    }

    [GeneratedRegex(@"http\.resilience\.[a-z0-9_.]+")]
    private static partial Regex ResilienceMetricToken();
}
