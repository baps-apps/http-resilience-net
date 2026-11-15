using System.Net;
using HttpResilience.NET.Tests.Infrastructure;
using Microsoft.Extensions.Options;

namespace HttpResilience.NET.Tests.Behavior;

/// <summary>
/// <c>Retry:MaxRetries</c> counts retries after the first attempt, and the key says so.
/// </summary>
/// <remarks>
/// <c>MaxRetries: 2</c> sends three requests. The old key <c>MaxAttempts</c> is read from the raw section, so a file using
/// it fails startup instead of being ignored or silently aliased.
/// </remarks>
public class RetryNamingTests
{
    /// <summary>
    /// Two retries after the first attempt is three origin calls.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task MaxRetriesOfTwo_MakesThreeOriginCalls()
    {
        var origin = new RecordingHandler(HttpStatusCode.InternalServerError);
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled().Set("Retry:MaxRetries", "2"), origin);

        (await harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken)).Dispose();

        Assert.Equal(3, origin.Count);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task MaxRetriesOfZeroIsRejected_BecauseRetryEnabledIsTheOffSwitch()
    {
        ServiceProvider_Throws(Settings.Enabled().Set("Retry:MaxRetries", "0"), out string message);

        Assert.Contains("Retry:MaxRetries", message, StringComparison.Ordinal);
        Assert.Contains("Retry:Enabled", message, StringComparison.Ordinal);
        await Task.CompletedTask;
    }

    /// <summary>
    /// Fails if <c>RenamedKeyRules</c> stops checking the raw section: the old key would bind to nothing and
    /// the client would run on the default retry count.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public void TheOldMaxAttemptsKey_FailsStartup_AndNamesItsReplacement()
    {
        ServiceProvider_Throws(
            Settings.Enabled().Set("Retry:MaxAttempts", "2"), out string message);

        Assert.Contains("Retry:MaxAttempts", message, StringComparison.Ordinal);
        Assert.Contains("MaxRetries", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The old key is refused in a client section too, where a per-client retry count is most likely.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public void TheOldMaxAttemptsKeyOnAClientSection_FailsStartup()
    {
        ServiceProvider_Throws(
            Settings.Enabled().ForClient("test", "Retry:MaxAttempts", "3"),
            out string message,
            sectionName: "test");

        Assert.Contains("Retry:MaxAttempts", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Refused even when retries are off, so the operator learns the key was renamed.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public void TheOldMaxAttemptsKey_IsRefusedEvenWhenRetriesAreDisabled()
    {
        ServiceProvider_Throws(
            Settings.Enabled().Set("Retry:Enabled", "false").Set("Retry:MaxAttempts", "2"),
            out string message);

        Assert.Contains("Retry:MaxAttempts", message, StringComparison.Ordinal);
    }

    private static void ServiceProvider_Throws(Settings settings, out string message, string? sectionName = null)
    {
        OptionsValidationException exception = StartupAssert.Fails(() => ResilienceHarness.BuildProvider(settings, sectionName: sectionName));

        message = string.Join(" ", exception.Failures);
    }
}
