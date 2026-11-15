using System.Net;
using System.Text;
using HttpResilience.NET.Tests.Infrastructure;

namespace HttpResilience.NET.Tests.Behavior;

/// <summary>
/// A retried or hedged non-idempotent request delivers the same body to the origin more than once, which is
/// how duplicate payments and duplicate writes happen. These are the highest-value assertions in the suite.
/// </summary>
public class UnsafeHttpMethodTests
{
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task UnsafeMethods_AreNotRetried_ByDefault(string method)
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(Settings.Enabled());

        HttpResponseMessage response = await harness.SendAsync(new HttpMethod(method), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, harness.Origin.Count);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    [InlineData("TRACE")]
    public async Task SafeMethods_AreStillRetried(string method)
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(Settings.Enabled());

        await harness.SendAsync(new HttpMethod(method), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, harness.Origin.Count);
    }

    /// <summary>
    /// A method this package has never heard of is not known to be idempotent, so it is not repeated.
    /// </summary>
    /// <remarks>
    /// Fails if the guard goes back to asking "is this one of POST, PATCH, PUT, DELETE, CONNECT?" and
    /// permitting everything else. Every one of these mutates: MOVE and MKCOL and PROPPATCH are WebDAV
    /// writes, PURGE evicts a cache entry, MERGE commits a changeset.
    /// </remarks>
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData("MOVE")]
    [InlineData("MKCOL")]
    [InlineData("PROPPATCH")]
    [InlineData("PURGE")]
    [InlineData("MERGE")]
    public async Task UnrecognisedMethods_AreNotRetried_ByDefault(string method)
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(Settings.Enabled());

        await harness.SendAsync(new HttpMethod(method), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, harness.Origin.Count);
    }

    /// <summary>
    /// The explicit allow-list stays the supported opt-in, and it has to work for a non-standard verb --
    /// otherwise inverting the default guard would leave no way to retry one at all.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task UnrecognisedMethod_IsRetried_WhenNamedInRetryableMethods()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled().ForClient("test", "Retry:RetryableMethods:0", "PURGE"));

        await harness.SendAsync(new HttpMethod("PURGE"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, harness.Origin.Count);
    }

    /// <summary>
    /// RFC 9110 section 9.1 makes method names case-sensitive, so <c>get</c> is an unknown method, not
    /// <c>GET</c>, and is not repeated.
    /// </summary>
    /// <remarks>Fails if the safe-method list is matched case-insensitively.</remarks>
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData("get")]
    [InlineData("Get")]
    [InlineData("head")]
    [InlineData("options")]
    [InlineData("trace")]
    public async Task ASafeMethodNameInTheWrongCase_IsNotRetried(string method)
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(Settings.Enabled());

        await harness.SendAsync(new HttpMethod(method), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, harness.Origin.Count);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ASafeMethodNameInTheWrongCase_IsNotHedged()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Hedged()
                .Set("Hedging:Delay", "00:00:00")
                .Set("Hedging:MaxHedgedAttempts", "2"),
            hedged: true);

        await harness.SendAsync(new HttpMethod("get"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, harness.Origin.Count);
    }

    /// <summary>
    /// Configuration is hand-written, so its entries are upper-cased: <c>purge</c> there opts in
    /// <c>PURGE</c>. The request is still matched exactly, so a lowercase request method is not opted in.
    /// </summary>
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData("purge", "PURGE", 3)]
    [InlineData("PURGE", "purge", 1)]
    public async Task ConfiguredMethods_IgnoreCase_ButRequestMethodsMatchExactly(
        string configured,
        string sent,
        int expectedOriginCalls)
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled().ForClient("test", "Retry:RetryableMethods:0", configured));

        await harness.SendAsync(new HttpMethod(sent), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expectedOriginCalls, harness.Origin.Count);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task PostBody_IsDeliveredExactlyOnce()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(Settings.Enabled());

        await harness.SendAsync(HttpMethod.Post, content: new StringContent("""{"amount":100}""", Encoding.UTF8, "application/json"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["""{"amount":100}"""], harness.Origin.Bodies);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task UnsafeMethods_AreNotHedged_ByDefault()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Hedged()
                .Set("Hedging:Delay", "00:00:00")
                .Set("Hedging:MaxHedgedAttempts", "3"),
            hedged: true);

        await harness.SendAsync(HttpMethod.Post, content: new StringContent("""{"amount":100}""", Encoding.UTF8, "application/json"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, harness.Origin.Count);
        Assert.Equal(["""{"amount":100}"""], harness.Origin.Bodies);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task SafeMethods_AreStillHedged()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Hedged()
                .Set("Hedging:Delay", "00:00:00")
                .Set("Hedging:MaxHedgedAttempts", "2"),
            hedged: true);

        await harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, harness.Origin.Count);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task RetryableMethods_OptsInExplicitly_AndExcludesEverythingElse()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled().ForClient("test", "Retry:RetryableMethods:0", "POST"));

        await harness.SendAsync(HttpMethod.Post, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(3, harness.Origin.Count);

        // The allow-list replaces the default guard entirely: GET is not on it, so GET is not retried.
        await harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(4, harness.Origin.Count);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task DisableForUnsafeHttpMethods_False_AllowsUnsafeRetries()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled().ForClient("test", "Retry:DisableForUnsafeHttpMethods", "false"));

        await harness.SendAsync(HttpMethod.Post, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, harness.Origin.Count);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task DisableForUnsafeHttpMethods_False_AllowsHedgingUnsafeMethods()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Hedged()
                .Set("Hedging:Delay", "00:00:00")
                .Set("Hedging:MaxHedgedAttempts", "2")
                .ForClient("test", "Hedging:DisableForUnsafeHttpMethods", "false"),
            hedged: true);

        await harness.SendAsync(HttpMethod.Post, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, harness.Origin.Count);
    }
}
