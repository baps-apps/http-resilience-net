using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HttpResilience.NET.Tests.Behavior;

/// <summary>
/// The sample's configuration is held to the same validation as any consumer's.
/// </summary>
/// <remarks>
/// The sample is the example most likely to be copied into a service. Fails with a message naming the key.
/// </remarks>
public class SampleConfigurationTests
{
    /// <summary>
    /// The clients the sample registers, and which of them uses the hedging pipeline. Their budget rules
    /// differ, so registering one as the other would validate against rules it does not run.
    /// </summary>
    private static readonly (string Name, bool Hedged)[] _sampleClients =
    [
        ("Default", false),
        ("Orders", false),
        ("Payments", false),
        ("Search", true),
        ("Partner", false)
    ];

    /// <summary>
    /// Fails if the sample's <c>appsettings.json</c> would fail startup: any rule on the root or on a client
    /// section, an unused client section, or a client whose handler chain cannot be built.
    /// </summary>
    /// <remarks>
    /// Registration reads no configuration, so validation is run the way a host runs it: every
    /// <see cref="IStartupValidator"/>, then each client created once, as the startup probe does.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task EveryClientTheSampleRegisters_PassesStartupValidation()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddJsonFile(SampleAppSettingsPath(), optional: false)
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilience(configuration);

        foreach ((string name, bool hedged) in _sampleClients)
        {
            IHttpClientBuilder builder = services.AddHttpClient(name);
            _ = hedged ? builder.AddHttpResilienceHedgingHandler() : builder.AddHttpResilienceHandler();
        }

        await using ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);

        IStartupValidator[] validators = [.. provider.GetServices<IStartupValidator>()];
        Assert.NotEmpty(validators);
        foreach (IStartupValidator validator in validators)
        {
            validator.Validate();
        }

        IHttpClientFactory factory = provider.GetRequiredService<IHttpClientFactory>();
        foreach ((string name, _) in _sampleClients)
        {
            factory.CreateClient(name).Dispose();
        }
    }

    /// <summary>
    /// Walks up from the test binary to the repository root, so the path does not depend on the build
    /// configuration or on where the test runner was started.
    /// </summary>
    private static string SampleAppSettingsPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HttpResilience.NET.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        string path = Path.Combine(
            directory.FullName, "samples", "HttpResilience.NET.Sample", "appsettings.json");

        Assert.True(File.Exists(path), $"The sample's appsettings.json was not found at '{path}'.");
        return path;
    }
}
