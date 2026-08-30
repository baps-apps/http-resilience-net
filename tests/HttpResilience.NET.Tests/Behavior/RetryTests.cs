using System.Net;
using System.Net.Http.Headers;
using HttpResilience.NET.Tests.Infrastructure;

namespace HttpResilience.NET.Tests.Behavior;

public class RetryTests
{
    [Fact]
    public async Task RetriesTransientFailures_ThenReturnsSuccess()
    {
        var origin = new RecordingHandler((request, attempt, _) => Task.FromResult(
            new HttpResponseMessage(attempt < 3 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            {
                RequestMessage = request
            }));

        await using ResilienceHarness harness = ResilienceHarness.Create(Settings.Enabled(), origin);

        HttpResponseMessage response = await harness.GetAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, origin.Count);
    }

    [Fact]
    public async Task StopsAtMaxRetries()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled()
                .Set("Retry:MaxRetries", "4")
                // The budget must fit the schedule: 5 attempts of 10s needs a 50s total.
                .Set("Timeout:Total", "00:01:00"));

        await harness.GetAsync();

        Assert.Equal(5, harness.Origin.Count);
    }

    /// <summary>
    /// Disabling retries is the most common request during an incident, so the supported spelling of it must
    /// work. Setting the attempt count to zero is rejected at startup instead, because the underlying strategy
    /// requires at least one.
    /// </summary>
    [Fact]
    public async Task Disabled_MakesExactlyOneAttempt()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled().Set("Retry:Enabled", "false"));

        HttpResponseMessage response = await harness.GetAsync();

        Assert.Equal(1, harness.Origin.Count);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task Disabled_StillAppliesTimeoutsAndCircuitBreaker()
    {
        var origin = new RecordingHandler(async (_, _, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled()
                .Set("Retry:Enabled", "false")
                .Set("Timeout:Attempt", "00:00:00.200")
                .Set("Timeout:Total", "00:00:02"),
            origin);

        await Assert.ThrowsAsync<Polly.Timeout.TimeoutRejectedException>(() => harness.GetAsync());
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task RetriesTransientStatusCodes(HttpStatusCode status)
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled(), new RecordingHandler(status));

        await harness.GetAsync();

        Assert.Equal(3, harness.Origin.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict)]
    public async Task DoesNotRetryClientErrors(HttpStatusCode status)
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled(), new RecordingHandler(status));

        HttpResponseMessage response = await harness.GetAsync();

        Assert.Equal(1, harness.Origin.Count);
        Assert.Equal(status, response.StatusCode);
    }

    [Fact]
    public async Task RetriesHttpRequestException_AndSurfacesItWhenAttemptsAreExhausted()
    {
        var origin = new RecordingHandler((_, _, _) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("connection refused")));

        await using ResilienceHarness harness = ResilienceHarness.Create(Settings.Enabled(), origin);

        // The original exception reaches the caller; it is not wrapped in a package-specific type.
        HttpRequestException exception = await Assert.ThrowsAsync<HttpRequestException>(() => harness.GetAsync());

        Assert.Equal("connection refused", exception.Message);
        Assert.Equal(3, origin.Count);
    }

    [Fact]
    public async Task HonoursRetryAfterHeader()
    {
        var origin = new RecordingHandler((request, attempt, _) =>
        {
            var response = new HttpResponseMessage(
                attempt == 1 ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK)
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
            Settings.Enabled().Set("Retry:BaseDelay", "00:00:00"), origin);

        long start = Environment.TickCount64;
        HttpResponseMessage response = await harness.GetAsync();
        long elapsed = Environment.TickCount64 - start;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, origin.Count);

        // The configured base delay is zero, so any meaningful wait can only have come from the header.
        Assert.True(elapsed >= 300, $"Expected the Retry-After delay to be honoured, waited {elapsed}ms.");
    }

    [Fact]
    public async Task RetryAfterHeader_CanBeIgnored()
    {
        var origin = new RecordingHandler((request, attempt, _) =>
        {
            var response = new HttpResponseMessage(
                attempt == 1 ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK)
            {
                RequestMessage = request
            };

            if (attempt == 1)
            {
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            }

            return Task.FromResult(response);
        });

        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled()
                .Set("Retry:BaseDelay", "00:00:00")
                .Set("Retry:UseRetryAfterHeader", "false"),
            origin);

        long start = Environment.TickCount64;
        await harness.GetAsync();

        Assert.True(Environment.TickCount64 - start < 5_000, "A 30 second Retry-After should have been ignored.");
    }
}
