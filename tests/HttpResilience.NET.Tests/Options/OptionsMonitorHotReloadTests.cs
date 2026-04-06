using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using HttpResilience.NET.Extensions;
using HttpResilience.NET.Options;

namespace HttpResilience.NET.Tests.Options;

public class OptionsMonitorHotReloadTests
{
    [Fact]
    public void OptionsMonitor_DetectsConfigChange_WhenSectionReloads()
    {
        var source = new MemoryConfigurationSource
        {
            InitialData = new Dictionary<string, string?>
            {
                ["HttpResilienceOptions:Enabled"] = "true",
                ["HttpResilienceOptions:PipelineOrder:0"] = "Standard",
                ["HttpResilienceOptions:Retry:MaxRetryAttempts"] = "3"
            }
        };
        var configuration = new ConfigurationBuilder()
            .Add(source)
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilienceOptions(configuration);

        using var provider = services.BuildServiceProvider();
        var monitor = provider.GetRequiredService<IOptionsMonitor<HttpResilienceOptions>>();

        // Initial value
        Assert.Equal(3, monitor.CurrentValue.Retry.MaxRetryAttempts);

        // Track change notification
        var changed = false;
        monitor.OnChange(opts => changed = true);

        // Mutate the underlying config and trigger reload
        configuration["HttpResilienceOptions:Retry:MaxRetryAttempts"] = "5";
        configuration.Reload();

        // After reload, monitor reflects the new value
        Assert.Equal(5, monitor.CurrentValue.Retry.MaxRetryAttempts);
        Assert.True(changed);
    }

    [Fact]
    public void OptionsSnapshot_ReturnsNamedOptions_AfterConfigChange()
    {
        var source = new MemoryConfigurationSource
        {
            InitialData = new Dictionary<string, string?>
            {
                ["MySection:Enabled"] = "true",
                ["MySection:PipelineOrder:0"] = "Standard",
                ["MySection:Retry:MaxRetryAttempts"] = "2"
            }
        };
        var configuration = new ConfigurationBuilder()
            .Add(source)
            .Build();
        var section = configuration.GetSection("MySection");

        var services = new ServiceCollection();
        services.AddHttpClient("NamedClient", _ => { })
            .AddHttpClientWithResilience(section);

        using var provider = services.BuildServiceProvider();

        // Use a scope to get IOptionsSnapshot (scoped)
        using var scope = provider.CreateScope();
        var snapshot = scope.ServiceProvider.GetRequiredService<IOptionsSnapshot<HttpResilienceOptions>>();
        var opts = snapshot.Get("NamedClient");

        Assert.Equal(2, opts.Retry.MaxRetryAttempts);
    }
}
