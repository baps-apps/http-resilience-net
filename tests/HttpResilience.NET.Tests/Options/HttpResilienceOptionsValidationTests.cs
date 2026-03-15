using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using HttpResilience.NET.Extensions;
using HttpResilience.NET.Options;

namespace HttpResilience.NET.Tests.Options;

public class HttpResilienceOptionsValidationTests
{
    [Fact]
    public void AddHttpResilienceOptions_WithValidConfig_BindsAndValidatesOnStart()
    {
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Connection:MaxConnectionsPerServer"] = "20",
            ["HttpResilienceOptions:Retry:MaxRetryAttempts"] = "5"
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<HttpResilienceOptions>>().Value;
        Assert.Equal(20, options.Connection.MaxConnectionsPerServer);
        Assert.Equal(5, options.Retry.MaxRetryAttempts);
    }

    [Fact]
    public void AddHttpResilienceOptions_WithInvalidMaxConnectionsPerServer_ThrowsOnStart()
    {
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "true",
            ["HttpResilienceOptions:Connection:Enabled"] = "true",
            ["HttpResilienceOptions:Connection:MaxConnectionsPerServer"] = "0" // invalid: range 1–1000
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(
            () => _ = provider.GetRequiredService<IOptions<HttpResilienceOptions>>().Value);
    }

    [Fact]
    public void AddHttpResilienceOptions_WithInvalidFallbackStatusCode_ThrowsOnStart()
    {
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "true",
            ["HttpResilienceOptions:Fallback:Enabled"] = "true",
            ["HttpResilienceOptions:Fallback:StatusCode"] = "200" // invalid: must be 400-599 when Enabled
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(
            () => _ = provider.GetRequiredService<IOptions<HttpResilienceOptions>>().Value);
    }

    [Fact]
    public void AddHttpResilienceOptions_WithInvalidRetryBackoffType_ThrowsOnStart()
    {
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "true",
            ["HttpResilienceOptions:Retry:BackoffType"] = "99" // invalid: not a defined enum value
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(
            () => _ = provider.GetRequiredService<IOptions<HttpResilienceOptions>>().Value);
    }

    [Fact]
    public void AddHttpResilienceOptions_WithValidRetryBackoffType_BindsSuccessfully()
    {
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "true",
            ["HttpResilienceOptions:Retry:BackoffType"] = "Linear",
            ["HttpResilienceOptions:Retry:UseJitter"] = "false",
            ["HttpResilienceOptions:Retry:UseRetryAfterHeader"] = "true"
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<HttpResilienceOptions>>().Value;
        Assert.Equal(RetryBackoffType.Linear, options.Retry.BackoffType);
        Assert.False(options.Retry.UseJitter);
        Assert.True(options.Retry.UseRetryAfterHeader);
    }

    [Fact]
    public void AddHttpResilienceOptions_WithValidRateLimitAlgorithmAndPipelineOrder_BindsSuccessfully()
    {
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "true",
            ["HttpResilienceOptions:RateLimiter:Algorithm"] = "SlidingWindow",
            ["HttpResilienceOptions:RateLimiter:SegmentsPerWindow"] = "4",
            ["HttpResilienceOptions:PipelineOrder"] = "ConcurrencyThenFallback"
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<HttpResilienceOptions>>().Value;
        Assert.Equal(RateLimitAlgorithm.SlidingWindow, options.RateLimiter.Algorithm);
        Assert.Equal(4, options.RateLimiter.SegmentsPerWindow);
        Assert.Equal(PipelineOrderType.ConcurrencyThenFallback, options.PipelineOrder);
    }

    [Fact]
    public void AddHttpResilienceOptions_WithPipelineTypeHedging_BindsSuccessfully()
    {
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "true",
            ["HttpResilienceOptions:PipelineType"] = "Hedging",
            ["HttpResilienceOptions:Hedging:MaxHedgedAttempts"] = "2"
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<HttpResilienceOptions>>().Value;
        Assert.Equal(ResiliencePipelineType.Hedging, options.PipelineType);
        Assert.Equal(2, options.Hedging.MaxHedgedAttempts);
    }

