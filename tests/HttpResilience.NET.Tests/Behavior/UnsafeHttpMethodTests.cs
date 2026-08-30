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
    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task UnsafeMethods_AreNotRetried_ByDefault(string method)
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(Settings.Enabled());

        HttpResponseMessage response = await harness.SendAsync(new HttpMethod(method));

        Assert.Equal(1, harness.Origin.Count);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    [InlineData("TRACE")]
    public async Task SafeMethods_AreStillRetried(string method)
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(Settings.Enabled());

        await harness.SendAsync(new HttpMethod(method));

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
    [Theory]
    [InlineData("MOVE")]
    [InlineData("MKCOL")]
    [InlineData("PROPPATCH")]
    [InlineData("PURGE")]
    [InlineData("MERGE")]
    public async Task UnrecognisedMethods_AreNotRetried_ByDefault(string method)
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(Settings.Enabled());

        await harness.SendAsync(new HttpMethod(method));

        Assert.Equal(1, harness.Origin.Count);
    }

    /// <summary>
    /// The explicit allow-list stays the supported opt-in, and it has to work for a non-standard verb --
    /// otherwise inverting the default guard would leave no way to retry one at all.
    /// </summary>
    [Fact]
    public async Task UnrecognisedMethod_IsRetried_WhenNamedInRetryableMethods()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled().ForClient("test", "Retry:RetryableMethods:0", "PURGE"));

        await harness.SendAsync(new HttpMethod("PURGE"));

        Assert.Equal(3, harness.Origin.Count);
    }

    [Fact]
    public async Task PostBody_IsDeliveredExactlyOnce()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(Settings.Enabled());

        await harness.SendAsync(
            HttpMethod.Post,
            content: new StringContent("""{"amount":100}""", Encoding.UTF8, "application/json"));

        Assert.Equal(["""{"amount":100}"""], harness.Origin.Bodies);
    }

    [Fact]
    public async Task UnsafeMethods_AreNotHedged_ByDefault()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Hedged()
                .Set("Hedging:Delay", "00:00:00")
                .Set("Hedging:MaxHedgedAttempts", "3"),
            hedged: true);

        await harness.SendAsync(
            HttpMethod.Post,
            content: new StringContent("""{"amount":100}""", Encoding.UTF8, "application/json"));

        Assert.Equal(1, harness.Origin.Count);
        Assert.Equal(["""{"amount":100}"""], harness.Origin.Bodies);
    }

    [Fact]
    public async Task SafeMethods_AreStillHedged()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Hedged()
                .Set("Hedging:Delay", "00:00:00")
                .Set("Hedging:MaxHedgedAttempts", "2"),
            hedged: true);

        await harness.GetAsync();

        Assert.Equal(3, harness.Origin.Count);
    }

    [Fact]
    public async Task RetryableMethods_OptsInExplicitly_AndExcludesEverythingElse()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled().ForClient("test", "Retry:RetryableMethods:0", "POST"));

        await harness.SendAsync(HttpMethod.Post);
        Assert.Equal(3, harness.Origin.Count);

        // The allow-list replaces the default guard entirely: GET is not on it, so GET is not retried.
        await harness.GetAsync();
        Assert.Equal(4, harness.Origin.Count);
    }

    [Fact]
    public async Task DisableForUnsafeHttpMethods_False_AllowsUnsafeRetries()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled().ForClient("test", "Retry:DisableForUnsafeHttpMethods", "false"));

        await harness.SendAsync(HttpMethod.Post);

        Assert.Equal(3, harness.Origin.Count);
    }

    [Fact]
    public async Task DisableForUnsafeHttpMethods_False_AllowsHedgingUnsafeMethods()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Hedged()
                .Set("Hedging:Delay", "00:00:00")
                .Set("Hedging:MaxHedgedAttempts", "2")
                .ForClient("test", "Hedging:DisableForUnsafeHttpMethods", "false"),
            hedged: true);

        await harness.SendAsync(HttpMethod.Post);

        Assert.Equal(3, harness.Origin.Count);
    }
}
