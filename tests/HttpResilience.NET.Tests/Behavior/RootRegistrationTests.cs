using HttpResilience.NET.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HttpResilience.NET.Tests.Behavior;

/// <summary>
/// The root registration holds the ledger of clients that already have a pipeline, so calling it twice used
/// to disarm the guard that stops two pipelines nesting on one client.
/// </summary>
/// <remarks>
/// A shared registration extension calling <c>AddHttpResilience(configuration)</c> and an application calling
/// it again is the ordinary shape for a platform package, and every other Microsoft registration extension is
/// idempotent, so nothing about the second call looks dangerous at the call site. The measured consequence was
/// nine origin calls for one logical GET: two nested pipelines of three attempts each, with nothing thrown and
/// nothing logged.
/// </remarks>
public class RootRegistrationTests
{
    private static IConfigurationRoot Configuration(Settings settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings.Build()).Build();

    /// <summary>
    /// Fails if the root registration replaces the client ledger instead of keeping it.
    /// </summary>
    [Fact]
    public async Task RepeatedRootRegistration_KeepsTheDuplicateClientGuardArmed()
    {
        var services = new ServiceCollection();
        services.AddHttpResilience(Configuration(Settings.Enabled()));

        var origin = new RecordingHandler();
        services.AddHttpClient("orders")
            .AddHttpResilience()
            .ConfigurePrimaryHttpMessageHandler(() => origin);

        // The shared platform extension adds it again, after the client is already configured.
        services.AddHttpResilience(Configuration(Settings.Enabled()));

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => services.AddHttpClient("orders").AddHttpResilience());

        Assert.Contains("already configured", exception.Message, StringComparison.Ordinal);

        // One pipeline, not two: three attempts rather than nine.
        await using ServiceProvider provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient("orders")
            .GetAsync("http://origin.test/x");

        Assert.Equal(3, origin.Count);
    }

    /// <summary>
    /// Fails if a second root registration silently changes which section later clients read.
    /// </summary>
    /// <remarks>
    /// Clients registered before the second call were configured from the first section and clients after it
    /// from the second, so the two disagreed with nothing to show it. Refusing is the only outcome that cannot
    /// mislead.
    /// </remarks>
    [Fact]
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
    [Fact]
    public async Task RepeatedRootRegistration_WithTheSameSection_StillConfiguresClients()
    {
        IConfigurationRoot configuration = Configuration(Settings.Enabled());

        var services = new ServiceCollection();
        services.AddHttpResilience(configuration);
        services.AddHttpResilience(configuration);

        var origin = new RecordingHandler();
        services.AddHttpClient("orders")
            .AddHttpResilience()
            .ConfigurePrimaryHttpMessageHandler(() => origin);

        await using ServiceProvider provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient("orders")
            .GetAsync("http://origin.test/x");

        Assert.Equal(3, origin.Count);
    }
}
