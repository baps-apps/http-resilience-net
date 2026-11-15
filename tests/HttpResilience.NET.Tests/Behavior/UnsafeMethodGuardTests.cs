using System.Diagnostics.Metrics;
using System.Net.Http.Headers;
using System.Net;
using System.Threading.RateLimiting;
using BclConcurrencyLimiterOptions = System.Threading.RateLimiting.ConcurrencyLimiterOptions;
using HttpResilience.NET.Internal;
using HttpResilience.NET.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.RateLimiting;
using Polly.Timeout;

namespace HttpResilience.NET.Tests.Behavior;

/// <summary>
/// The unsafe-method guards cannot be switched off for every client at once, and cannot contradict an allow-list.
/// </summary>
public class UnsafeMethodGuardTests
{
    private static IConfigurationRoot Configuration(Settings settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings.Build()).Build();

    /// <summary>
    /// Neither guard may be switched off at the root, where one key decides it for every client in the
    /// process -- including clients registered after it that state nothing.
    /// </summary>
    /// <remarks>
    /// Enforced at registration, not only by the options validator: root options are validated only when
    /// something invokes <c>IStartupValidator</c>, which a bare <see cref="ServiceCollection"/> does not.
    /// Fails if the check moves out of <c>AddHttpResilience</c>.
    /// </remarks>
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData("Retry:DisableForUnsafeHttpMethods")]
    [InlineData("Hedging:DisableForUnsafeHttpMethods")]
    public void ASafeMethodGuardSwitchedOffAtTheRoot_FailsAtRegistration(string key)
    {
        var services = new ServiceCollection();

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(
            () => services.AddHttpResilience(Configuration(Settings.Enabled().Set(key, "false"))));

        string message = string.Join(" ", exception.Failures);
        Assert.Contains($"HttpResilience:{key}", message, StringComparison.Ordinal);
        Assert.Contains("HttpResilience:Clients:{name}", message, StringComparison.Ordinal);
        Assert.Contains("idempotency handling", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// It fails before any client is registered.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public void TheRootGuardFails_EvenWithNoClientsAndNoHost()
    {
        var services = new ServiceCollection();

        Assert.Throws<OptionsValidationException>(() => services.AddHttpResilience(
            Configuration(Settings.Empty().Set("Retry:DisableForUnsafeHttpMethods", "false"))));

        // Nothing was left half-registered for a later call to build on.
        Assert.Empty(services);
    }

    /// <summary>
    /// The per-client decision is still expressible, for an endpoint that deduplicates on an idempotency key.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task TheSameGuard_InAClientSection_IsAccepted()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled().ForClient("test", "Retry:DisableForUnsafeHttpMethods", "false"));

        await harness.SendAsync(HttpMethod.Post, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, harness.Origin.Count);
    }

    /// <summary>
    /// A root-level allow-list may <b>narrow</b> what every client retries.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ARootLevelAllowListOfSafeMethods_IsStillAccepted()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled().Set("Retry:RetryableMethods:0", "GET"));

        await harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(3, harness.Origin.Count);

        // And it is in force rather than decorative: OPTIONS is safe, but it is not on the list.
        await harness.SendAsync(HttpMethod.Options, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(4, harness.Origin.Count);
    }

    /// <summary>
    /// A root-level allow-list may not <b>widen</b>: an unsafe entry there would reach every standard client.
    /// </summary>
    /// <remarks>
    /// Fails at <b>registration</b>, like the flag guards, because a bare <see cref="ServiceCollection"/> never
    /// runs root options validation.
    /// </remarks>
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    [InlineData("PURGE")]
    public void ARootLevelAllowListNamingAnUnsafeMethod_FailsAtRegistration(string method)
    {
        var services = new ServiceCollection();

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(
            () => services.AddHttpResilience(
                Configuration(Settings.Enabled().Set("Retry:RetryableMethods:0", method))));

        string message = string.Join(" ", exception.Failures);
        Assert.Contains("HttpResilience:Retry:RetryableMethods", message, StringComparison.Ordinal);
        Assert.Contains(method, message, StringComparison.Ordinal);
        Assert.Contains("HttpResilience:Clients:{name}", message, StringComparison.Ordinal);

        // And nothing was left half-registered for a later call to build on.
        Assert.Empty(services);
    }

    /// <summary>
    /// The same list in a client's own section is the supported opt-in and still works.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task TheSameAllowList_InAClientSection_IsAccepted()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled().ForClient("test", "Retry:RetryableMethods:0", "POST"));

        await harness.SendAsync(HttpMethod.Post, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, harness.Origin.Count);
    }

    /// <summary>
    /// A client stating the safety guard while an allow-list is in force has written a statement that is not
    /// obeyed, and startup fails.
    /// </summary>
    /// <remarks>
    /// The allow-list replaces the guard in <c>StandardPipelineConfigurator.ConfigureRetry</c>. Statedness is
    /// read from the raw section, because the bound flag defaults to <c>true</c> and cannot be told from
    /// silence. Fails if <c>CollectInertConfiguration</c> stops reading the raw section.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public void AClientGuardBesideAnAllowListInForce_FailsAtStartup()
    {
        var services = new ServiceCollection();
        services.AddHttpResilience(Configuration(Settings.Enabled()
            .ForClient("orders", "Retry:RetryableMethods:0", "POST")
            .ForClient("orders", "Retry:DisableForUnsafeHttpMethods", "true")));

        OptionsValidationException exception = StartupAssert.Fails(services, () => services.AddHttpClient("orders").AddHttpResilienceHandler());

        string message = string.Join(" ", exception.Failures);
        Assert.Contains(
            "HttpResilience:Clients:orders -- Retry:DisableForUnsafeHttpMethods", message, StringComparison.Ordinal);
        Assert.Contains("bound and never read", message, StringComparison.Ordinal);
        Assert.Contains("the methods actually retried are POST", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The guard on its own, with no list in force, is left alone.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task AClientGuardWithNoAllowList_IsAccepted()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled().ForClient("test", "Retry:DisableForUnsafeHttpMethods", "true"));

        await harness.SendAsync(HttpMethod.Post, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, harness.Origin.Count);
    }

    /// <summary>
    /// An allow-list wins outright in the pipeline, so the flag beside it is bound and never read.
    /// </summary>
    /// <remarks>
    /// Same class of inert configuration as <c>Authorities</c> under <c>Mode: None</c>.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public void AnAllowListBesideTheDisabledGuard_FailsAtStartup()
    {
        var services = new ServiceCollection();
        services.AddHttpResilience(Configuration(Settings.Enabled()
            .ForClient("orders", "Retry:RetryableMethods:0", "POST")
            .ForClient("orders", "Retry:DisableForUnsafeHttpMethods", "false")));

        OptionsValidationException exception = StartupAssert.Fails(services, () => services.AddHttpClient("orders").AddHttpResilienceHandler());

        string message = string.Join(" ", exception.Failures);
        Assert.Contains("Retry:DisableForUnsafeHttpMethods", message, StringComparison.Ordinal);
        Assert.Contains("bound and never read", message, StringComparison.Ordinal);
    }
}
