using HttpResilience.NET.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace HttpResilience.NET.Tests.Behavior;

/// <summary>
/// The startup switches (<c>ValidateClientsOnStart</c>, <c>AllowUnusedClientSections</c>,
/// <c>AllowUnknownKeys</c>) are read from the root section and can also be set in code, which wins.
/// </summary>
public class StartupOptionsTests
{
    private static ServiceProvider Build(
        Settings settings,
        Action<IServiceCollection>? before = null,
        Action<IServiceCollection>? after = null,
        bool unconfigurableHandler = false)
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Build())
            .Build();

        var services = new ServiceCollection();
        before?.Invoke(services);
        services.AddHttpResilience(configuration);
        IHttpClientBuilder builder = services.AddHttpClient("Orders").AddHttpResilienceHandler();
        if (unconfigurableHandler)
        {
            // Connection:Enabled needs a SocketsHttpHandler, so creating this client throws.
            builder.ConfigurePrimaryHttpMessageHandler(() => new RecordingHandler());
        }

        after?.Invoke(services);
        return services.BuildServiceProvider(validateScopes: true);
    }

    private static Settings WithConnection() => Settings.Enabled()
        .Set("Connection:Enabled", "true")
        .Set("Connection:PooledConnectionLifetime", "00:01:00")
        .Set("Connection:PooledConnectionIdleTimeout", "00:00:30");

    private static async Task StartAsync(ServiceProvider provider)
    {
        foreach (IHostedService service in provider.GetServices<IHostedService>())
        {
            await service.StartAsync(CancellationToken.None);
        }
    }

    /// <summary>Configuration values reach the options, so code reading them sees what the host will do.</summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public void TheRootKeys_BindToTheStartupOptions()
    {
        using ServiceProvider provider = Build(Settings.Enabled()
            .Set("ValidateClientsOnStart", "false")
            .Set("AllowUnusedClientSections", "true")
            .Set("AllowUnknownKeys", "true"));

        HttpResilienceStartupOptions options =
            provider.GetRequiredService<IOptions<HttpResilienceStartupOptions>>().Value;

        Assert.False(options.ValidateClientsOnStart);
        Assert.True(options.AllowUnusedClientSections);
        Assert.True(options.AllowUnknownKeys);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void TheDefaults_AreStrict()
    {
        using ServiceProvider provider = Build(Settings.Enabled());

        HttpResilienceStartupOptions options =
            provider.GetRequiredService<IOptions<HttpResilienceStartupOptions>>().Value;

        Assert.True(options.ValidateClientsOnStart);
        Assert.False(options.AllowUnusedClientSections);
        Assert.False(options.AllowUnknownKeys);
    }

    /// <summary>A code-only application can turn the client probe off.</summary>
    /// <remarks>Fails if the probe stops reading the startup options.</remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ValidateClientsOnStart_SetInCode_TurnsTheProbeOff()
    {
        await using ServiceProvider provider = Build(
            WithConnection(),
            after: services => services.Configure<HttpResilienceStartupOptions>(o => o.ValidateClientsOnStart = false),
            unconfigurableHandler: true);

        await StartAsync(provider);

        Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<IHttpClientFactory>().CreateClient("Orders"));
    }

    /// <summary>
    /// Code wins over configuration whichever is registered first, matching the package's composition order
    /// (configuration, then code).
    /// </summary>
    /// <remarks>Fails if configuration is applied after a delegate registered before <c>AddHttpResilience</c>.</remarks>
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CodeWinsOverConfiguration_InEitherRegistrationOrder(bool codeFirst)
    {
        static void TurnProbeOn(IServiceCollection services) =>
            services.Configure<HttpResilienceStartupOptions>(o => o.ValidateClientsOnStart = true);

        await using ServiceProvider provider = Build(
            WithConnection().Set("ValidateClientsOnStart", "false"),
            before: codeFirst ? TurnProbeOn : null,
            after: codeFirst ? null : TurnProbeOn,
            unconfigurableHandler: true);

        // The probe runs and creating the client fails the start.
        await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(provider));
    }

    /// <summary>A code-only application can allow a section no client reads.</summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public void AllowUnusedClientSections_SetInCode_AllowsAnUnreadSection()
    {
        using ServiceProvider provider = Build(
            Settings.Enabled().Set("Clients:Nobody:Timeout:Total", "00:00:10"),
            after: services => services.Configure<HttpResilienceStartupOptions>(o => o.AllowUnusedClientSections = true));

        foreach (IStartupValidator validator in provider.GetServices<IStartupValidator>())
        {
            validator.Validate();
        }
    }

    /// <summary>A value that is neither true nor false fails startup naming the key, for every switch.</summary>
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData("ValidateClientsOnStart")]
    [InlineData("AllowUnusedClientSections")]
    [InlineData("AllowUnknownKeys")]
    public void AMalformedSwitch_FailsStartup_NamingTheKey(string key)
    {
        OptionsValidationException exception = StartupAssert.Fails(() => Build(Settings.Enabled().Set(key, "yes")));

        string failures = string.Join(" | ", exception.Failures);
        Assert.Contains($"HttpResilience:{key}", failures, StringComparison.Ordinal);
        Assert.Contains("'yes'", failures, StringComparison.Ordinal);
    }
}
