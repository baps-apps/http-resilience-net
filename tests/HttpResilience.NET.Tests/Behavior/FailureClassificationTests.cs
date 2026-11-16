using System.Net;
using HttpResilience.NET.Tests.Infrastructure;
using Microsoft.Extensions.Time.Testing;
using Polly.CircuitBreaker;
using Polly.RateLimiting;
using Polly.Timeout;

namespace HttpResilience.NET.Tests.Behavior;

/// <summary>
/// <see cref="HttpResilienceFailure"/> names the cause of every failure the pipeline produces, so a consumer
/// never inspects Polly's exception types or the inner-exception chain.
/// </summary>
/// <remarks>
/// Each test drives the real failure, so a platform or Polly change to the exception thrown fails here.
/// </remarks>
public class FailureClassificationTests
{
    /// <summary>An origin that answers only when the request is cancelled.</summary>
    private static RecordingHandler HangingOrigin() =>
        new(async (request, _, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };
        });

    private static async Task<Exception> FailureOf(Task task)
    {
        Exception? exception = await Record.ExceptionAsync(() => task);
        Assert.NotNull(exception);
        return exception;
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task PipelineTimeout_IsTimeout()
    {
        var time = new FakeTimeProvider();
        RecordingHandler origin = HangingOrigin();

        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled()
                .Set("Retry:Enabled", "false")
                .Set("Timeout:Attempt", "00:00:00.200"),
            origin,
            timeProvider: time);

        Task<HttpResponseMessage> request = harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken);
        await origin.WaitForCountAsync(1);
        time.Advance(TimeSpan.FromMilliseconds(200));

        Exception failure = await FailureOf(request);

        Assert.IsType<TimeoutRejectedException>(failure);
        Assert.Equal(HttpResilienceFailureType.Timeout, HttpResilienceFailure.Classify(failure));
    }

    /// <summary>
    /// <c>Timeout:Client</c> (<c>HttpClient.Timeout</c>) surfaces as a <see cref="TaskCanceledException"/>, the
    /// same type as a caller's cancellation; only its inner <see cref="TimeoutException"/> tells them apart.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ClientTimeout_IsTimeout_NotCanceled()
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
                .Set("Timeout:Total", "00:00:00.200")
                .Set("Timeout:Attempt", "00:00:00.100")
                .Set("Timeout:Client", "00:00:00.300"),
            origin);

        Exception failure = await FailureOf(harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken));

        Assert.IsType<TaskCanceledException>(failure);
        Assert.Equal(HttpResilienceFailureType.Timeout, HttpResilienceFailure.Classify(failure));
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task CallerCancellation_IsCanceled()
    {
        RecordingHandler origin = HangingOrigin();
        await using ResilienceHarness harness = ResilienceHarness.Create(Settings.Enabled(), origin);
        using var cts = new CancellationTokenSource();

        Task<HttpResponseMessage> request = harness.GetAsync(cancellationToken: cts.Token);
        await origin.WaitForCountAsync(1);
        await cts.CancelAsync();

        Exception failure = await FailureOf(request);

        Assert.Equal(HttpResilienceFailureType.Canceled, HttpResilienceFailure.Classify(failure));
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task OpenCircuit_IsCircuitOpen()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled()
                .Set("Retry:Enabled", "false")
                .Set("CircuitBreaker:MinimumThroughput", "2")
                .Set("CircuitBreaker:FailureRatio", "0.1")
                .Set("CircuitBreaker:BreakDuration", "00:00:30"));

        (await harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken)).Dispose();
        (await harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken)).Dispose();

        Exception failure = await FailureOf(harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken));

        Assert.IsType<BrokenCircuitException>(failure);
        Assert.Equal(HttpResilienceFailureType.CircuitOpen, HttpResilienceFailure.Classify(failure));
        Assert.False(HttpResilienceFailure.TryGetRetryAfter(failure, out _));
    }

    /// <summary>A window limiter knows when its next permit is due, and says so.</summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task RateLimiterRejection_IsLimiterRejected_WithItsRetryAfter()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled()
                .Set("Retry:Enabled", "false")
                .Set("RateLimiter:Enabled", "true")
                .Set("RateLimiter:PermitLimit", "1")
                .Set("RateLimiter:Window", "01:00:00")
                .Set("RateLimiter:QueueLimit", "0"),
            new RecordingHandler(HttpStatusCode.OK));

        (await harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken)).Dispose();

        Exception failure = await FailureOf(harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken));

        Assert.IsType<RateLimiterRejectedException>(failure);
        Assert.Equal(HttpResilienceFailureType.LimiterRejected, HttpResilienceFailure.Classify(failure));
        Assert.True(HttpResilienceFailure.TryGetRetryAfter(failure, out TimeSpan retryAfter));
        Assert.InRange(retryAfter, TimeSpan.FromTicks(1), TimeSpan.FromHours(1));
    }

    /// <summary>A concurrency limiter cannot know when a slot frees up, so it gives no retry-after.</summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ConcurrencyLimiterRejection_IsLimiterRejected_WithoutARetryAfter()
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
                .Set("ConcurrencyLimiter:Enabled", "true")
                .Set("ConcurrencyLimiter:Limit", "1")
                .Set("ConcurrencyLimiter:QueueLimit", "0"),
            origin);

        Task<HttpResponseMessage> holder = harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken);
        try
        {
            await origin.WaitForCountAsync(1);

            Exception failure = await FailureOf(harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken));

            Assert.Equal(HttpResilienceFailureType.LimiterRejected, HttpResilienceFailure.Classify(failure));
            Assert.False(HttpResilienceFailure.TryGetRetryAfter(failure, out _));
        }
        finally
        {
            gate.SetResult();
            (await holder).Dispose();
        }
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ConnectionFailure_IsRequestFailed()
    {
        var origin = new RecordingHandler((_, _, _) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("Connection refused")));

        await using ResilienceHarness harness = ResilienceHarness.Create(Settings.Enabled(), origin);

        Exception failure = await FailureOf(harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(HttpResilienceFailureType.RequestFailed, HttpResilienceFailure.Classify(failure));
    }

    /// <summary>
    /// The pipeline returns a final non-success response rather than throwing; the consumer's
    /// <see cref="HttpResponseMessage.EnsureSuccessStatusCode"/> turns it into the failure.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task NonSuccessStatus_AfterEnsureSuccessStatusCode_IsRequestFailed()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled(), new RecordingHandler(HttpStatusCode.ServiceUnavailable));

        using HttpResponseMessage response = await harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken);
        Exception failure = Assert.Throws<HttpRequestException>(() => response.EnsureSuccessStatusCode());

        Assert.Equal(HttpResilienceFailureType.RequestFailed, HttpResilienceFailure.Classify(failure));
    }

    /// <summary>
    /// A hedged client's refusal of an unlisted authority is a configuration error, not a dependency failure.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task UnlistedAuthority_IsNone()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Hedged().Set("Hedging:Delay", "00:00:00"),
            hedged: true);

        Exception failure = await FailureOf(harness.GetAsync("http://elsewhere.test/x", TestContext.Current.CancellationToken));

        Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal(HttpResilienceFailureType.None, HttpResilienceFailure.Classify(failure));
    }

    /// <summary>
    /// A consumer that wraps the failure in its own exception keeps its cause, and a resilience cause outranks
    /// the generic exception around it.
    /// </summary>
    [Theory(Timeout = TestTimeouts.Default)]
    [MemberData(nameof(WrappedFailures))]
    public void WrappedFailure_IsClassifiedByItsCause(Exception failure, HttpResilienceFailureType expected) =>
        Assert.Equal(expected, HttpResilienceFailure.Classify(failure));

    public static TheoryData<Exception, HttpResilienceFailureType> WrappedFailures() => new()
    {
        { new InvalidOperationException("Orders failed.", new BrokenCircuitException()), HttpResilienceFailureType.CircuitOpen },
        { new HttpRequestException("Orders failed.", new TimeoutRejectedException()), HttpResilienceFailureType.Timeout },
        { new HttpRequestException("Orders failed.", new RateLimiterRejectedException()), HttpResilienceFailureType.LimiterRejected },
        { new InvalidOperationException("Orders failed.", new HttpRequestException("refused")), HttpResilienceFailureType.RequestFailed },
        { new HttpRequestException("Orders failed.", new TaskCanceledException("timed out", new TimeoutException())), HttpResilienceFailureType.Timeout },
        { new OperationCanceledException(), HttpResilienceFailureType.Canceled },
        { new InvalidOperationException("A bug."), HttpResilienceFailureType.None },
    };

    [Fact(Timeout = TestTimeouts.Default)]
    public void RetryAfter_IsFoundInsideAWrapper()
    {
        var failure = new InvalidOperationException(
            "Orders failed.", new RateLimiterRejectedException(TimeSpan.FromSeconds(7)));

        Assert.True(HttpResilienceFailure.TryGetRetryAfter(failure, out TimeSpan retryAfter));
        Assert.Equal(TimeSpan.FromSeconds(7), retryAfter);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void NullException_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => HttpResilienceFailure.Classify(null!));
        Assert.Throws<ArgumentNullException>(() => HttpResilienceFailure.TryGetRetryAfter(null!, out _));
    }

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
}
