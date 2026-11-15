using System.Net;
using System.Text;
using HttpResilience.NET.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HttpResilience.NET.Tests.Behavior;

/// <summary>
/// Hedging must never duplicate a mutating request, including on the path that has nothing to do with the
/// outcome of an attempt.
/// </summary>
/// <remarks>
/// Polly starts a hedged attempt when an attempt fails and <c>ShouldHandle</c> says to continue, or when the
/// hedging delay elapses while attempts are still running. Only the first consults <c>ShouldHandle</c>, so
/// every test here uses a <b>slow</b> origin to reach the timer path.
/// </remarks>
public class HedgingSafetyTests
{
    private static RecordingHandler SlowOrigin(TimeSpan? delay = null) =>
        new(async (request, _, cancellationToken) =>
        {
            await Task.Delay(delay ?? TimeSpan.FromSeconds(2), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };
        });

    private static Settings Hedged() => Settings.Hedged()
        .Set("Hedging:Delay", "00:00:00.100")
        .Set("Hedging:MaxHedgedAttempts", "3");

    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task UnsafeMethods_AreNotHedged_WhenThePrimaryAttemptIsSlow(string method)
    {
        RecordingHandler origin = SlowOrigin();
        await using ResilienceHarness harness = ResilienceHarness.Create(Hedged(), origin, hedged: true);

        using var content = new StringContent("""{"amount":100}""", Encoding.UTF8, "application/json");
        await harness.SendAsync(new HttpMethod(method), content: content, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, harness.Origin.Count);
    }

    /// <summary>
    /// A method this package has never heard of is not known to be idempotent, and hedged attempts are
    /// simultaneous, so it must not be raced either.
    /// </summary>
    /// <remarks>
    /// Fails if either hedging guard goes back to a five-verb deny-list. The <c>ActionGenerator</c> guard is
    /// the one this exercises: a slow origin never reaches the outcome predicate.
    /// </remarks>
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData("MOVE")]
    [InlineData("PROPPATCH")]
    [InlineData("PURGE")]
    public async Task UnrecognisedMethods_AreNotHedged_WhenThePrimaryAttemptIsSlow(string method)
    {
        RecordingHandler origin = SlowOrigin();
        await using ResilienceHarness harness = ResilienceHarness.Create(Hedged(), origin, hedged: true);

        await harness.SendAsync(new HttpMethod(method), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, harness.Origin.Count);
    }

    /// <summary>
    /// The count alone would pass if the body failed to replay. What must not happen is the origin seeing
    /// the same payload twice.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task SlowPost_DeliversItsBodyExactlyOnce()
    {
        RecordingHandler origin = SlowOrigin();
        await using ResilienceHarness harness = ResilienceHarness.Create(Hedged(), origin, hedged: true);

        using var content = new StringContent("""{"amount":100}""", Encoding.UTF8, "application/json");
        await harness.SendAsync(HttpMethod.Post, content: content, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["""{"amount":100}"""], harness.Origin.Bodies);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task SafeMethods_AreStillHedged_WhenThePrimaryAttemptIsSlow()
    {
        RecordingHandler origin = SlowOrigin();
        await using ResilienceHarness harness = ResilienceHarness.Create(Hedged(), origin, hedged: true);

        await harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken);

        // The guard must suppress unsafe methods without disabling the feature it guards.
        Assert.Equal(4, harness.Origin.Count);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task OptingOut_StillHedgesUnsafeMethods_WhenThePrimaryAttemptIsSlow()
    {
        RecordingHandler origin = SlowOrigin();
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Hedged().ForClient("test", "Hedging:DisableForUnsafeHttpMethods", "false"), origin, hedged: true);

        using var content = new StringContent("x", Encoding.UTF8, "text/plain");
        await harness.SendAsync(HttpMethod.Post, content: content, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(4, harness.Origin.Count);
    }

    /// <summary>
    /// Turning the guard <b>on</b> after registration protects the client, rather than being reported and
    /// doing nothing.
    /// </summary>
    /// <remarks>
    /// The guard is registered unconditionally and reads the option when the pipeline is built. Fails if
    /// <c>SuppressUnsafeHedgedAttempts</c> is registered conditionally: the origin sees four POST bodies.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task EnablingTheGuardAfterRegistration_ActuallySuppressesHedgedAttempts()
    {
        RecordingHandler origin = SlowOrigin();

        await using ResilienceHarness harness = ResilienceHarness.Create(
            Hedged().ForClient("test", "Hedging:DisableForUnsafeHttpMethods", "false"),
            origin,
            hedged: true,
            configure: null,
            postConfigure: options => options.Hedging.DisableForUnsafeHttpMethods = true);

        using var content = new StringContent("x", Encoding.UTF8, "text/plain");
        await harness.SendAsync(HttpMethod.Post, content: content, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, harness.Origin.Count);
    }

    /// <summary>
    /// And turning it off after registration still opts back in, so the option is a value like any other.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task DisablingTheGuardAfterRegistration_HedgesAgain()
    {
        RecordingHandler origin = SlowOrigin();

        await using ResilienceHarness harness = ResilienceHarness.Create(
            Hedged(),
            origin,
            hedged: true,
            configure: null,
            postConfigure: options => options.Hedging.DisableForUnsafeHttpMethods = false);

        using var content = new StringContent("x", Encoding.UTF8, "text/plain");
        await harness.SendAsync(HttpMethod.Post, content: content, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(4, harness.Origin.Count);
    }

    /// <summary>
    /// Options are restart-only, and the guard is part of that: it is fixed when the pipeline is built, so code
    /// that later writes to the monitor's cached instance cannot turn on hedging of a mutating request without
    /// validation.
    /// </summary>
    /// <remarks>
    /// The GET builds the pipeline for the authority; only the POST is slow. Fails if the guard reads the
    /// options per hedge: the origin sees the GET plus four POSTs.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task MutatingTheOptionsAfterThePipelineIsBuilt_DoesNotTurnOffTheGuard()
    {
        var origin = new RecordingHandler(async (request, _, cancellationToken) =>
        {
            if (request.Method != HttpMethod.Get)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };
        });
        await using ResilienceHarness harness = ResilienceHarness.Create(Hedged(), origin, hedged: true);

        (await harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken)).Dispose();
        harness.Services.GetRequiredService<IOptionsMonitor<HttpResilienceOptions>>()
            .Get("test").Hedging.DisableForUnsafeHttpMethods = false;

        using var content = new StringContent("x", Encoding.UTF8, "text/plain");
        await harness.SendAsync(HttpMethod.Post, content: content, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["GET", "POST"], harness.Origin.Methods);
    }

    /// <summary>
    /// A zero delay issues every attempt at once, so the timer path fires immediately rather than after a
    /// wait. The guard has to hold there too.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ZeroDelay_DoesNotHedgeAnUnsafeMethod_WhenThePrimaryAttemptIsSlow()
    {
        RecordingHandler origin = SlowOrigin();
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Hedged().Set("Hedging:Delay", "00:00:00").Set("Hedging:MaxHedgedAttempts", "3"),
            origin, hedged: true);

        using var content = new StringContent("x", Encoding.UTF8, "text/plain");
        await harness.SendAsync(HttpMethod.Put, content: content, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, harness.Origin.Count);
    }
}
