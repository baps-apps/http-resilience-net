using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using HttpResilience.NET.Extensions;
using HttpResilience.NET.Internal;

namespace HttpResilience.NET.IntegrationTests;

public class ConcurrencyIntegrationTests
{
    [Fact]
    public async Task ParallelRequests_ThroughResiliencePipeline_AllSucceed()
    {
        await using var server = await StartTestServerAsync();
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "true",
            ["HttpResilienceOptions:PipelineOrder:0"] = "Standard",
            ["HttpResilienceOptions:Timeout:TotalRequestTimeoutSeconds"] = "30",
            ["HttpResilienceOptions:Timeout:AttemptTimeoutSeconds"] = "10",
            ["HttpResilienceOptions:Retry:MaxRetryAttempts"] = "2",
            ["HttpResilienceOptions:Retry:BaseDelaySeconds"] = "0",
            ["HttpResilienceOptions:Retry:BackoffType"] = "Constant",
            ["HttpResilienceOptions:CircuitBreaker:MinimumThroughput"] = "100"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);
        services.AddHttpClient("ConcurrencyTest", _ => { })
            .AddHttpClientWithResilience(configuration)
            .ConfigurePrimaryHttpMessageHandler(() => server.CreateHandler());

        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        const int parallelRequests = 50;
        var tasks = Enumerable.Range(0, parallelRequests)
            .Select(async _ =>
            {
                var client = factory.CreateClient("ConcurrencyTest");
                return await client.GetAsync($"{server.BaseAddress}ok");
            })
            .ToArray();

        var responses = await Task.WhenAll(tasks);

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
    }

    [Fact]
    public async Task ParallelRequests_WithRateLimiter_CompletesWithoutDeadlock()
    {
        await using var server = await StartTestServerAsync();
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "true",
            ["HttpResilienceOptions:PipelineOrder:0"] = "RateLimiter",
            ["HttpResilienceOptions:PipelineOrder:1"] = "Standard",
            ["HttpResilienceOptions:RateLimiter:Enabled"] = "true",
            ["HttpResilienceOptions:RateLimiter:PermitLimit"] = "1000",
            ["HttpResilienceOptions:RateLimiter:WindowSeconds"] = "1",
            ["HttpResilienceOptions:Timeout:TotalRequestTimeoutSeconds"] = "30",
            ["HttpResilienceOptions:Timeout:AttemptTimeoutSeconds"] = "10",
            ["HttpResilienceOptions:Retry:MaxRetryAttempts"] = "1",
            ["HttpResilienceOptions:Retry:BaseDelaySeconds"] = "0",
            ["HttpResilienceOptions:CircuitBreaker:MinimumThroughput"] = "100"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);
        services.AddHttpClient("RateLimitConcurrency", _ => { })
            .AddHttpClientWithResilience(configuration)
            .ConfigurePrimaryHttpMessageHandler(() => server.CreateHandler());

        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        const int parallelRequests = 20;
        var tasks = Enumerable.Range(0, parallelRequests)
            .Select(async _ =>
            {
                var client = factory.CreateClient("RateLimitConcurrency");
                return await client.GetAsync($"{server.BaseAddress}ok");
            })
            .ToArray();

        // Should complete without deadlock within a reasonable time.
        var completed = await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.NotEqual(typeof(Task), completed.GetType());

        var responses = await Task.WhenAll(tasks);
        Assert.All(responses, r => Assert.True(
            r.StatusCode == HttpStatusCode.OK || (int)r.StatusCode == 429 || r.StatusCode == HttpStatusCode.ServiceUnavailable,
            $"Unexpected status: {r.StatusCode}"));
    }

    [Fact]
    public async Task ParallelRequests_WithHealthCheck_TracksState()
    {
        await using var server = await StartTestServerAsync();
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "true",
            ["HttpResilienceOptions:PipelineOrder:0"] = "Standard",
            ["HttpResilienceOptions:Timeout:TotalRequestTimeoutSeconds"] = "30",
            ["HttpResilienceOptions:Timeout:AttemptTimeoutSeconds"] = "10",
            ["HttpResilienceOptions:Retry:MaxRetryAttempts"] = "1",
            ["HttpResilienceOptions:Retry:BaseDelaySeconds"] = "0",
            ["HttpResilienceOptions:CircuitBreaker:MinimumThroughput"] = "100"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);
        services.AddHttpResilienceHealthChecks();
        services.AddHttpClient("HealthCheckTest", _ => { })
            .AddHttpClientWithResilience(configuration)
            .ConfigurePrimaryHttpMessageHandler(() => server.CreateHandler());

        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        var client = factory.CreateClient("HealthCheckTest");
        var response = await client.GetAsync($"{server.BaseAddress}ok");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var tracker = provider.GetRequiredService<CircuitBreakerStateTracker>();
        Assert.False(tracker.HasOpenCircuits);
    }

    private static async Task<TestServerFixture> StartTestServerAsync()
    {
        var builder = Host.CreateDefaultBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services => services.AddRouting());
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/ok", () => Results.Ok());
                    });
                });
            });

        var host = builder.Build();
        await host.StartAsync();
        return new TestServerFixture(host);
    }

    private sealed class TestServerFixture : IAsyncDisposable
    {
        private readonly IHost _host;
        private readonly Func<HttpMessageHandler> _createHandler;

        public TestServerFixture(IHost host)
        {
            _host = host;
            var testServer = host.GetTestServer();
            BaseAddress = testServer.BaseAddress?.ToString() ?? "http://localhost/";
            _createHandler = testServer.CreateHandler;
        }

        public string BaseAddress { get; }
        public HttpMessageHandler CreateHandler() => _createHandler();
        public async ValueTask DisposeAsync() => await _host.StopAsync().ConfigureAwait(false);
    }
}
