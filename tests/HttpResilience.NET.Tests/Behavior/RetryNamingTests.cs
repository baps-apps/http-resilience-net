using System.Net;
using HttpResilience.NET.Tests.Infrastructure;
using Microsoft.Extensions.Options;

namespace HttpResilience.NET.Tests.Behavior;

/// <summary>
/// <c>Retry:MaxRetries</c> counts retries after the first attempt, and the key says so.
/// </summary>
/// <remarks>
/// The key was called <c>MaxAttempts</c> and was assigned to Polly's <c>MaxRetryAttempts</c>, so
/// <c>MaxAttempts: 2</c> sent three requests. Every document in the repository was internally consistent
/// about that, and every operator doing amplification arithmetic from the key name alone was off by one --
/// in the direction that under-counts load on a dependency that is already failing.
/// <para>
/// The old key is still bound, so that a configuration file carrying it fails startup rather than being
/// ignored. Aliasing it instead would have preserved the behavior of anyone who read the name literally and
/// got it wrong, which is the outcome the rename exists to prevent.
/// </para>
/// </remarks>
public class RetryNamingTests
{
    /// <summary>
    /// The arithmetic the key name now states: two retries after the first attempt is three origin calls.
    /// </summary>
    [Fact]
    public async Task MaxRetriesOfTwo_MakesThreeOriginCalls()
    {
        var origin = new RecordingHandler(HttpStatusCode.InternalServerError);
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled().Set("Retry:MaxRetries", "2"), origin);

        (await harness.GetAsync()).Dispose();

        Assert.Equal(3, origin.Count);
    }

    [Fact]
    public async Task MaxRetriesOfZeroIsRejected_BecauseRetryEnabledIsTheOffSwitch()
    {
        ServiceProvider_Throws(Settings.Enabled().Set("Retry:MaxRetries", "0"), out string message);

        Assert.Contains("Retry.MaxRetries", message, StringComparison.Ordinal);
        Assert.Contains("Retry.Enabled", message, StringComparison.Ordinal);
        await Task.CompletedTask;
    }

    /// <summary>
    /// Production change that would make this fail: removing the tombstone property, which would let the old
    /// key bind to nothing and leave the client silently on the default retry count.
    /// </summary>
    [Fact]
    public void TheOldMaxAttemptsKey_FailsStartup_AndNamesItsReplacement()
    {
        ServiceProvider_Throws(
            Settings.Enabled().Set("Retry:MaxAttempts", "2"), out string message);

        Assert.Contains("Retry.MaxAttempts", message, StringComparison.Ordinal);
        Assert.Contains("MaxRetries", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The old key must be refused on a client section too, not only at the root -- that is where a
    /// per-client retry count is most likely to have been written.
    /// </summary>
    [Fact]
    public void TheOldMaxAttemptsKeyOnAClientSection_FailsStartup()
    {
        ServiceProvider_Throws(
            Settings.Enabled().ForClient("test", "Retry:MaxAttempts", "3"),
            out string message,
            sectionName: "test");

        Assert.Contains("Retry.MaxAttempts", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Refused even when retries are switched off: the value is unread either way, but the message an
    /// operator needs is that the key was renamed, not silence.
    /// </summary>
    [Fact]
    public void TheOldMaxAttemptsKey_IsRefusedEvenWhenRetriesAreDisabled()
    {
        ServiceProvider_Throws(
            Settings.Enabled().Set("Retry:Enabled", "false").Set("Retry:MaxAttempts", "2"),
            out string message);

        Assert.Contains("Retry.MaxAttempts", message, StringComparison.Ordinal);
    }

    private static void ServiceProvider_Throws(Settings settings, out string message, string? sectionName = null)
    {
        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(
            () => ResilienceHarness.BuildProvider(settings, sectionName: sectionName));

        message = string.Join(" ", exception.Failures);
    }
}
