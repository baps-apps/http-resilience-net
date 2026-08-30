using HttpResilience.NET.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace HttpResilience.NET.Tests.Behavior;

/// <summary>
/// <c>ConfigurePrimaryHttpMessageHandler</c> is last-wins; <c>SetHandlerLifetime</c> is not. A client can
/// therefore end up with factory rotation disabled and a primary handler whose
/// <c>PooledConnectionLifetime</c> is the runtime default of infinite, so its connection pool is never
/// recycled and DNS is never refreshed. In Kubernetes that is an outage that takes a deploy to notice.
/// </summary>
public class PrimaryHandlerOwnershipTests
{
    private static ServiceProvider Build(Action<IHttpClientBuilder> before, Action<IHttpClientBuilder> after)
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(Settings.Enabled()
                .Set("Connection:Enabled", "true")
                .Set("Connection:PooledConnectionLifetime", "00:01:00")
                // Below the lifetime, or the idle bound could never fire and startup validation says so.
                .Set("Connection:PooledConnectionIdleTimeout", "00:00:30")
                .Build())
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilience(configuration);

        IHttpClientBuilder builder = services.AddHttpClient("test");
        before(builder);
        builder.AddHttpResilience();
        after(builder);

        return services.BuildServiceProvider(validateScopes: true);
    }

    private static HttpClient CreateClient(ServiceProvider provider) =>
        provider.GetRequiredService<IHttpClientFactory>().CreateClient("test");

    /// <summary>
    /// The failure this guards is a handler that outlives its connections: factory rotation is disabled on
    /// the strength of PooledConnectionLifetime, so a replacement handler carrying the runtime default of
    /// infinite would leave nothing to recycle the pool or re-resolve DNS.
    /// </summary>
    [Fact]
    public void HandlerReplacedAfterRegistration_StillCarriesTheConnectionSettings()
    {
        var consumerHandler = new SocketsHttpHandler();

        using ServiceProvider provider = Build(
            before: _ => { },
            after: builder => builder.ConfigurePrimaryHttpMessageHandler(() => consumerHandler));

        _ = CreateClient(provider);

        Assert.Equal(TimeSpan.FromMinutes(1), consumerHandler.PooledConnectionLifetime);
        Assert.NotEqual(Timeout.InfiniteTimeSpan, consumerHandler.PooledConnectionLifetime);
    }

    /// <summary>
    /// Registration order must not decide whether connection settings take effect. It used to: the package
    /// replaced the handler while registering, so a consumer registering before lost their settings and a
    /// consumer registering after silently lost the package's.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConnectionSettings_AreAppliedRegardlessOfRegistrationOrder(bool consumerFirst)
    {
        var consumerHandler = new SocketsHttpHandler { MaxResponseHeadersLength = 77 };
        void Register(IHttpClientBuilder builder) =>
            builder.ConfigurePrimaryHttpMessageHandler(() => consumerHandler);

        using ServiceProvider provider = Build(
            before: consumerFirst ? Register : _ => { },
            after: consumerFirst ? _ => { }
        : Register);

        _ = CreateClient(provider);

        Assert.Equal(77, consumerHandler.MaxResponseHeadersLength);
        Assert.Equal(TimeSpan.FromMinutes(1), consumerHandler.PooledConnectionLifetime);
    }

    /// <summary>
    /// A handler configured before registration carries settings this package cannot express -- a client
    /// certificate, a proxy, an SSL callback. Replacing it would discard them silently, so the connection
    /// settings are applied to it instead.
    /// </summary>
    [Fact]
    public void SocketsHandlerConfiguredBeforeRegistration_IsKeptAndConfigured()
    {
        var consumerHandler = new SocketsHttpHandler { MaxResponseHeadersLength = 123 };

        using ServiceProvider provider = Build(
            before: builder => builder.ConfigurePrimaryHttpMessageHandler(() => consumerHandler),
            after: _ => { });

        _ = CreateClient(provider);

        Assert.Equal(123, consumerHandler.MaxResponseHeadersLength);
        Assert.Equal(TimeSpan.FromMinutes(1), consumerHandler.PooledConnectionLifetime);
    }

    /// <summary>
    /// A primary handler this package cannot configure -- a stub, a recording handler, anything that is not
    /// a <c>SocketsHttpHandler</c> -- has no pooled-connection lifetime to set, so silently disabling
    /// factory rotation around it would be the same defect by another route.
    /// </summary>
    [Fact]
    public void UnconfigurableHandler_FailsWithAnActionableMessage()
    {
        using ServiceProvider provider = Build(
            before: builder => builder.ConfigurePrimaryHttpMessageHandler(() => new RecordingHandler()),
            after: _ => { });

        InvalidOperationException exception =
            Assert.Throws<InvalidOperationException>(() => CreateClient(provider));

        Assert.Contains("Connection:Enabled", exception.Message, StringComparison.Ordinal);
        Assert.Contains("SocketsHttpHandler", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Nothing here applies when the package was never asked to own connection settings.
    /// </summary>
    [Fact]
    public async Task ConnectionDisabled_LeavesThePrimaryHandlerAlone()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(Settings.Enabled());

        await harness.GetAsync();

        Assert.Equal(3, harness.Origin.Count);
    }

    /// <summary>
    /// The connection filter is process-wide, so it runs on every handler build in the application once any
    /// client enables connection settings. A client this package never registered must come out untouched.
    /// </summary>
    /// <remarks>
    /// Correct today, but by a chain of coincidences nothing asserted: both validators skip an unknown options
    /// name, an unknown name resolves to a default HttpResilienceOptions, and that default has
    /// Connection.Enabled false. Fails if the filter stops reading per-client options -- which would put an
    /// infinite handler lifetime on a client that never asked for one, around a pool with the runtime's
    /// infinite PooledConnectionLifetime, so nothing would recycle it for the life of the process.
    /// </remarks>
    [Fact]
    public void AnUnrelatedClient_IsNotTouchedByTheConnectionFilter()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(Settings.Enabled()
                .Set("Connection:Enabled", "true")
                .Set("Connection:MaxConnectionsPerServer", "42")
                .Set("Connection:PooledConnectionLifetime", "00:05:00")
                .Build())
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilience(configuration);
        services.AddHttpClient("configured").AddHttpResilience();

        // Never passed through AddHttpResilience.
        services.AddHttpClient("unrelated");

        using ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);

        // The filter runs on every handler build, so creating the unrelated client's handler runs it. The
        // type is not the assertion -- on .NET 10 the factory's own default primary handler is already a
        // SocketsHttpHandler -- so this asserts the settings, which is what "untouched" means.
        var unrelated = Assert.IsType<SocketsHttpHandler>(Innermost(provider, "unrelated"));
        Assert.Equal(int.MaxValue, unrelated.MaxConnectionsPerServer);
        Assert.NotEqual(TimeSpan.FromMinutes(5), unrelated.PooledConnectionLifetime);

        var configured = Assert.IsType<SocketsHttpHandler>(Innermost(provider, "configured"));
        Assert.Equal(42, configured.MaxConnectionsPerServer);
        Assert.Equal(TimeSpan.FromMinutes(5), configured.PooledConnectionLifetime);

        // Still the factory's own rotation, which is what recycles a pool with no lifetime of its own.
        Assert.Equal(
            TimeSpan.FromMinutes(2),
            provider.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>()
                .Get("unrelated").HandlerLifetime);
        Assert.Equal(
            Timeout.InfiniteTimeSpan,
            provider.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>()
                .Get("configured").HandlerLifetime);
    }

    private static HttpMessageHandler Innermost(ServiceProvider provider, string clientName)
    {
        HttpMessageHandler handler =
            provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(clientName);

        while (handler is DelegatingHandler delegating)
        {
            handler = delegating.InnerHandler!;
        }

        return handler;
    }
}
