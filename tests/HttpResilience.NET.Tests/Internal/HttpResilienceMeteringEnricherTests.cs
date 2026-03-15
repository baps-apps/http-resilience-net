using System.Net;
using Polly;
using Polly.Telemetry;
using HttpResilience.NET.Internal;

namespace HttpResilience.NET.Tests.Internal;

public class HttpResilienceMeteringEnricherTests
{
    private static EnrichmentContext<HttpResponseMessage, object> CreateContext(
        Outcome<HttpResponseMessage>? outcome,
        string? operationKey = null,
        string? pipelineName = null,
        string? strategyName = null)
    {
        var source = new ResilienceTelemetrySource("test-source", "test-instance", null);
        var @event = new ResilienceEvent(ResilienceEventSeverity.Error, "test-event");
        var context = operationKey is null
            ? ResilienceContextPool.Shared.Get()
            : ResilienceContextPool.Shared.Get(operationKey);

        var telemetryEvent = new TelemetryEventArguments<HttpResponseMessage, object>(
            source,
            @event,
            context,
            args: null!,
            outcome: outcome);

        var tags = new List<KeyValuePair<string, object?>>();
        if (pipelineName is not null)
        {
            tags.Add(new KeyValuePair<string, object?>("pipeline.name", pipelineName));
        }
        if (strategyName is not null)
        {
            tags.Add(new KeyValuePair<string, object?>("strategy.name", strategyName));
        }

        return new EnrichmentContext<HttpResponseMessage, object>(telemetryEvent, tags);
    }

    [Fact]
    public void Enrich_SetsErrorType_ForException()
    {
        var outcome = Outcome.FromException<HttpResponseMessage>(new TimeoutException());
        var enricher = new HttpResilienceMeteringEnricher();
        var context = CreateContext(outcome);

        enricher.Enrich(context);

        Assert.Contains(context.Tags, t => t.Key == "error.type" && t.Value is string v && v.Contains("TimeoutException"));
    }

    [Fact]
    public void Enrich_SetsErrorType_ForNonSuccessStatusCode()
    {
        var response = new HttpResponseMessage(HttpStatusCode.InternalServerError);
        var outcome = Outcome.FromResult(response);
        var enricher = new HttpResilienceMeteringEnricher();
        var context = CreateContext(outcome);

        enricher.Enrich(context);

        Assert.Contains(context.Tags, t => t.Key == "error.type" && (string?)t.Value == "HttpStatusCode.500");
    }

    [Fact]
    public void Enrich_SetsRequestName_FromOperationKey()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        var outcome = Outcome.FromResult(response);
        var enricher = new HttpResilienceMeteringEnricher();
        var context = CreateContext(outcome, operationKey: "my-operation");

        enricher.Enrich(context);

        Assert.Contains(context.Tags, t => t.Key == "request.name" && (string?)t.Value == "my-operation");
    }

    [Fact]
    public void Enrich_SetsRequestName_FromPipelineAndStrategy_WhenNoOperationKey()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        var outcome = Outcome.FromResult(response);
        var enricher = new HttpResilienceMeteringEnricher();
        var context = CreateContext(outcome, operationKey: null, pipelineName: "pipeline", strategyName: "retry");

        enricher.Enrich(context);

        Assert.Contains(context.Tags, t => t.Key == "request.name" && (string?)t.Value == "pipeline/retry");
    }

    [Fact]
    public void Enrich_SetsRequestDependencyName_FromResponseRequestUri()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/resource")
        };
        var outcome = Outcome.FromResult(response);
        var enricher = new HttpResilienceMeteringEnricher();
        var context = CreateContext(outcome);

        enricher.Enrich(context);

        Assert.Contains(context.Tags, t => t.Key == "request.dependency.name" && (string?)t.Value == "https://api.example.com");
    }
}

