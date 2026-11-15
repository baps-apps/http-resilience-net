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
/// HttpClient.Timeout bounds the response body, which Timeout:Total cannot reach.
/// </summary>
public class ResponseBodyTimeoutTests
{
    /// <summary>Sends response headers immediately and then never finishes the body.</summary>
    private sealed class StalledContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            await stream.WriteAsync(new byte[] { 1 }, cancellationToken);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    /// <summary>
    /// An origin that answers headers promptly and then stops sending the body degrades every call to the
    /// full <c>Timeout:Client</c> budget without opening the circuit.
    /// </summary>
    /// <remarks>
    /// Pins documented behavior (<c>docs/OPERATIONS.md</c>): <c>Timeout:Client</c> fires as caller
    /// cancellation, which is not a dependency failure. If this changes, update the documentation. Contrast
    /// <c>CircuitBreakerTests.Opens_AfterTheFailureRatioIsExceeded_AndThenFailsFast</c>, where failures before
    /// headers open the breaker.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task AStalledResponseBody_IsBoundedByTheClientTimeout_AndNeverOpensTheCircuit()
    {
        var origin = new RecordingHandler((request, _, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StalledContent()
            }));

        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled()
                .Set("Retry:Enabled", "false")
                .Set("Timeout:Attempt", "00:00:00.200")
                .Set("Timeout:Total", "00:00:00.500")
                .Set("Timeout:Client", "00:00:01")
                .Set("CircuitBreaker:MinimumThroughput", "2")
                .Set("CircuitBreaker:FailureRatio", "0.1")
                .Set("CircuitBreaker:SamplingDuration", "00:00:30")
                .Set("CircuitBreaker:BreakDuration", "00:00:30"),
            origin);

        for (int i = 0; i < 4; i++)
        {
            await Assert.ThrowsAsync<TaskCanceledException>(() => harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken));
        }

        // Four requests, each degraded to the full client budget, and four attempts at the origin: nothing
        // was retried or rejected.
        Assert.Equal(4, origin.Count);

        // Well past MinimumThroughput at a 10% failure ratio, and the breaker has observed no failure at
        // all: the handler chain returned successfully with headers each time.
        Assert.Empty(BreakerState.NotClosed(harness.Services));
    }
}
