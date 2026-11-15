using System.Net;
using HttpResilience.NET.Internal;
using HttpResilience.NET.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Polly.CircuitBreaker;

namespace HttpResilience.NET.Tests.Behavior;

/// <summary>
/// A breaker that opens must also be seen to recover. The state gauge and events 3 and 4 come from the
/// half-open and closed callbacks; if either stopped reporting, the gauge would read Open forever and keep
/// paging after the dependency had recovered.
/// </summary>
/// <remarks>
/// Deterministic: time is a <see cref="FakeTimeProvider"/>, and Polly awaits each transition callback inside
/// the request that causes it, so the gauge is current as soon as that request reaches the origin or returns.
/// </remarks>
public class CircuitBreakerRecoveryTests
{
    private static readonly TimeSpan BreakDuration = TimeSpan.FromSeconds(30);

    private static Settings Breaking() => Settings.Enabled()
        .Set("Retry:Enabled", "false")
        .Set("CircuitBreaker:MinimumThroughput", "2")
        .Set("CircuitBreaker:FailureRatio", "0.1")
        .Set("CircuitBreaker:SamplingDuration", "00:00:30")
        .Set("CircuitBreaker:BreakDuration", "00:00:30");

    /// <summary>
    /// Open, then half-open while the trial request is in flight, then closed once it succeeds: the gauge
    /// reads 1, 2, 0 and events 2, 3, 4 are logged in that order.
    /// </summary>
    /// <remarks>
    /// Fails if the half-open callback stops reporting (the gauge reads 1 during the trial) or the closed one
    /// does (it reads 2 afterwards), or if either stops logging.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ASuccessfulTrial_AfterTheBreakDuration_ClosesTheCircuit_AndTheGaugeAndLogsFollow()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider();
        var logs = new ListLoggerProvider();
        var origin = new ScriptedOrigin();

        await using ResilienceHarness harness = ResilienceHarness.Create(
            Breaking(), origin.Handler, timeProvider: time, logs: logs);

        await OpenAsync(harness, cancellationToken);
        Assert.Equal(1, Gauge(harness));

        time.Advance(BreakDuration);
        origin.Healthy = true;
        origin.HoldNext();

        Task<HttpResponseMessage> trial = harness.GetAsync(cancellationToken: cancellationToken);
        long halfOpen;
        try
        {
            await harness.Origin.WaitForCountAsync(3);
            halfOpen = Gauge(harness);
        }
        finally
        {
            // Always let the trial finish, so a failed assertion reports instead of leaving it held.
            origin.Release();
        }

        using (HttpResponseMessage response = await trial.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(2, halfOpen);
        Assert.Equal(0, Gauge(harness));
        Assert.Equal([2, 3, 4], BreakerEventIds(logs));
    }

    /// <summary>
    /// A trial that fails opens the circuit again: the gauge returns to 1, a second event 2 is logged, and the
    /// next request fails fast without reaching the origin.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task AFailedTrial_ReopensTheCircuit()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider();
        var logs = new ListLoggerProvider();
        var origin = new ScriptedOrigin();

        await using ResilienceHarness harness = ResilienceHarness.Create(
            Breaking(), origin.Handler, timeProvider: time, logs: logs);

        await OpenAsync(harness, cancellationToken);

        time.Advance(BreakDuration);
        (await harness.GetAsync(cancellationToken: cancellationToken)).Dispose();

        Assert.Equal(1, Gauge(harness));
        Assert.Equal([2, 3, 2], BreakerEventIds(logs));

        int calls = harness.Origin.Count;
        await Assert.ThrowsAsync<BrokenCircuitException>(() => harness.GetAsync(cancellationToken: cancellationToken));
        Assert.Equal(calls, harness.Origin.Count);
    }

    /// <summary>
    /// The breaker events carry the IDs, levels and category the operations guide tells operators to alert on.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task BreakerEvents_CarryTheirDocumentedIdLevelAndCategory()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider();
        var logs = new ListLoggerProvider();
        var origin = new ScriptedOrigin();

        await using ResilienceHarness harness = ResilienceHarness.Create(
            Breaking(), origin.Handler, timeProvider: time, logs: logs);

        await OpenAsync(harness, cancellationToken);
        time.Advance(BreakDuration);
        origin.Healthy = true;
        (await harness.GetAsync(cancellationToken: cancellationToken)).Dispose();

        LogEntry opened = Assert.Single(logs.WithEvent(HttpResilienceLogging.PipelineCategory, 2));
        LogEntry halfOpen = Assert.Single(logs.WithEvent(HttpResilienceLogging.PipelineCategory, 3));
        LogEntry closed = Assert.Single(logs.WithEvent(HttpResilienceLogging.PipelineCategory, 4));

        Assert.Equal(LogLevel.Warning, opened.Level);
        Assert.Equal(LogLevel.Information, halfOpen.Level);
        Assert.Equal(LogLevel.Information, closed.Level);
        Assert.Contains("'test'", opened.Message, StringComparison.Ordinal);
        Assert.Contains("'shared'", opened.Message, StringComparison.Ordinal);
    }

    /// <summary>Two failures with retry off: the second opens the breaker.</summary>
    private static async Task OpenAsync(ResilienceHarness harness, CancellationToken cancellationToken)
    {
        (await harness.GetAsync(cancellationToken: cancellationToken)).Dispose();
        (await harness.GetAsync(cancellationToken: cancellationToken)).Dispose();
    }

    private static long Gauge(ResilienceHarness harness) =>
        Assert.Single(BreakerState.All(harness.Services)).Value;

    private static int[] BreakerEventIds(ListLoggerProvider logs) =>
    [
        .. logs.Entries
            .Where(entry => entry.Category == HttpResilienceLogging.PipelineCategory &&
                entry.EventId.Id is 2 or 3 or 4)
            .Select(entry => entry.EventId.Id)
    ];

    /// <summary>
    /// An origin that fails until marked healthy, and can hold one request so a test can look at the system
    /// while it is in flight.
    /// </summary>
    private sealed class ScriptedOrigin
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _healthy;
        private int _holdNext;

        public ScriptedOrigin() =>
            Handler = new RecordingHandler(async (request, _, cancellationToken) =>
            {
                if (Interlocked.Exchange(ref _holdNext, 0) == 1)
                {
                    await _gate.Task.WaitAsync(cancellationToken);
                }

                return new HttpResponseMessage(_healthy ? HttpStatusCode.OK : HttpStatusCode.InternalServerError)
                {
                    RequestMessage = request
                };
            });

        public RecordingHandler Handler { get; }

        public bool Healthy
        {
            get => _healthy;
            set => _healthy = value;
        }

        /// <summary>The next request waits at the origin until <see cref="Release"/>.</summary>
        public void HoldNext() => Volatile.Write(ref _holdNext, 1);

        public void Release() => _gate.TrySetResult();
    }
}
