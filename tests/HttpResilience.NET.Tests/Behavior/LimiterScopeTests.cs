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
/// Which limiters are per client and which are per authority.
/// </summary>
public class LimiterScopeTests
{
    /// <summary>
    /// Under <c>ByAuthority</c> the backstop is one limiter per authority -- unless a rate limiter has taken
    /// the handler's limiter slot, which moves the backstop into a handler of its own, outside the
    /// per-authority pipelines and therefore one per client.
    /// </summary>
    /// <remarks>
    /// Counterpart to <c>Backstop_IsPerAuthority_UnderByAuthoritySelection</c>, which has no rate limiter and
    /// admits two concurrent requests. Here the bound is <c>1 x Backstop</c>, as the documentation states.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task Backstop_IsPerClientNotPerAuthority_WhenARateLimiterHasDisplacedIt()
    {
        var gate = new TaskCompletionSource();
        var origin = new RecordingHandler(async (request, _, cancellationToken) =>
        {
            await gate.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };
        });

        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled()
                .Set("Retry:Enabled", "false")
                .Set("ConcurrencyLimiter:Backstop", "1")
                .Set("RateLimiter:Enabled", "true")
                .Set("RateLimiter:PermitLimit", "100")
                .Set("RateLimiter:Window", "00:01:00")
                .Set("PipelineSelection:Mode", "ByAuthority")
                .Set("PipelineSelection:Authorities:0", "http://a.test")
                .Set("PipelineSelection:Authorities:1", "http://b.test"),
            origin);

        Task<HttpResponseMessage> held = harness.GetAsync("http://a.test/x", TestContext.Current.CancellationToken);
        await origin.WaitForCountAsync(1);

        // A different authority, and still rejected: the backstop is outside the per-authority pipelines.
        await Assert.ThrowsAsync<RateLimiterRejectedException>(() => harness.GetAsync("http://b.test/x", TestContext.Current.CancellationToken));

        gate.SetResult();
        (await held).Dispose();

        Assert.Equal(1, origin.MaxConcurrent);
    }
}
