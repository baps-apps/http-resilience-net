using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using HttpResilience.NET.Internal;
using HttpResilience.NET.Tests.Infrastructure;

namespace HttpResilience.NET.Tests.Behavior;

/// <summary>
/// <c>Retry:MaxDelay</c> bounds every wait between attempts: a computed backoff is cut to it, and a
/// <c>Retry-After</c> longer than it ends the retries, returning the origin's response at once rather than
/// holding the caller and its limiter permit.
/// </summary>
public partial class RetryDelayCapTests
{
    private static RecordingHandler ThrottledOrigin(RetryConditionHeaderValue retryAfter) =>
        new((request, _, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { RequestMessage = request };
            response.Headers.RetryAfter = retryAfter;
            return Task.FromResult(response);
        });

    /// <summary>
    /// The origin asks for 15 seconds; the default cap is 5. The caller gets the 429 immediately.
    /// </summary>
    /// <remarks>
    /// Retrying sooner than the origin asked would most likely earn another 429 while adding load to a
    /// dependency that is shedding it, so the cap ends the retries instead of shortening the wait. Fails if the
    /// header is honoured uncapped (the request then waits 15 seconds) or clamped (the origin sees a second call).
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ARetryAfterLongerThanMaxDelay_EndsTheRetries_AndReturnsTheResponse()
    {
        RecordingHandler origin = ThrottledOrigin(new RetryConditionHeaderValue(TimeSpan.FromSeconds(15)));
        await using ResilienceHarness harness = ResilienceHarness.Create(Settings.Enabled(), origin);

        long start = Environment.TickCount64;
        using HttpResponseMessage response =
            await harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken);
        long elapsed = Environment.TickCount64 - start;

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(1, origin.Count);
        Assert.True(elapsed < 5_000, $"The 15 second Retry-After should not have been waited for; took {elapsed}ms.");
    }

    /// <summary>The same for the HTTP-date form of the header.</summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ARetryAfterDateBeyondMaxDelay_EndsTheRetries()
    {
        RecordingHandler origin = ThrottledOrigin(new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddHours(1)));
        await using ResilienceHarness harness = ResilienceHarness.Create(Settings.Enabled(), origin);

        using HttpResponseMessage response =
            await harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(1, origin.Count);
    }

    /// <summary>
    /// A wait within the cap is still the origin's to set.
    /// </summary>
    /// <remarks>Fails if the cap is applied to every Retry-After rather than only to longer ones.</remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ARetryAfterWithinMaxDelay_IsHonoured()
    {
        var origin = new RecordingHandler((request, attempt, _) =>
        {
            var response = new HttpResponseMessage(attempt == 1 ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK)
            {
                RequestMessage = request
            };
            if (attempt == 1)
            {
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(400));
            }

            return Task.FromResult(response);
        });

        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled().Set("Retry:MaxDelay", "00:00:01"), origin);

        long start = Environment.TickCount64;
        using HttpResponseMessage response =
            await harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken);
        long elapsed = Environment.TickCount64 - start;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, origin.Count);
        Assert.True(elapsed >= 300, $"Expected the Retry-After delay to be honoured, waited {elapsed}ms.");
    }

    /// <summary>
    /// With the header switched off, nothing about it ends the retries: the computed schedule runs.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task AnIgnoredRetryAfter_DoesNotEndTheRetries()
    {
        RecordingHandler origin = ThrottledOrigin(new RetryConditionHeaderValue(TimeSpan.FromSeconds(15)));
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled().Set("Retry:UseRetryAfterHeader", "false"), origin);

        using HttpResponseMessage response =
            await harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(3, origin.Count);
    }

    /// <summary>
    /// The computed backoff is cut to the cap: exponential from 100 ms would wait 100, 200, 400, 800 and 1,600 ms.
    /// </summary>
    /// <remarks>
    /// Read from the retry log (event 1), which carries the delay Polly actually used. Fails if
    /// <c>Retry:MaxDelay</c> is not passed to the platform's retry strategy.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task TheComputedBackoff_IsCutToMaxDelay()
    {
        var logs = new ListLoggerProvider();
        RecordingHandler origin = new(HttpStatusCode.ServiceUnavailable);
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled()
                .Set("Retry:MaxRetries", "5")
                .Set("Retry:BaseDelay", "00:00:00.100")
                .Set("Retry:BackoffType", "Exponential")
                .Set("Retry:UseJitter", "false")
                .Set("Retry:MaxDelay", "00:00:00.150"),
            origin,
            logs: logs);

        using HttpResponseMessage response =
            await harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(6, origin.Count);
        double[] delays =
        [
            .. logs.WithEvent(HttpResilienceLogging.PipelineCategory, 1)
                .Select(entry => double.Parse(
                    DelayPattern().Match(entry.Message).Groups[1].Value, CultureInfo.InvariantCulture))
        ];

        Assert.Equal([100, 150, 150, 150, 150], delays);
    }

    [GeneratedRegex(@"after ([\d.]+)ms")]
    private static partial Regex DelayPattern();
}
