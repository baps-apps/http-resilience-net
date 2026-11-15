using HttpResilience.NET.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HttpResilience.NET.Tests.Behavior;

/// <summary>
/// The root registration holds the ledger of clients that already have a pipeline, so it must be idempotent
/// rather than replace that ledger.
/// </summary>
/// <remarks>
/// A shared extension and the application both calling <c>AddHttpResilience(configuration)</c> is normal.
/// Replacing the ledger would let a client nest two pipelines: nine origin calls for one GET.
/// </remarks>
public class RootRegistrationTests
{
    private static IConfigurationRoot Configuration(Settings settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings.Build()).Build();

    /// <summary>
    /// Fails if the root registration replaces the client ledger instead of keeping it.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task RepeatedRootRegistration_KeepsTheDuplicateClientGuardArmed()
    {
        var services = new ServiceCollection();
        services.AddHttpResilience(Configuration(Settings.Enabled()));

        var origin = new RecordingHandler();
        services.AddHttpClient("orders")
            .AddHttpResilienceHandler()
            .ConfigurePrimaryHttpMessageHandler(() => origin);

        // The shared platform extension adds it again, after the client is already configured.
        services.AddHttpResilience(Configuration(Settings.Enabled()));

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => services.AddHttpClient("orders").AddHttpResilienceHandler());

        Assert.Contains("already configured", exception.Message, StringComparison.Ordinal);

        // One pipeline, not two: three attempts rather than nine.
        await using ServiceProvider provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient("orders")
            .GetAsync("http://origin.test/x", TestContext.Current.CancellationToken);

        Assert.Equal(3, origin.Count);
    }

    /// <summary>
    /// Fails if a second root registration silently changes which section later clients read.
    /// </summary>
    /// <remarks>
    /// Clients on either side of the second call would read different sections.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public void RootRegistrationWithASecondSection_FailsAndNamesBothPaths()
    {
        IConfigurationRoot configuration = Configuration(Settings.Enabled());

        var services = new ServiceCollection();
        services.AddHttpResilience(configuration.GetSection("HttpResilience"));

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => services.AddHttpResilience(configuration.GetSection("SomewhereElse")));

        Assert.Contains("HttpResilience", exception.Message, StringComparison.Ordinal);
        Assert.Contains("SomewhereElse", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The idempotent path must not stop working: repeating the call with the same section is what a shared
    /// registration extension does, and it has to leave a usable container behind.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task RepeatedRootRegistration_WithTheSameSection_StillConfiguresClients()
    {
        IConfigurationRoot configuration = Configuration(Settings.Enabled());

        var services = new ServiceCollection();
        services.AddHttpResilience(configuration);
        services.AddHttpResilience(configuration);

        var origin = new RecordingHandler();
        services.AddHttpClient("orders")
            .AddHttpResilienceHandler()
            .ConfigurePrimaryHttpMessageHandler(() => origin);

        await using ServiceProvider provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient("orders")
            .GetAsync("http://origin.test/x", TestContext.Current.CancellationToken);

        Assert.Equal(3, origin.Count);
    }

    /// <summary>
    /// The same path from a <i>different</i> configuration is a different section. The first call's values would win
    /// silently, so a repeat whose values differ fails when client options are built, naming a differing key.
    /// </summary>
    /// <remarks>Fails if the repeat is compared by path alone.</remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task RepeatedRootRegistration_FromADifferentConfiguration_WithDifferentValues_Fails()
    {
        var services = new ServiceCollection();
        services.AddHttpResilience(Configuration(Settings.Enabled()));
        services.AddHttpResilience(Configuration(Settings.Enabled().Set("Retry:MaxRetries", "5")));
        services.AddHttpClient("orders").AddHttpResilienceHandler()
            .ConfigurePrimaryHttpMessageHandler(() => new RecordingHandler());

        await using ServiceProvider provider = services.BuildServiceProvider();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<IHttpClientFactory>().CreateClient("orders"));

        Assert.Contains("'HttpResilience'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("HttpResilience:Retry:MaxRetries", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A different configuration object with the same values changes nothing, so it is accepted.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task RepeatedRootRegistration_FromADifferentConfiguration_WithTheSameValues_IsAccepted()
    {
        var services = new ServiceCollection();
        services.AddHttpResilience(Configuration(Settings.Enabled()));
        services.AddHttpResilience(Configuration(Settings.Enabled()));
        var origin = new RecordingHandler();
        services.AddHttpClient("orders").AddHttpResilienceHandler().ConfigurePrimaryHttpMessageHandler(() => origin);

        await using ServiceProvider provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient("orders")
            .GetAsync("http://origin.test/x", TestContext.Current.CancellationToken);

        Assert.Equal(3, origin.Count);
    }
}
