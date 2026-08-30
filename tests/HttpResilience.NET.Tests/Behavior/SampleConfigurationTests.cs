using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HttpResilience.NET.Tests.Behavior;

/// <summary>
/// The sample's configuration is held to the same validation as any consumer's.
/// </summary>
/// <remarks>
/// <c>samples/HttpResilience.NET.Sample</c> is the only executable example in the repository and the file
/// most likely to be copied into a service, and it shipped with
/// <c>Connection:PooledConnectionIdleTimeout</c> equal to <c>Connection:PooledConnectionLifetime</c> -- a
/// combination this package's own validator rejects, so <c>dotnet run --project samples/...</c> terminated
/// with an <c>OptionsValidationException</c> before reaching a single line of the demonstration.
/// <para>
/// CI builds, tests, AOT-publishes and packs, and ran nothing. A smoke step now starts the sample, and this
/// test fails the unit suite first with a message naming the key, because a validation failure in the
/// example is a defect in the documentation rather than in the code.
/// </para>
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
    /// Fails if any client section in the sample's <c>appsettings.json</c> would be rejected at registration.
    /// </summary>
    [Fact]
    public void EveryClientTheSampleRegisters_PassesValidation()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddJsonFile(SampleAppSettingsPath(), optional: false)
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilience(configuration);

        foreach ((string name, bool hedged) in _sampleClients)
        {
            IHttpClientBuilder builder = services.AddHttpClient(name);

            // Registration is where this package validates, so the assertion is that it returns at all.
            _ = hedged ? builder.AddHedgedHttpResilience() : builder.AddHttpResilience();
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
