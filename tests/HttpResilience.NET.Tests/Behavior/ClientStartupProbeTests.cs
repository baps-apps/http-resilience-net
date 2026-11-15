using HttpResilience.NET.Internal;
using HttpResilience.NET.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace HttpResilience.NET.Tests.Behavior;

/// <summary>
/// The client startup probe creates every client this package configured, once, at host start, so a failure
/// reachable only through handler construction fails the deployment instead of a later request.
/// </summary>
/// <remarks>
/// Example: <c>Connection:Enabled</c> on a client whose primary handler is not a <c>SocketsHttpHandler</c>. That
/// is a DI fact, so <c>ValidateOnStart</c> cannot see it. On by default; opting out is a configuration key.
/// </remarks>
public class ClientStartupProbeTests
{
    private static ServiceProvider Build(
        string? validateClientsOnStart = null,
        Action<IHttpClientBuilder>? afterRegistration = null)
    {
        Settings settings = Settings.Enabled()
            .Set("Connection:Enabled", "true")
            .Set("Connection:PooledConnectionLifetime", "00:01:00")
            .Set("Connection:PooledConnectionIdleTimeout", "00:00:30");

        if (validateClientsOnStart is not null)
        {
            settings.Set("ValidateClientsOnStart", validateClientsOnStart);
        }

        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Build())
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilience(configuration);

        IHttpClientBuilder builder = services.AddHttpClient("test").AddHttpResilienceHandler();
        afterRegistration?.Invoke(builder);

        return services.BuildServiceProvider(validateScopes: true);
    }

    private static async Task StartAsync(ServiceProvider provider)
    {
        foreach (IHostedService service in provider.GetServices<IHostedService>())
        {
            await service.StartAsync(CancellationToken.None);
        }
    }

    private static Action<IHttpClientBuilder> UnconfigurableHandler() =>
        builder => builder.ConfigurePrimaryHttpMessageHandler(() => new RecordingHandler());

    /// <summary>
    /// The probe is registered by <c>AddHttpResilience</c> itself, with nothing else asked for.
    /// </summary>
    /// <remarks>
    /// Fails if the registration is removed from <c>AddHttpResilience</c>.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task TheProbeRuns_WithoutBeingAskedFor()
    {
        await using ServiceProvider provider = Build(afterRegistration: UnconfigurableHandler());

        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(provider));

        Assert.Contains("Connection:Enabled", exception.Message, StringComparison.Ordinal);
        Assert.Contains("SocketsHttpHandler", exception.Message, StringComparison.Ordinal);
        Assert.Contains("test", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The opt-out key turns it off, and the failure goes back to waiting for the first request.
    /// </summary>
    /// <remarks>
    /// Root-only and read from the raw section, like <c>AllowUnusedClientSections</c>, because it applies to
    /// the process, not a client.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task TheOptOutKey_TurnsTheProbeOff()
    {
        await using ServiceProvider provider = Build(
            validateClientsOnStart: "false",
            afterRegistration: UnconfigurableHandler());

        await StartAsync(provider);

        Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<IHttpClientFactory>().CreateClient("test"));
    }

    /// <summary>
    /// A value that is neither true nor false fails, rather than being read as one of them.
    /// </summary>
    /// <remarks>
    /// Same rule as <c>AllowUnusedClientSections</c>: "yes" must not be read as a value.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task AMalformedOptOutValue_FailsAtStart()
    {
        await using ServiceProvider provider = Build(validateClientsOnStart: "yes");

        // A validation failure, raised the moment the switch is read.
        OptionsValidationException exception =
            await Assert.ThrowsAsync<OptionsValidationException>(() => StartAsync(provider));

        Assert.Contains("ValidateClientsOnStart", exception.Message, StringComparison.Ordinal);
        Assert.Contains("yes", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A container whose clients are all constructible must start.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task AWellFormedContainer_StartsCleanly()
    {
        await using ServiceProvider provider = Build();

        await StartAsync(provider);
    }

    /// <summary>
    /// A shared library and the application may both call <c>AddHttpResilience</c>; there is still one probe.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task RegisteringTheRootTwice_RegistersOneProbe()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(Settings.Enabled().Build())
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilience(configuration);
        services.AddHttpResilience(configuration);
        services.AddHttpClient("test").AddHttpResilienceHandler();

        await using ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);

        Assert.Single(provider.GetServices<IHostedService>().OfType<ClientStartupProbe>());
        await StartAsync(provider);
    }

    /// <summary>
    /// Registering the schema and never calling the opt-in registers exactly one probe, not two.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public void TheDefaultRegistration_AddsExactlyOneProbe()
    {
        using ServiceProvider provider = Build();

        Assert.Single(provider.GetServices<IHostedService>().OfType<ClientStartupProbe>());
    }

    /// <summary>
    /// Registering the schema and no HTTP clients at all must still start.
    /// </summary>
    /// <remarks>
    /// <see cref="IHttpClientFactory"/> is not in the container until something calls <c>AddHttpClient</c>,
    /// so the probe must not require it. Fails if the <c>AddHttpClient()</c> core registration is removed
    /// from <c>AddHttpResilience</c>.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task AContainerWithNoHttpClientsAtAll_StillStarts()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(Settings.Enabled().Build())
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilience(configuration);

        await using ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);

        await StartAsync(provider);
    }

    /// <summary>
    /// Only the clients this package configured; an unrelated client's handler is not this package's
    /// concern.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ClientsThisPackageDidNotConfigure_AreNotCreated()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(Settings.Enabled().Build())
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilience(configuration);
        services.AddHttpClient("test").AddHttpResilienceHandler();

        // Not registered with this package, and deliberately unconstructible.
        services.AddHttpClient("someone-elses")
            .ConfigurePrimaryHttpMessageHandler(
                () => throw new InvalidOperationException("this handler must never be constructed"));

        await using ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);

        await StartAsync(provider);
    }
}
