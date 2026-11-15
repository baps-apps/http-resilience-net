using System.Net;
using System.Text;
using HttpResilience.NET.Tests.Infrastructure;
using Polly.RateLimiting;

namespace HttpResilience.NET.Tests.Behavior;

/// <summary>
/// A standard client's synchronous <c>HttpClient.Send</c> runs the same pipeline as <c>SendAsync</c>: retries,
/// the unsafe-method guard and the concurrency bound.
/// </summary>
/// <remarks>
/// <c>DelegatingHandler.Send</c> forwards to the inner handler without calling <c>SendAsync</c>, so any handler
/// in the chain that implements only the asynchronous path is silently skipped by every synchronous request.
/// </remarks>
public class SyncSendTests
{
    /// <summary>Every standard pipeline shape: which handler groups are active decides the chain.</summary>
    public static TheoryData<string> Shapes => ["defaults", "concurrency limiter", "rate limiter", "both limiters"];

    private static Settings For(string shape)
    {
        Settings settings = Settings.Enabled();
        if (shape is "concurrency limiter" or "both limiters")
        {
            settings.Set("ConcurrencyLimiter:Enabled", "true").Set("ConcurrencyLimiter:Limit", "5");
        }

        if (shape is "rate limiter" or "both limiters")
        {
            settings.Set("RateLimiter:Enabled", "true").Set("RateLimiter:PermitLimit", "1000")
                .Set("RateLimiter:Window", "01:00:00");
        }

        return settings;
    }

    /// <remarks>Fails if a handler group on the synchronous path skips the retry strategy.</remarks>
    [Theory(Timeout = TestTimeouts.Default)]
    [MemberData(nameof(Shapes))]
    public async Task Send_RetriesATransientGet(string shape)
    {
        var origin = new RecordingHandler((request, attempt, _) => Task.FromResult(
            new HttpResponseMessage(attempt <= 2 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            {
                RequestMessage = request
            }));
        await using ResilienceHarness harness = ResilienceHarness.Create(For(shape), origin);

        using var request = new HttpRequestMessage(HttpMethod.Get, "http://origin.test/x");
        using HttpResponseMessage response = harness.Client.Send(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, origin.Count);
    }

    /// <remarks>Fails if the synchronous path loses the safe-method guard.</remarks>
    [Theory(Timeout = TestTimeouts.Default)]
    [MemberData(nameof(Shapes))]
    public async Task Send_DeliversAFailingPostOnce(string shape)
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(For(shape));

        using var request = new HttpRequestMessage(HttpMethod.Post, "http://origin.test/orders")
        {
            Content = new StringContent("""{"sku":"ABC"}""", Encoding.UTF8, "application/json")
        };
        using HttpResponseMessage response = harness.Client.Send(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(["""{"sku":"ABC"}"""], harness.Origin.Bodies);
    }

    /// <summary>
    /// The concurrency bound holds for synchronous callers too: with the backstop at one, a second
    /// <c>Send</c> while the first is held at the origin is rejected.
    /// </summary>
    /// <remarks>
    /// Both places the backstop can sit: the platform handler's limiter slot, and its own handler when a rate
    /// limiter takes that slot. Fails if either is skipped on the synchronous path.
    /// </remarks>
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData("defaults")]
    [InlineData("rate limiter")]
    public async Task Send_IsBoundedByTheBackstop(string shape)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Holds only the first request, so a missing bound shows up as a second request that succeeds.
        var origin = new RecordingHandler(async (request, attempt, cancellationToken) =>
        {
            if (attempt == 1)
            {
                await gate.Task.WaitAsync(cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };
        });
        await using ResilienceHarness harness = ResilienceHarness.Create(
            For(shape).Set("ConcurrencyLimiter:Backstop", "1"), origin);

        CancellationToken token = TestContext.Current.CancellationToken;
        Task<HttpStatusCode> held = Task.Run(
            () =>
            {
                using var first = new HttpRequestMessage(HttpMethod.Get, "http://origin.test/x");
                using HttpResponseMessage response = harness.Client.Send(first, token);
                return response.StatusCode;
            },
            token);
        await origin.WaitForCountAsync(1);

        using var second = new HttpRequestMessage(HttpMethod.Get, "http://origin.test/x");
        Assert.Throws<RateLimiterRejectedException>(() => harness.Client.Send(second, token));

        gate.SetResult();
        Assert.Equal(HttpStatusCode.OK, await held);
        Assert.Equal(1, origin.Count);
    }
}
