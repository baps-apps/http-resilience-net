using System.Net;
using HttpResilience.NET.Internal;
using Polly;
using Polly.Telemetry;

namespace HttpResilience.NET.Tests.Internal;

public class MeteringEnricherTests
{
    private static List<KeyValuePair<string, object?>> Enrich(
        Outcome<HttpResponseMessage>? outcome,
        params KeyValuePair<string, object?>[] existingTags)
    {
        var enricher = new HttpResilienceMeteringEnricher();
        var tags = new List<KeyValuePair<string, object?>>(existingTags);
        ResilienceContext context = ResilienceContextPool.Shared.Get();

        try
        {
            var telemetryEvent = new TelemetryEventArguments<HttpResponseMessage, string>(
                new ResilienceTelemetrySource("pipeline", "instance", "strategy"),
                new ResilienceEvent(ResilienceEventSeverity.Warning, "event"),
                context,
                "args",
                outcome);

            enricher.Enrich(new EnrichmentContext<HttpResponseMessage, string>(telemetryEvent, tags));
            return tags;
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }
    }

    /// <summary>
    /// The gap this enricher exists to fill: Polly tags an exception outcome with <c>exception.type</c>, and
    /// nothing supplies <c>error.type</c> -- the attribute the OpenTelemetry convention names, and the one a
    /// dashboard filters on. Without it, a query for <c>error.type</c> misses every connection failure.
    /// </summary>
    [Fact]
    public void AddsExceptionTypeName()
    {
        List<KeyValuePair<string, object?>> tags =
            Enrich(Outcome.FromException<HttpResponseMessage>(new HttpRequestException("boom")));

        KeyValuePair<string, object?> tag = Assert.Single(tags);
        Assert.Equal("error.type", tag.Key);
        Assert.Equal("System.Net.Http.HttpRequestException", tag.Value);
    }

    /// <summary>
    /// A failed response is the platform's to tag, on every pipeline in the process. Tagging it here as well
    /// put <c>error.type</c> on the measurement twice, because this enricher runs first.
    /// </summary>
    [Fact]
    public void AddsNothing_ForFailedResponses_BecauseThePlatformAlreadyTagsThem()
    {
        Assert.Empty(Enrich(Outcome.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))));
    }

    /// <summary>
    /// Insurance for the reverse ordering: if this enricher ever ran after one that supplies the key, it must
    /// not add a second copy.
    /// </summary>
    [Fact]
    public void AddsNothing_WhenTheTagIsAlreadyPresent()
    {
        List<KeyValuePair<string, object?>> tags = Enrich(
            Outcome.FromException<HttpResponseMessage>(new HttpRequestException("boom")),
            new KeyValuePair<string, object?>("error.type", "already-set"));

        Assert.Equal("already-set", Assert.Single(tags).Value);
    }

    [Fact]
    public void AddsNothing_ForSuccessfulResponses()
    {
        Assert.Empty(Enrich(Outcome.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));
    }

    [Fact]
    public void AddsNothing_WhenThereIsNoOutcome()
    {
        Assert.Empty(Enrich(null));
    }

    /// <summary>
    /// Nothing derived from the request may appear in a metric tag: a dimension whose cardinality is the
    /// number of hosts a process happens to call will eventually evict everything else in the backend.
    /// </summary>
    [Fact]
    public void NeverTagsAnythingDerivedFromTheRequestUri()
    {
        // Even an exception message carrying the URL must not reach a tag: only the type name is used.
        var exception = new HttpRequestException("connecting to tenant-12345.customer.example/orders/42 failed");

        List<KeyValuePair<string, object?>> tags =
            Enrich(Outcome.FromException<HttpResponseMessage>(exception));

        Assert.All(tags, tag =>
        {
            string? value = tag.Value?.ToString();
            Assert.DoesNotContain("tenant-12345", value ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain("customer.example", value ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain("orders", value ?? string.Empty, StringComparison.Ordinal);
        });
    }
}
