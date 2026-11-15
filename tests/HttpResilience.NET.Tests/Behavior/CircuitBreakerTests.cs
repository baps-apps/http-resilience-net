using System.Net;
using HttpResilience.NET.Tests.Infrastructure;
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

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task Opens_AfterTheFailureRatioIsExceeded_AndThenFailsFast()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(Breaking());

        await harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken);
        await harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken);

        int callsBeforeBreak = harness.Origin.Count;

        await Assert.ThrowsAsync<BrokenCircuitException>(() => harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken));

        // Failing fast means the request never reached the origin.
        Assert.Equal(callsBeforeBreak, harness.Origin.Count);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task AnOpenCircuit_IsReportedOpenByTheStateGauge()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(Breaking());

        await harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken);
        await harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, Assert.Single(BreakerState.All(harness.Services)).Value);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task HealthyDependencies_ReportNoOpenCircuit()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled(), new RecordingHandler(HttpStatusCode.OK));

        await harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(BreakerState.NotClosed(harness.Services));
    }

    /// <summary>
    /// Under per-authority selection one client owns several breakers. Reporting them under the client name
    /// alone would let the last callback to fire overwrite the others, so one host recovering would mask
    /// another still being open.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
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

        await harness.GetAsync("http://bad.test/x", TestContext.Current.CancellationToken);
        await harness.GetAsync("http://bad.test/x", TestContext.Current.CancellationToken);

        await harness.GetAsync("http://good.test/x", TestContext.Current.CancellationToken);
        await harness.GetAsync("http://good.test/x", TestContext.Current.CancellationToken);

        IReadOnlyList<string> notClosed = BreakerState.NotClosed(harness.Services);

        Assert.Contains(notClosed, key => key.Contains("bad.test", StringComparison.Ordinal));
        Assert.DoesNotContain(notClosed, key => key.Contains("good.test", StringComparison.Ordinal));
        // Every breaker is reported, each under its own key: the failing host open, its neighbour and the
        // shared pipeline for unlisted hosts closed.
        IReadOnlyDictionary<string, long> all = BreakerState.All(harness.Services);
        Assert.Equal(3, all.Count);
        Assert.Equal(1, all["test -> http://bad.test"]);
        Assert.Equal(0, all["test -> http://good.test"]);

        // The healthy host keeps serving even though its neighbour's circuit is open.
        HttpResponseMessage response = await harness.GetAsync("http://good.test/x", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact(Timeout = TestTimeouts.Default)]
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

        await harness.GetAsync("http://bad.test/x", TestContext.Current.CancellationToken);
        await harness.GetAsync("http://bad.test/x", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<BrokenCircuitException>(() => harness.GetAsync("http://bad.test/x", TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.OK, (await harness.GetAsync("http://good.test/x", TestContext.Current.CancellationToken)).StatusCode);
    }

    /// <summary>
    /// Any authority outside the allow-list shares one pipeline, so no amount of request traffic to novel
    /// hosts can mint additional pipelines, breakers or metric series.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
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
                await harness.GetAsync($"http://attacker-{i}.test/x", TestContext.Current.CancellationToken);
            }
            catch (BrokenCircuitException)
            {
                // Expected once the shared pipeline's breaker opens; the point of the test is how many
                // pipelines exist, not whether they are healthy.
            }
        }

        IReadOnlyDictionary<string, long> data = BreakerState.All(harness.Services);

        // The listed authority's breaker and the one shared breaker: twenty novel hosts added nothing.
        Assert.Equal(["test -> http://known.test", "test -> shared"], data.Keys.Order(StringComparer.Ordinal));
        Assert.NotEqual(0, data["test -> shared"]);
    }
}
