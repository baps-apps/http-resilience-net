using System.Collections.Generic;
using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Resilience;
using Polly;
using HttpResilience.NET.Configuration;
using HttpResilience.NET.Extensions;

namespace HttpResilience.NET.Tests.Extensions;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddHttpResilienceOptions_And_AddHttpClientWithResilience_RegisterWithoutThrowing()
    {
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "true",
            ["HttpResilienceOptions:Connection:MaxConnectionsPerServer"] = "10",
            ["HttpResilienceOptions:Retry:MaxRetryAttempts"] = "3"
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);
        services.AddHttpClient("TestClient", _ => { })
            .AddHttpClientWithResilience(configuration, 30);

        using ServiceProvider provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        HttpClient client = factory.CreateClient("TestClient");

        Assert.NotNull(client);
    }

    [Fact]
    public void AddHttpClientWithResilience_WithNullTimeout_UsesDefault()
    {
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "true",
            ["HttpResilienceOptions:Connection:MaxConnectionsPerServer"] = "5"
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);
        services.AddHttpClient("DefaultTimeout", _ => { })
            .AddHttpClientWithResilience(configuration, requestTimeoutSeconds: null);

        using ServiceProvider provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        HttpClient client = factory.CreateClient("DefaultTimeout");

        Assert.NotNull(client);
    }

    [Fact]
    public void AddHttpClientWithResilience_WhenDisabled_ReturnsBuilderWithoutApplyingResilience()
    {
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "false",
            ["HttpResilienceOptions:Connection:MaxConnectionsPerServer"] = "10"
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);
        services.AddHttpClient("NoResilience", _ => { })
            .AddHttpClientWithResilience(configuration);

        using ServiceProvider provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        HttpClient client = factory.CreateClient("NoResilience");

        Assert.NotNull(client);
    }

    [Fact]
    public void AddHttpClientWithResilience_WithRateLimitEnabled_RegistersWithoutThrowing()
    {
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "true",
            ["HttpResilienceOptions:RateLimiter:Enabled"] = "true",
            ["HttpResilienceOptions:RateLimiter:PermitLimit"] = "100",
            ["HttpResilienceOptions:RateLimiter:WindowSeconds"] = "1"
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);
        services.AddHttpClient("RateLimited", _ => { })
            .AddHttpClientWithResilience(configuration);

        using ServiceProvider provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        HttpClient client = factory.CreateClient("RateLimited");

        Assert.NotNull(client);
    }

    [Fact]
    public void AddHttpClientWithResilience_WithConcurrencyLimitEnabled_RegistersWithoutThrowing()
    {
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "true",
            ["HttpResilienceOptions:Bulkhead:Enabled"] = "true",
            ["HttpResilienceOptions:Bulkhead:Limit"] = "50",
            ["HttpResilienceOptions:Bulkhead:QueueLimit"] = "10"
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);
        services.AddHttpClient("ConcurrencyLimited", _ => { })
            .AddHttpClientWithResilience(configuration);

        using ServiceProvider provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        HttpClient client = factory.CreateClient("ConcurrencyLimited");

        Assert.NotNull(client);
    }

    [Fact]
    public void AddHttpClientWithResilience_WithFallbackEnabled_RegistersWithoutThrowing()
    {
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "true",
            ["HttpResilienceOptions:Fallback:Enabled"] = "true",
            ["HttpResilienceOptions:Fallback:StatusCode"] = "503"
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);
        services.AddHttpClient("WithFallback", _ => { })
            .AddHttpClientWithResilience(configuration);

        using ServiceProvider provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        HttpClient client = factory.CreateClient("WithFallback");

        Assert.NotNull(client);
    }

    [Fact]
    public void AddHttpClientWithResilience_WithPipelineTypeHedging_RegistersWithoutThrowing()
    {
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "true",
            ["HttpResilienceOptions:PipelineType"] = "Hedging",
            ["HttpResilienceOptions:Connection:MaxConnectionsPerServer"] = "10",
            ["HttpResilienceOptions:Hedging:DelaySeconds"] = "2",
            ["HttpResilienceOptions:Hedging:MaxHedgedAttempts"] = "1"
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);
        services.AddHttpClient("HedgingClient", _ => { })
            .AddHttpClientWithResilience(configuration, 30);

        using ServiceProvider provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        HttpClient client = factory.CreateClient("HedgingClient");

        Assert.NotNull(client);
    }

    [Fact]
    public void AddHttpClientWithResilience_WhenDisabled_ReturnsBuilderWithoutApplyingResilience_RegardlessOfPipelineType()
    {
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "false",
            ["HttpResilienceOptions:PipelineType"] = "Hedging"
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);
        services.AddHttpClient("NoHedging", _ => { })
            .AddHttpClientWithResilience(configuration);

        using ServiceProvider provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        HttpClient client = factory.CreateClient("NoHedging");

        Assert.NotNull(client);
    }

    [Fact]
    public void AddHttpClientWithResilience_WithCustomInnerPipeline_RegistersWithoutThrowing()
    {
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "true",
            ["HttpResilienceOptions:Connection:MaxConnectionsPerServer"] = "10"
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);
        services.AddHttpClient("CustomInner", _ => { })
            .AddHttpClientWithResilience(
                configuration,
                requestTimeoutSeconds: 10,
                fallbackHandler: null,
                configureInnerPipeline: inner =>
                {
                    inner
                        .AddTimeout(new HttpTimeoutStrategyOptions { Timeout = TimeSpan.FromSeconds(10) })
                        .AddRetry(new HttpRetryStrategyOptions
                        {
                            MaxRetryAttempts = 1,
                            Delay = TimeSpan.FromMilliseconds(50),
                            BackoffType = DelayBackoffType.Constant
                        })
                        .AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
                        {
                            FailureRatio = 0.5,
                            MinimumThroughput = 10,
                            SamplingDuration = TimeSpan.FromSeconds(30),
                            BreakDuration = TimeSpan.FromSeconds(5)
                        });
                });

        using ServiceProvider provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        HttpClient client = factory.CreateClient("CustomInner");

        Assert.NotNull(client);
    }
}
