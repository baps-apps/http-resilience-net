using Microsoft.Extensions.Diagnostics.HealthChecks;
using HttpResilience.NET.Internal;

namespace HttpResilience.NET.Tests.Internal;

public class HttpResilienceHealthCheckTests
{
    [Fact]
    public async Task CheckHealthAsync_AllClosed_ReturnsHealthy()
    {
        var tracker = new CircuitBreakerStateTracker();
        tracker.ReportClosed("client-a");
        var check = new HttpResilienceHealthCheck(tracker);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task CheckHealthAsync_NoClients_ReturnsHealthy()
    {
        var tracker = new CircuitBreakerStateTracker();
        var check = new HttpResilienceHealthCheck(tracker);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task CheckHealthAsync_OneOpen_ReturnsDegraded()
    {
        var tracker = new CircuitBreakerStateTracker();
        tracker.ReportClosed("client-a");
        tracker.ReportOpened("client-b");
        var check = new HttpResilienceHealthCheck(tracker);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("client-b", result.Description!);
    }

    [Fact]
    public async Task CheckHealthAsync_HalfOpen_ReturnsDegraded()
    {
        var tracker = new CircuitBreakerStateTracker();
        tracker.ReportHalfOpen("client-a");
        var check = new HttpResilienceHealthCheck(tracker);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    [Fact]
    public async Task CheckHealthAsync_IncludesDataPerClient()
    {
        var tracker = new CircuitBreakerStateTracker();
        tracker.ReportOpened("client-a");
        tracker.ReportClosed("client-b");
        var check = new HttpResilienceHealthCheck(tracker);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.True(result.Data.ContainsKey("client-a"));
        Assert.True(result.Data.ContainsKey("client-b"));
        Assert.Equal("Open", result.Data["client-a"]);
        Assert.Equal("Closed", result.Data["client-b"]);
    }
}