    [Fact]
    public void AddHttpResilienceOptions_WithValidPipelineStrategyOrder_BindsSuccessfully()
    {
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "true",
            ["HttpResilienceOptions:PipelineStrategyOrder:0"] = "Fallback",
            ["HttpResilienceOptions:PipelineStrategyOrder:1"] = "Bulkhead",
            ["HttpResilienceOptions:PipelineStrategyOrder:2"] = "Standard"
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<HttpResilienceOptions>>().Value;
        Assert.NotNull(options.PipelineStrategyOrder);
        Assert.Equal(3, options.PipelineStrategyOrder.Count);
        Assert.Equal("Standard", options.PipelineStrategyOrder[2]);
    }

    [Fact]
    public void AddHttpResilienceOptions_WithPipelineStrategyOrderMissingCore_ThrowsOnStart()
    {
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "true",
            ["HttpResilienceOptions:PipelineStrategyOrder:0"] = "Fallback",
            ["HttpResilienceOptions:PipelineStrategyOrder:1"] = "Bulkhead"
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(
            () => _ = provider.GetRequiredService<IOptions<HttpResilienceOptions>>().Value);
    }

    [Fact]
    public void AddHttpResilienceOptions_WithPipelineSelectionByAuthority_BindsSuccessfully()
    {
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "true",
            ["HttpResilienceOptions:PipelineSelection:Mode"] = "ByAuthority"
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<HttpResilienceOptions>>().Value;
        Assert.Equal(PipelineSelectionMode.ByAuthority, options.PipelineSelection.Mode);
    }

    [Fact]
    public void AddHttpResilienceOptions_WithAttemptTimeoutGreaterThanTotalTimeout_ThrowsOnStart()
    {
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "true",
            ["HttpResilienceOptions:Timeout:TotalRequestTimeoutSeconds"] = "10",
            ["HttpResilienceOptions:Timeout:AttemptTimeoutSeconds"] = "15"
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(
            () => _ = provider.GetRequiredService<IOptions<HttpResilienceOptions>>().Value);
    }

    [Fact]
    public void AddHttpResilienceOptions_WithMaxConnectionsPerServerOver1000_ThrowsOnStart()
    {
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "true",
            ["HttpResilienceOptions:Connection:Enabled"] = "true",
            ["HttpResilienceOptions:Connection:MaxConnectionsPerServer"] = "1001"
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(
            () => _ = provider.GetRequiredService<IOptions<HttpResilienceOptions>>().Value);
    }

    [Fact]
    public void AddHttpResilienceOptions_WithFallbackDisabled_DoesNotValidateStatusCode()
    {
        // When Fallback.Enabled is false, StatusCode is not validated (can be any value or default).
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "false",
            ["HttpResilienceOptions:Fallback:Enabled"] = "false",
            ["HttpResilienceOptions:Fallback:StatusCode"] = "200"
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<HttpResilienceOptions>>().Value;
        Assert.False(options.Fallback.Enabled);
        Assert.Equal(200, options.Fallback.StatusCode);
    }

    [Fact]
    public void AddHttpResilienceOptions_WithFeatureDisabled_DoesNotValidateInvalidValues()
    {
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "false",
            ["HttpResilienceOptions:Connection:MaxConnectionsPerServer"] = "0",
            ["HttpResilienceOptions:Timeout:TotalRequestTimeoutSeconds"] = "10",
            ["HttpResilienceOptions:Timeout:AttemptTimeoutSeconds"] = "15",
            ["HttpResilienceOptions:Retry:BackoffType"] = "99",
            ["HttpResilienceOptions:RateLimiter:Algorithm"] = "99",
            ["HttpResilienceOptions:PipelineOrder"] = "999",
            ["HttpResilienceOptions:PipelineType"] = "999",
            ["HttpResilienceOptions:PipelineStrategyOrder:0"] = "Invalid",
            ["HttpResilienceOptions:PipelineSelection:Mode"] = "999"
        };

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        // Should not throw even though values are invalid, because Enabled is false.
        var options = provider.GetRequiredService<IOptions<HttpResilienceOptions>>().Value;
        Assert.False(options.Enabled);
    }
}
