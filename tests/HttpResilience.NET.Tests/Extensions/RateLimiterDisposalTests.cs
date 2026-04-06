using System.Threading.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using HttpResilience.NET.Extensions;

namespace HttpResilience.NET.Tests.Extensions;

public class RateLimiterDisposalTests
{
    [Fact]
    public async Task ServiceProvider_Dispose_DisposesRateLimiterSingleton()
    {
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "true",
            ["HttpResilienceOptions:PipelineOrder:0"] = "Standard",
            ["HttpResilienceOptions:RateLimiter:Enabled"] = "true",
            ["HttpResilienceOptions:RateLimiter:PermitLimit"] = "100",
            ["HttpResilienceOptions:RateLimiter:WindowSeconds"] = "1"
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);
        services.AddHttpClient("DisposalTest", _ => { })
            .AddHttpClientWithResilience(configuration);

        var provider = services.BuildServiceProvider();

        // Resolve the rate limiter singleton registered by the library.
        var limiter = provider.GetRequiredService<RateLimiter>();
        Assert.NotNull(limiter);

        // Verify the limiter is functional before disposal.
        using var lease = await limiter.AcquireAsync(1);
        Assert.True(lease.IsAcquired);

        // Dispose the provider — should dispose all singletons including the rate limiter.
        await provider.DisposeAsync();

        // After disposal, the limiter should throw ObjectDisposedException.
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await limiter.AcquireAsync(1));
    }

    [Fact]
    public async Task ServiceProvider_Dispose_DisposesRateLimiterFromPipelineOrder()
    {
        var configData = new Dictionary<string, string?>
        {
            ["HttpResilienceOptions:Enabled"] = "true",
            ["HttpResilienceOptions:PipelineOrder:0"] = "RateLimiter",
            ["HttpResilienceOptions:PipelineOrder:1"] = "Standard",
            ["HttpResilienceOptions:RateLimiter:Enabled"] = "true",
            ["HttpResilienceOptions:RateLimiter:PermitLimit"] = "50",
            ["HttpResilienceOptions:RateLimiter:WindowSeconds"] = "1"
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);
        services.AddHttpClient("DisposalTest2", _ => { })
            .AddHttpClientWithResilience(configuration);

        var provider = services.BuildServiceProvider();
        var limiter = provider.GetRequiredService<RateLimiter>();
        Assert.NotNull(limiter);

        await provider.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await limiter.AcquireAsync(1));
    }
}
