using System.Net;
using System.Text;
using HttpResilience.NET.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HttpResilience.NET.Tests.Behavior;

/// <summary>
/// A client section is layered on top of the root, and what "layered" means for a <b>list</b> is not what it
/// means for a scalar.
/// </summary>
/// <remarks>
/// <c>Microsoft.Extensions.Configuration</c>'s binder adds to a non-null collection rather than replacing it,
/// so binding the root and then the client section onto one options instance <i>unions</i> every list. That
/// is the unsafe direction for both lists this schema has: a client could widen an inherited allow-list but
/// never narrow one, so a section that reads as a restriction is not one.
/// <para>
/// Every test here asserts on delivered requests rather than on the bound value, because the bound value is
/// what a reader would check and the origin call count is what an operator experiences.
/// </para>
/// </remarks>
public class ConfigurationInheritanceTests
{
    /// <summary>
    /// Fails if <c>Bind</c> stops clearing <c>Retry:RetryableMethods</c> before binding the client section:
    /// the root's HEAD entry survives, and a client that named only GET goes on retrying HEAD.
    /// </summary>
    /// <remarks>
    /// The root list is safe-only because an unsafe entry there is now refused outright -- a root list may
    /// narrow what is retried but never widen it. That removes the original failure this test described (a
    /// client naming GET under a root naming POST, still retrying POST bodies) as a reachable state, and it
    /// is <c>ARootLevelAllowListNamingAnUnsafeMethod_FailsAtRegistration</c> that now holds that line. What
    /// is left here is the mechanism underneath it: a client section <b>replaces</b> an inherited list rather
    /// than unioning with it, which is what the binder does not do on its own.
    /// </remarks>
    [Fact]
    public async Task ClientRetryableMethods_ReplaceTheRootList_RatherThanAddingToIt()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled()
                .Set("Retry:RetryableMethods:0", "GET")
                .Set("Retry:RetryableMethods:1", "HEAD")
                .ForClient("test", "Retry:RetryableMethods:0", "GET"));

        await harness.SendAsync(HttpMethod.Head);

        // The client narrowed the list to GET. A retried HEAD means the root's entry survived the override.
        Assert.Equal(1, harness.Origin.Count);
    }

    /// <summary>
    /// The opposite direction still has to work: a client that states no list of its own inherits the root's.
    /// </summary>
    /// <remarks>
    /// The inherited list is <c>["HEAD"]</c> rather than the default safe set, so the assertion discriminates:
    /// a client that failed to inherit it would fall back to the default guard and retry the GET.
    /// </remarks>
    [Fact]
    public async Task ClientWithoutItsOwnList_StillInheritsTheRootRetryableMethods()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled().Set("Retry:RetryableMethods:0", "HEAD"));

        await harness.GetAsync();

        // GET is not on the inherited list, so it is not retried. Three calls would mean the client never
        // saw the root's list at all.
        Assert.Equal(1, harness.Origin.Count);

        await harness.SendAsync(HttpMethod.Head);

        // And what the list does name is still retried, so the list is in force rather than merely present.
        Assert.Equal(4, harness.Origin.Count);
    }

    /// <summary>
    /// A client under a permissive inherited list can return to the default safe guard by stating an empty
    /// list of its own -- the documented opt-out, and the one narrowing move a client could not otherwise
    /// make.
    /// </summary>
    /// <remarks>
    /// The client section states the key, so <c>ResetListsStatedBy</c> clears the inherited list before
    /// binding, and an empty list means "no allow-list" rather than "no retries": GET is still retried by the
    /// safe-method guard, and POST is not. Fails if the empty list starts being treated as an error again, or
    /// starts unioning with the root's.
    /// </remarks>
    [Fact]
    public async Task ClientStatingAnEmptyList_ReturnsToTheDefaultSafeMethodGuard()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled()
                .Set("Retry:RetryableMethods:0", "HEAD")
                .ForClient("test", "Retry:RetryableMethods", string.Empty));

        await harness.SendAsync(HttpMethod.Head);

        // Not the inherited list any more: HEAD is retried because it is safe, not because it was named.
        Assert.Equal(3, harness.Origin.Count);

        using var content = new StringContent("x", Encoding.UTF8, "text/plain");
        await harness.SendAsync(HttpMethod.Post, content: content);

        // And the guard the empty list restored is the real one.
        Assert.Equal(4, harness.Origin.Count);
    }

    /// <summary>
    /// A hedged client's allow-list bounds the destinations it may reach, so inheriting an entry the client's
    /// own section does not state widens a security control by accident.
    /// </summary>
    /// <remarks>
    /// Fails if the authorities list goes back to being unioned: the request to the root-only authority is
    /// admitted instead of rejected.
    /// </remarks>
    [Fact]
    public async Task ClientAuthorities_ReplaceTheRootList_SoAHedgedClientCannotInheritADestination()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled()
                .Set("PipelineSelection:Authorities:0", "http://inherited.test")
                .ForClient("test", "PipelineSelection:Authorities:0", "http://origin.test"),
            hedged: true);

        HttpRequestException rejected = await Assert.ThrowsAsync<HttpRequestException>(
            () => harness.GetAsync("http://inherited.test/x"));

        Assert.Contains("http://inherited.test", rejected.Message, StringComparison.Ordinal);

        // The authority the client did list is still reachable.
        HttpResponseMessage response = await harness.GetAsync("http://origin.test/x");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    /// <summary>
    /// A hedged client that states no authorities of its own still inherits the root's, which is what makes
    /// a fleet-wide allow-list expressible in one place.
    /// </summary>
    [Fact]
    public async Task HedgedClientWithoutItsOwnList_StillInheritsTheRootAuthorities()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled().Set("PipelineSelection:Authorities:0", "http://origin.test"),
            hedged: true);

        HttpResponseMessage response = await harness.GetAsync("http://origin.test/x");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    /// <summary>
    /// The root authority list above is inherited by <i>every</i> client, so a standard client sharing that
    /// root must not be the thing that fails startup.
    /// </summary>
    /// <remarks>
    /// The mirror of <c>RootHedgingConfiguration_DoesNotFailAStandardClient</c> and
    /// <c>RootRetryConfiguration_DoesNotFailAHedgedClient</c>, and it did not hold. The rule that an
    /// authority list is inert under <c>Mode: None</c> was evaluated against the <i>bound</i> options, which
    /// include everything inherited -- so stating the fleet-wide list the test above depends on made every
    /// standard client in the same process fail registration, and the message named the standard client's own
    /// section rather than the root where the list actually is.
    /// <para>
    /// Two documented features, individually tested, that could not be used together: no existing test
    /// registered both kinds of client. Fails if the rule goes back to reading the bound value instead of the
    /// client's own section.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ARootAuthorityList_DoesNotFailAStandardClient()
    {
        var services = new ServiceCollection();
        services.AddHttpResilience(new ConfigurationBuilder()
            .AddInMemoryCollection(
                Settings.Enabled().Set("PipelineSelection:Authorities:0", "http://origin.test").Build())
            .Build());

        // One of each, which is the ordinary shape of a service with a hedged read path: the list is for the
        // hedged client, and the standard client inherits it because inheritance is not selective.
        services.AddHttpClient("hedged").AddHedgedHttpResilience();

        var origin = new RecordingHandler(HttpStatusCode.OK);
        services.AddHttpClient("standard").AddHttpResilience()
            .ConfigurePrimaryHttpMessageHandler(() => origin);

        await using ServiceProvider provider = services.BuildServiceProvider();
        provider.GetRequiredService<IStartupValidator>().Validate();

        (await provider.GetRequiredService<IHttpClientFactory>().CreateClient("standard")
            .GetAsync("http://origin.test/x")).Dispose();

        // The standard client runs, and the inherited list did nothing to it -- one shared pipeline, as
        // Mode: None says.
        Assert.Equal(1, origin.Count);
    }

    /// <summary>
    /// A client that states an authority list of its own under <c>Mode: None</c> still fails, because that is
    /// a written statement with no effect.
    /// </summary>
    /// <remarks>
    /// The half of the rule worth keeping. Fails if moving the check to the client's own section dropped it
    /// rather than narrowing it.
    /// </remarks>
    [Fact]
    public void AClientStatingItsOwnAuthorityListUnderModeNone_StillFails()
    {
        OptionsValidationException failure = Assert.Throws<OptionsValidationException>(() =>
            ResilienceHarness.BuildProvider(
                Settings.Enabled().ForClient("test", "PipelineSelection:Authorities:0", "http://origin.test")));

        Assert.Contains(
            "HttpResilience:Clients:test -- PipelineSelection:Authorities",
            string.Join(" ", failure.Failures),
            StringComparison.Ordinal);
    }
}
