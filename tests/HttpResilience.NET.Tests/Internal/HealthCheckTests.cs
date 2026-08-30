using HttpResilience.NET.Internal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace HttpResilience.NET.Tests.Internal;

public class HealthCheckTests
{
    private static async Task<HealthCheckResult> CheckAsync(CircuitBreakerStateTracker tracker) =>
        await new HttpResilienceHealthCheck(tracker).CheckHealthAsync(
            new HealthCheckContext { Registration = new HealthCheckRegistration("x", _ => null!, null, null) });

    [Fact]
    public async Task NoBreakers_IsHealthy()
    {
        Assert.Equal(HealthStatus.Healthy, (await CheckAsync(new CircuitBreakerStateTracker())).Status);
    }

    [Fact]
    public async Task AllClosed_IsHealthy()
    {
        var tracker = new CircuitBreakerStateTracker();
        tracker.Report(new CircuitKey("orders", "https://a.test"), CircuitState.Closed);

        Assert.Equal(HealthStatus.Healthy, (await CheckAsync(tracker)).Status);
    }

    /// <summary>
    /// An open circuit means a downstream is unhealthy, not that this process is. Reporting Unhealthy would
    /// invite an operator to wire it to a probe and shed capacity during a dependency outage.
    /// </summary>
    [Theory]
    [InlineData("Open")]
    [InlineData("HalfOpen")]
    public async Task AnythingOtherThanClosed_IsDegradedAndNeverUnhealthy(string stateName)
    {
        var tracker = new CircuitBreakerStateTracker();
        tracker.Report(new CircuitKey("orders", "https://a.test"), Enum.Parse<CircuitState>(stateName));

        HealthCheckResult result = await CheckAsync(tracker);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("orders", result.Description!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportsEachBreakerSeparately()
    {
        var tracker = new CircuitBreakerStateTracker();
        tracker.Report(new CircuitKey("orders", "https://a.test"), CircuitState.Open);
        tracker.Report(new CircuitKey("orders", "https://b.test"), CircuitState.Closed);

        HealthCheckResult result = await CheckAsync(tracker);

        Assert.Equal(2, result.Data.Count);
        Assert.Equal("Open", result.Data["orders -> https://a.test"]);
        Assert.Equal("Closed", result.Data["orders -> https://b.test"]);
    }

    /// <summary>
    /// One client can own several breakers under per-authority selection. Collapsing them onto the client
    /// name would let a recovering host mask one that is still open.
    /// </summary>
    [Fact]
    public async Task OneHostRecovering_DoesNotMaskAnotherStillOpen()
    {
        var tracker = new CircuitBreakerStateTracker();
        tracker.Report(new CircuitKey("orders", "https://a.test"), CircuitState.Open);
        tracker.Report(new CircuitKey("orders", "https://b.test"), CircuitState.Closed);

        Assert.Equal(HealthStatus.Degraded, (await CheckAsync(tracker)).Status);
    }

    /// <summary>
    /// The check is registered with the <c>dependency</c> tag by default.
    /// </summary>
    /// <remarks>
    /// The tag is what lets an operator route this check to a diagnostic endpoint and exclude it from a
    /// probe. It is a convenience, not the safety mechanism -- see
    /// <see cref="TheDegradedCeilingHolds_WhateverTagsAreUsed"/>.
    /// </remarks>
    [Fact]
    public void DefaultRegistration_CarriesTheDependencyTag()
    {
        HealthCheckRegistration registration = Register();

        Assert.Equal("http-resilience", registration.Name);
        Assert.Contains(HttpResilienceHealthCheckExtensions.DependencyTag, registration.Tags);
        Assert.Equal(HealthStatus.Degraded, registration.FailureStatus);
    }

    /// <summary>
    /// Caller-supplied tags <b>replace</b> the default rather than adding to it.
    /// </summary>
    /// <remarks>
    /// Deliberate, and the reason is that tags are routing. A caller passing its own set is choosing which
    /// endpoints this check appears on, and appending <c>dependency</c> underneath that choice would put the
    /// check on any endpoint filtering for <c>dependency</c> -- including one the caller had deliberately
    /// kept it off. That is a silent change to a consumer's routing, which is the class of thing this package
    /// refuses everywhere else.
    /// <para>
    /// So the tag is documented as the default, not as a guarantee. What holds unconditionally is the
    /// <see cref="HealthStatus.Degraded"/> ceiling below, which no parameter can lift.
    /// </para>
    /// </remarks>
    [Fact]
    public void CallerSuppliedTags_ReplaceTheDefault()
    {
        HealthCheckRegistration registration = Register(tags: ["diagnostics"]);

        Assert.Contains("diagnostics", registration.Tags);
        Assert.DoesNotContain(HttpResilienceHealthCheckExtensions.DependencyTag, registration.Tags);
    }

    /// <summary>
    /// The real protection against a dependency outage restarting a healthy pod is that the check cannot
    /// report <see cref="HealthStatus.Unhealthy"/>, and that holds whatever tags it carries.
    /// </summary>
    /// <remarks>
    /// Written because the documentation used to say the check was "tagged so it cannot be wired to a
    /// liveness probe by accident", which credits the wrong mechanism: tags are replaceable by a parameter
    /// and route rather than protect. Registered failure status and reported status are both Degraded, and
    /// ASP.NET Core maps Degraded to HTTP 200 by default -- so even wired directly to a liveness probe the
    /// endpoint answers 200 and no pod is restarted. Only an explicit <c>ResultStatusCodes</c> opt-in on that
    /// endpoint changes it, which is a second deliberate act.
    /// </remarks>
    [Fact]
    public async Task TheDegradedCeilingHolds_WhateverTagsAreUsed()
    {
        HealthCheckRegistration registration = Register(name: "on-liveness", tags: ["live"]);

        var tracker = new CircuitBreakerStateTracker();
        tracker.Report(new CircuitKey("orders", "https://a.test"), CircuitState.Open);

        HealthCheckResult result = await new HttpResilienceHealthCheck(tracker).CheckHealthAsync(
            new HealthCheckContext { Registration = registration });

        Assert.Equal(HealthStatus.Degraded, registration.FailureStatus);
        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.NotEqual(HealthStatus.Unhealthy, result.Status);
    }

    private static HealthCheckRegistration Register(
        string name = "http-resilience",
        IEnumerable<string>? tags = null)
    {
        var services = new ServiceCollection();
        services.AddHttpResilience(new ConfigurationBuilder().Build().GetSection("HttpResilience"));
        services.AddHttpResilienceHealthChecks(name, tags);

        using ServiceProvider provider = services.BuildServiceProvider();
        return Assert.Single(
            provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations);
    }
}
