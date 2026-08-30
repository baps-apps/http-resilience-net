using System.Net;
using System.Text;
using HttpResilience.NET.Tests.Infrastructure;

namespace HttpResilience.NET.Tests.Behavior;

/// <summary>
/// Hedging must never duplicate a mutating request, including on the path that has nothing to do with the
/// outcome of an attempt.
/// </summary>
/// <remarks>
/// Polly's hedging strategy starts a supplementary attempt for one of two reasons: an attempt completed and
/// <c>ShouldHandle</c> said to keep going, or the hedging delay elapsed while every attempt was still
/// running. Only the first consults <c>ShouldHandle</c>. Every test here therefore uses a <b>slow</b> origin,
/// because an origin that answers immediately never reaches the timer path -- which is how a guard that only
/// covered the outcome path passed a suite of hedging tests while POST bodies arrived four times.
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

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task UnsafeMethods_AreNotHedged_WhenThePrimaryAttemptIsSlow(string method)
    {
        RecordingHandler origin = SlowOrigin();
        await using ResilienceHarness harness = ResilienceHarness.Create(Hedged(), origin, hedged: true);

        using var content = new StringContent("""{"amount":100}""", Encoding.UTF8, "application/json");
        await harness.SendAsync(new HttpMethod(method), content: content);

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
    [Theory]
    [InlineData("MOVE")]
    [InlineData("PROPPATCH")]
    [InlineData("PURGE")]
    public async Task UnrecognisedMethods_AreNotHedged_WhenThePrimaryAttemptIsSlow(string method)
    {
        RecordingHandler origin = SlowOrigin();
        await using ResilienceHarness harness = ResilienceHarness.Create(Hedged(), origin, hedged: true);

        await harness.SendAsync(new HttpMethod(method));

        Assert.Equal(1, harness.Origin.Count);
    }

    /// <summary>
    /// The count alone would pass if the body failed to replay. What must not happen is the origin seeing
    /// the same payload twice.
    /// </summary>
    [Fact]
    public async Task SlowPost_DeliversItsBodyExactlyOnce()
    {
        RecordingHandler origin = SlowOrigin();
        await using ResilienceHarness harness = ResilienceHarness.Create(Hedged(), origin, hedged: true);

        using var content = new StringContent("""{"amount":100}""", Encoding.UTF8, "application/json");
        await harness.SendAsync(HttpMethod.Post, content: content);

        Assert.Equal(["""{"amount":100}"""], harness.Origin.Bodies);
    }

    [Fact]
    public async Task SafeMethods_AreStillHedged_WhenThePrimaryAttemptIsSlow()
    {
        RecordingHandler origin = SlowOrigin();
        await using ResilienceHarness harness = ResilienceHarness.Create(Hedged(), origin, hedged: true);

        await harness.GetAsync();

        // The guard must suppress unsafe methods without disabling the feature it guards.
        Assert.Equal(4, harness.Origin.Count);
    }

    [Fact]
    public async Task OptingOut_StillHedgesUnsafeMethods_WhenThePrimaryAttemptIsSlow()
    {
        RecordingHandler origin = SlowOrigin();
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Hedged().ForClient("test", "Hedging:DisableForUnsafeHttpMethods", "false"), origin, hedged: true);

        using var content = new StringContent("x", Encoding.UTF8, "text/plain");
        await harness.SendAsync(HttpMethod.Post, content: content);

        Assert.Equal(4, harness.Origin.Count);
    }

    /// <summary>
    /// Turning the guard <b>on</b> after registration protects the client, rather than being reported and
    /// doing nothing.
    /// </summary>
    /// <remarks>
    /// The guard used to be registered only when the option was already true, so a client configured with it
    /// false and switched on later kept hedging POSTs while its options said it did not. The guard is now
    /// registered unconditionally and consults the option when an attempt is considered, so the only thing
    /// that can switch it off is the option itself.
    /// <para>
    /// Fails if <c>SuppressUnsafeHedgedAttempts</c> goes back to being registered conditionally: the origin
    /// sees four POST bodies instead of one.
    /// </para>
    /// </remarks>
    [Fact]
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
        await harness.SendAsync(HttpMethod.Post, content: content);

        Assert.Equal(1, harness.Origin.Count);
    }

    /// <summary>
    /// And turning it off after registration still opts back in, so the option is a value like any other.
    /// </summary>
    [Fact]
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
        await harness.SendAsync(HttpMethod.Post, content: content);

        Assert.Equal(4, harness.Origin.Count);
    }

    /// <summary>
    /// A zero delay issues every attempt at once, so the timer path fires immediately rather than after a
    /// wait. The guard has to hold there too.
    /// </summary>
    [Fact]
    public async Task ZeroDelay_DoesNotHedgeAnUnsafeMethod_WhenThePrimaryAttemptIsSlow()
    {
        RecordingHandler origin = SlowOrigin();
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Hedged().Set("Hedging:Delay", "00:00:00").Set("Hedging:MaxHedgedAttempts", "3"),
            origin, hedged: true);

        using var content = new StringContent("x", Encoding.UTF8, "text/plain");
        await harness.SendAsync(HttpMethod.Put, content: content);

        Assert.Equal(1, harness.Origin.Count);
    }
}
