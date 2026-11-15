using System.Net;
using System.Text;
using HttpResilience.NET.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HttpResilience.NET.Tests.Behavior;

/// <summary>
/// A client section is layered on top of the root, but a client's <b>list</b> replaces the root's.
/// </summary>
/// <remarks>
/// The configuration binder appends to an existing collection, which would let a client widen an inherited
/// allow-list but never narrow it. Tests assert on delivered requests, not on the bound value.
/// </remarks>
public class ConfigurationInheritanceTests
{
    /// <summary>
    /// Fails if <c>Bind</c> stops clearing <c>Retry:RetryableMethods</c> before binding the client section:
    /// the root's HEAD entry survives, and a client that named only GET goes on retrying HEAD.
    /// </summary>
    /// <remarks>
    /// A root list may only name safe methods (see
    /// <c>ARootLevelAllowListNamingAnUnsafeMethod_FailsAtRegistration</c>). This test covers the mechanism: a
    /// client section replaces an inherited list rather than unioning with it.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ClientRetryableMethods_ReplaceTheRootList_RatherThanAddingToIt()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled()
                .Set("Retry:RetryableMethods:0", "GET")
                .Set("Retry:RetryableMethods:1", "HEAD")
                .ForClient("test", "Retry:RetryableMethods:0", "GET"));

        await harness.SendAsync(HttpMethod.Head, cancellationToken: TestContext.Current.CancellationToken);

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
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ClientWithoutItsOwnList_StillInheritsTheRootRetryableMethods()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled().Set("Retry:RetryableMethods:0", "HEAD"));

        await harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken);

        // GET is not on the inherited list, so it is not retried. Three calls would mean the client never
        // saw the root's list at all.
        Assert.Equal(1, harness.Origin.Count);

        await harness.SendAsync(HttpMethod.Head, cancellationToken: TestContext.Current.CancellationToken);

        // And what the list does name is still retried, so the list is in force rather than merely present.
        Assert.Equal(4, harness.Origin.Count);
    }

    /// <summary>
    /// A client under an inherited list returns to the default safe guard by stating an empty list.
    /// </summary>
    /// <remarks>
    /// <remarks>
    /// <c>ResetListsStatedBy</c> clears the inherited list, and an empty list means "no allow-list", not "no
    /// retries": GET is retried by the safe-method guard, POST is not. Fails if the empty list is treated as
    /// an error or unions with the root's.
    /// </remarks>
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ClientStatingAnEmptyList_ReturnsToTheDefaultSafeMethodGuard()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled()
                .Set("Retry:RetryableMethods:0", "HEAD")
                .ForClient("test", "Retry:RetryableMethods", string.Empty));

        await harness.SendAsync(HttpMethod.Head, cancellationToken: TestContext.Current.CancellationToken);

        // Not the inherited list any more: HEAD is retried because it is safe, not because it was named.
        Assert.Equal(3, harness.Origin.Count);

        using var content = new StringContent("x", Encoding.UTF8, "text/plain");
        await harness.SendAsync(HttpMethod.Post, content: content, cancellationToken: TestContext.Current.CancellationToken);

        // And the guard the empty list restored is the real one.
        Assert.Equal(4, harness.Origin.Count);
    }

    /// <summary>
    /// A hedged client's allow-list bounds its destinations, so an entry only the root states must not widen it.
    /// </summary>
    /// <remarks>
    /// Fails if the authorities list is unioned: the root-only authority would be admitted.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ClientAuthorities_ReplaceTheRootList_SoAHedgedClientCannotInheritADestination()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled()
                .Set("PipelineSelection:Authorities:0", "http://inherited.test")
                .ForClient("test", "PipelineSelection:Authorities:0", "http://origin.test"),
            hedged: true);

        InvalidOperationException rejected = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.GetAsync("http://inherited.test/x", TestContext.Current.CancellationToken));

        Assert.Contains("http://inherited.test", rejected.Message, StringComparison.Ordinal);

        // The authority the client did list is still reachable.
        HttpResponseMessage response = await harness.GetAsync("http://origin.test/x", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    /// <summary>
    /// A hedged client that states no authorities inherits the root's, so a fleet-wide list is one entry.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task HedgedClientWithoutItsOwnList_StillInheritsTheRootAuthorities()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled().Set("PipelineSelection:Authorities:0", "http://origin.test"),
            hedged: true);

        HttpResponseMessage response = await harness.GetAsync("http://origin.test/x", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    /// <summary>
    /// A root authority list is inherited by every client, so it must not fail a standard client.
    /// </summary>
    /// <remarks>
    /// The inert-list rule reads the client's own section, not the bound value, which includes the root.
    /// Fails if the rule reads the bound value.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ARootAuthorityList_DoesNotFailAStandardClient()
    {
        var services = new ServiceCollection();
        services.AddHttpResilience(new ConfigurationBuilder()
            .AddInMemoryCollection(
                Settings.Enabled().Set("PipelineSelection:Authorities:0", "http://origin.test").Build())
            .Build());

        // One of each, the usual shape of a service with a hedged read path.
        services.AddHttpClient("hedged").AddHttpResilienceHedgingHandler();

        var origin = new RecordingHandler(HttpStatusCode.OK);
        services.AddHttpClient("standard").AddHttpResilienceHandler()
            .ConfigurePrimaryHttpMessageHandler(() => origin);

        await using ServiceProvider provider = services.BuildServiceProvider();
        provider.GetRequiredService<IStartupValidator>().Validate();

        (await provider.GetRequiredService<IHttpClientFactory>().CreateClient("standard")
            .GetAsync("http://origin.test/x", TestContext.Current.CancellationToken)).Dispose();

        // The standard client runs on one shared pipeline, as Mode: None says.
        Assert.Equal(1, origin.Count);
    }

    /// <summary>
    /// A client that states an authority list of its own under <c>Mode: None</c> still fails, because that is
    /// a written statement with no effect.
    /// </summary>
    /// <remarks>
    /// Fails if the check is dropped rather than scoped to the client's section.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public void AClientStatingItsOwnAuthorityListUnderModeNone_StillFails()
    {
        OptionsValidationException failure = StartupAssert.Fails(() => ResilienceHarness.BuildProvider(
                Settings.Enabled().ForClient("test", "PipelineSelection:Authorities:0", "http://origin.test")));

        Assert.Contains(
            "HttpResilience:Clients:test -- PipelineSelection:Authorities",
            string.Join(" ", failure.Failures),
            StringComparison.Ordinal);
    }
}
