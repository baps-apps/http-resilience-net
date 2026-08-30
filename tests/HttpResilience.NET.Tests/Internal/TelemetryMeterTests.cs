using System.Diagnostics.Metrics;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using HttpResilience.NET.Tests.Infrastructure;

namespace HttpResilience.NET.Tests.Internal;

/// <summary>
/// <see cref="HttpResilienceTelemetryExtensions.PollyMeterName"/> is the name consumers pass to
/// <c>metrics.AddMeter(...)</c>. It re-declares a name Polly owns, so if Polly ever renames its meter this
/// constant drifts silently and every dashboard built on it goes blank.
/// </summary>
public partial class TelemetryMeterTests
{
    [Fact]
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
        await harness.GetAsync();

        Assert.NotEmpty(meters);
    }

    /// <summary>
    /// Every instrument name in the XML documentation on
    /// <see cref="HttpResilienceTelemetryExtensions.MeterName"/> is one the meter actually publishes, and
    /// every instrument it publishes is documented there.
    /// </summary>
    /// <remarks>
    /// That documentation ships inside the package -- <c>GenerateDocumentationFile</c> puts
    /// <c>HttpResilience.NET.xml</c> in <c>lib/net10.0</c> beside the assembly -- so it is what a consumer
    /// reads from IntelliSense while writing a dashboard query. It named
    /// <c>http.resilience.rate_limiter.available_permits</c> and
    /// <c>http.resilience.rate_limiter.queued_requests</c> for a release after the instruments were renamed
    /// to <c>http.resilience.limiter.*</c> -- the rename was deliberate, because the concurrency cap and the
    /// backstop report on them too and an instrument named for one of three kinds would be misleading. Both
    /// documented names emit nothing, and a query built on them returns an empty graph during the incident
    /// it was written for. Every <c>docs/*.md</c> reference was already correct, so nothing but this compared
    /// the two.
    /// <para>
    /// The same shape as <c>scripts/check-benchmark-docs.py</c>, which exists because the benchmark tables
    /// and the raw reports beside them disagreed through four reviews with nothing comparing them. Both
    /// directions are asserted: an undocumented instrument is as much a gap as a documented one that does
    /// not exist.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheDocumentedInstrumentNames_AreTheOnesTheMeterPublishes()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(Settings.Enabled());
        using var collector = new GaugeCollector(harness.Services);

        // The gauges are created in the HttpResilienceMetrics constructor, which the pipeline configurator
        // resolves when the pipeline is first built -- so one request publishes all three.
        (await harness.GetAsync()).Dispose();

        IReadOnlyList<string> published = collector.PublishedInstruments;
        Assert.Equal(3, published.Count);

        string documentation = MeterNameDocumentation();

        // Tag keys share the http.resilience prefix with the instruments and are documented in the same
        // block, so they are named here rather than inferred. A tag that is renamed without this list being
        // updated fails as an undocumented instrument, which points at the right paragraph.
        string[] documentedTags = ["http.resilience.authority", "http.resilience.limiter.kind"];

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
