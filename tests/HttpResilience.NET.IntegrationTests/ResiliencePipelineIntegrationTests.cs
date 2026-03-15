using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using HttpResilience.NET.Extensions;

namespace HttpResilience.NET.IntegrationTests;

public class ResiliencePipelineIntegrationTests
{
    [Fact]
    public async Task FullPipeline_AgainstRealServer_ReturnsSuccess()
    {
        await using var server = await StartTestServerAsync();
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "true",
            ["HttpResilienceOptions:Timeout:TotalRequestTimeoutSeconds"] = "30",
            ["HttpResilienceOptions:Timeout:AttemptTimeoutSeconds"] = "10",
            ["HttpResilienceOptions:Retry:MaxRetryAttempts"] = "2",
            ["HttpResilienceOptions:CircuitBreaker:MinimumThroughput"] = "2"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);
        services.AddHttpClient("IntegrationTest", client => { })
            .AddHttpClientWithResilience(configuration)
            .ConfigurePrimaryHttpMessageHandler(() => server.CreateHandler());

        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        var client = factory.CreateClient("IntegrationTest");

        var response = await client.GetAsync($"{server.BaseAddress}ok");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task FullPipeline_AgainstRealServer_RetriesThenSucceeds()
    {
        s_failTwiceCount = 0;
        await using var server = await StartTestServerAsync();
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "true",
            ["HttpResilienceOptions:Timeout:TotalRequestTimeoutSeconds"] = "30",
            ["HttpResilienceOptions:Timeout:AttemptTimeoutSeconds"] = "5",
            ["HttpResilienceOptions:Retry:MaxRetryAttempts"] = "3",
            ["HttpResilienceOptions:Retry:BaseDelaySeconds"] = "0",
            ["HttpResilienceOptions:Retry:BackoffType"] = "Constant",
            ["HttpResilienceOptions:CircuitBreaker:MinimumThroughput"] = "10"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);
        services.AddHttpClient("RetryTest", client => { })
            .AddHttpClientWithResilience(configuration)
            .ConfigurePrimaryHttpMessageHandler(() => server.CreateHandler());

        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        var client = factory.CreateClient("RetryTest");

        var response = await client.GetAsync($"{server.BaseAddress}fail-twice");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task FullPipeline_WithFallback_ReturnsFallbackOnTotalFailure()
    {
        await using var server = await StartTestServerAsync();
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "true",
            ["HttpResilienceOptions:Fallback:Enabled"] = "true",
            ["HttpResilienceOptions:Fallback:StatusCode"] = "503",
            ["HttpResilienceOptions:Retry:MaxRetryAttempts"] = "1",
            ["HttpResilienceOptions:Timeout:TotalRequestTimeoutSeconds"] = "5",
            ["HttpResilienceOptions:Timeout:AttemptTimeoutSeconds"] = "2"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);
        services.AddHttpClient("FallbackTest", client => { })
            .AddHttpClientWithResilience(configuration)
            .ConfigurePrimaryHttpMessageHandler(() => server.CreateHandler());

        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        var client = factory.CreateClient("FallbackTest");

        var response = await client.GetAsync($"{server.BaseAddress}error");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
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
                        endpoints.MapGet("/error", () => Results.StatusCode(500));
                        endpoints.MapGet("/fail-twice", () => FailTwiceThenOk());
                    });
                });
            });

        var host = builder.Build();
        await host.StartAsync();
        return new TestServerFixture(host);
    }

    private static int s_failTwiceCount;

    private static IResult FailTwiceThenOk()
    {
        if (s_failTwiceCount < 2)
        {
            s_failTwiceCount++;
            return Results.StatusCode(500);
        }
        return Results.Ok();
    }

    private sealed class TestServerFixture : IAsyncDisposable
    {
        private readonly IHost _host;

        public TestServerFixture(IHost host)
        {
            _host = host;
            var testServer = host.GetTestServer();
            BaseAddress = testServer.BaseAddress?.ToString() ?? "http://localhost/";
            _createHandler = testServer.CreateHandler;
        }

        private readonly Func<HttpMessageHandler> _createHandler;

        public string BaseAddress { get; }

        public HttpMessageHandler CreateHandler() => _createHandler();

        public async ValueTask DisposeAsync() => await _host.StopAsync().ConfigureAwait(false);
    }
}
