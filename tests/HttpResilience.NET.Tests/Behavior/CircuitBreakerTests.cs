using System.Net;
using HttpResilience.NET.Tests.Infrastructure;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Polly.CircuitBreaker;

namespace HttpResilience.NET.Tests.Behavior;

public class CircuitBreakerTests
{
    private static Settings Breaking() => Settings.Enabled()
        .Set("Retry:Enabled", "false")
        .Set("CircuitBreaker:MinimumThroughput", "2")
        .Set("CircuitBreaker:FailureRatio", "0.1")
        .Set("CircuitBreaker:SamplingDuration", "00:00:30")
        .Set("CircuitBreaker:BreakDuration", "00:00:30");

    [Fact]
    public async Task Opens_AfterTheFailureRatioIsExceeded_AndThenFailsFast()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(Breaking());

        await harness.GetAsync();
        await harness.GetAsync();

        int callsBeforeBreak = harness.Origin.Count;

        await Assert.ThrowsAsync<BrokenCircuitException>(() => harness.GetAsync());

        // Failing fast means the request never reached the origin.
        Assert.Equal(callsBeforeBreak, harness.Origin.Count);
    }

    [Fact]
    public async Task OpenCircuit_IsReportedAsDegraded_NotUnhealthy()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(Breaking());

        await harness.GetAsync();
        await harness.GetAsync();

        Assert.Equal(HealthStatus.Degraded, HealthState.Status(harness.Services));
        Assert.NotEmpty(HealthState.NotClosed(harness.Services));
    }

    [Fact]
    public async Task HealthyDependencies_AreReportedHealthy()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled(), new RecordingHandler(HttpStatusCode.OK));

        await harness.GetAsync();

        Assert.Equal(HealthStatus.Healthy, HealthState.Status(harness.Services));
    }

    /// <summary>
    /// Under per-authority selection one client owns several breakers. Reporting them under the client name
    /// alone would let the last callback to fire overwrite the others, so one host recovering would mask
    /// another still being open.
    /// </summary>
    [Fact]
    public async Task PerAuthority_TracksEachBreakerSeparately()
    {
        var origin = new RecordingHandler((request, _, _) => Task.FromResult(
            new HttpResponseMessage(request.RequestUri!.Host == "bad.test"
                ? HttpStatusCode.InternalServerError
                : HttpStatusCode.OK)
            { RequestMessage = request }));

        await using ResilienceHarness harness = ResilienceHarness.Create(
            Breaking()
                .Set("PipelineSelection:Mode", "ByAuthority")
                .Set("PipelineSelection:Authorities:0", "http://bad.test")
                .Set("PipelineSelection:Authorities:1", "http://good.test"),
            origin);

        await harness.GetAsync("http://bad.test/x");
        await harness.GetAsync("http://bad.test/x");

        await harness.GetAsync("http://good.test/x");
        await harness.GetAsync("http://good.test/x");

        IReadOnlyList<string> notClosed = HealthState.NotClosed(harness.Services);

        Assert.Contains(notClosed, key => key.Contains("bad.test", StringComparison.Ordinal));
        Assert.DoesNotContain(notClosed, key => key.Contains("good.test", StringComparison.Ordinal));
        // A breaker that never left Closed fires no transition callback, so absence of an entry means
        // healthy. Only the failing host is tracked, and it is tracked under its own key.
        Assert.Single(HealthState.Data(harness.Services));

        // The healthy host keeps serving even though its neighbour's circuit is open.
        HttpResponseMessage response = await harness.GetAsync("http://good.test/x");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PerAuthority_IsolatesBreakersSoOneBadHostDoesNotBreakAnother()
    {
        var origin = new RecordingHandler((request, _, _) => Task.FromResult(
            new HttpResponseMessage(request.RequestUri!.Host == "bad.test"
                ? HttpStatusCode.InternalServerError
                : HttpStatusCode.OK)
            { RequestMessage = request }));

        await using ResilienceHarness harness = ResilienceHarness.Create(
            Breaking()
                .Set("PipelineSelection:Mode", "ByAuthority")
                .Set("PipelineSelection:Authorities:0", "http://bad.test")
                .Set("PipelineSelection:Authorities:1", "http://good.test"),
            origin);

        await harness.GetAsync("http://bad.test/x");
        await harness.GetAsync("http://bad.test/x");

        await Assert.ThrowsAsync<BrokenCircuitException>(() => harness.GetAsync("http://bad.test/x"));
        Assert.Equal(HttpStatusCode.OK, (await harness.GetAsync("http://good.test/x")).StatusCode);
    }

    /// <summary>
    /// Any authority outside the allow-list shares one pipeline, so no amount of request traffic to novel
    /// hosts can mint additional pipelines, breakers or metric series.
    /// </summary>
    [Fact]
    public async Task PerAuthority_UnlistedHostsShareASinglePipeline()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Breaking()
                .Set("PipelineSelection:Mode", "ByAuthority")
                .Set("PipelineSelection:Authorities:0", "http://known.test"));

        for (int i = 0; i < 20; i++)
        {
            try
            {
                await harness.GetAsync($"http://attacker-{i}.test/x");
            }
            catch (BrokenCircuitException)
            {
                // Expected once the shared pipeline's breaker opens; the point of the test is how many
                // pipelines exist, not whether they are healthy.
            }
        }

        IReadOnlyDictionary<string, object> data = HealthState.Data(harness.Services);

        Assert.Single(data);
        Assert.Contains("shared", data.Keys.Single(), StringComparison.Ordinal);
    }
}
