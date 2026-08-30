using System.Net;
using System.Threading.RateLimiting;
using HttpResilience.NET.Internal;
using HttpResilience.NET.Options;
using HttpResilience.NET.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace HttpResilience.NET.Tests.Behavior;

public class RegistrationTests
{
    [Fact]
    public void AddingResilienceWithoutRegisteringConfiguration_FailsWithAnActionableMessage()
    {
        var services = new ServiceCollection();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => services.AddHttpClient("orders").AddHttpResilience());

        Assert.Contains("AddHttpResilience(configuration)", exception.Message, StringComparison.Ordinal);
        Assert.Contains("orders", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A service that adds the package and configures nothing gets no pipeline. <c>Enabled</c> is opt-in.
    /// </summary>
    /// <remarks>
    /// Pins the default by behavior rather than by reading the property: one origin call means no retry
    /// strategy ran. The state is deliberately indistinguishable from a client that set <c>Enabled: false</c>,
    /// which is why <c>DisabledClient_WarnsAtStartup_BeforeAnyClientIsCreated</c> exists -- a forgotten key
    /// has to leave a Warning in the deployment's logs, because nothing else about it is visible.
    /// </remarks>
    [Fact]
    public async Task NoConfigurationAtAll_AddsNoPipeline()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(Settings.Empty());

        await harness.GetAsync();

        Assert.Equal(1, harness.Origin.Count);
    }

    [Fact]
    public async Task Disabled_AddsNoPipeline_AndPassesRequestsStraightThrough()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Empty().Set("Enabled", "false"));

        HttpResponseMessage response = await harness.GetAsync();

        Assert.Equal(1, harness.Origin.Count);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    /// <summary>
    /// Connection settings are infrastructure, not policy. Turning the pipeline off during an incident must
    /// not also silently revert the client to default connection behavior.
    /// </summary>
    [Fact]
    public void Disabled_StillAppliesConnectionSettings()
    {
        using ServiceProvider provider = ResilienceHarness.BuildProvider(
            Settings.Empty()
                .Set("Enabled", "false")
                .Set("Connection:Enabled", "true")
                .Set("Connection:MaxConnectionsPerServer", "42"),
            origin: null);

        SocketsHttpHandler handler = PrimaryHandler(provider, "test");

        Assert.Equal(42, handler.MaxConnectionsPerServer);
    }

    /// <summary>
    /// The connection defaults are a shared package's opinion applied to every consumer that turns
    /// <c>Connection:Enabled</c> on, so they are pinned rather than left to whatever the type initialiser says.
    /// </summary>
    /// <remarks>
    /// <c>ConnectTimeout</c> covers TCP <i>and</i> the TLS handshake. At 2 seconds a cross-AZ or cross-region
    /// connect on a loaded node can lose the race and be reported as a connect failure, which then retries --
    /// amplifying load at exactly the wrong moment. 3 seconds keeps a clear margin below the 5-second attempt
    /// budget that validation requires it to sit under.
    /// <para>
    /// <c>PooledConnectionIdleTimeout</c> was equal to <c>PooledConnectionLifetime</c>, which made it inert:
    /// no connection could ever reach the idle bound before the age bound retired it. 1 minute is the runtime's
    /// own default and gives it something to do.
    /// </para>
    /// </remarks>
    [Fact]
    public void ConnectionDefaults_AreTheOnesTheSchemaDocuments()
    {
        using ServiceProvider provider = ResilienceHarness.BuildProvider(
            Settings.Enabled().Set("Connection:Enabled", "true"),
            origin: null);

        SocketsHttpHandler handler = PrimaryHandler(provider, "test");

        Assert.Equal(TimeSpan.FromSeconds(3), handler.ConnectTimeout);
        Assert.Equal(TimeSpan.FromMinutes(1), handler.PooledConnectionIdleTimeout);
        Assert.Equal(TimeSpan.FromMinutes(2), handler.PooledConnectionLifetime);
        Assert.True(handler.EnableMultipleHttp2Connections);
        Assert.True(handler.AllowAutoRedirect);
        Assert.True(handler.PooledConnectionIdleTimeout < handler.PooledConnectionLifetime,
            "an idle timeout at or above the connection lifetime can never fire.");
    }

    [Fact]
    public void ConnectionSettings_ReachTheHandler()
    {
        using ServiceProvider provider = ResilienceHarness.BuildProvider(
            Settings.Enabled()
                .Set("Connection:Enabled", "true")
                .Set("Connection:MaxConnectionsPerServer", "64")
                .Set("Connection:ConnectTimeout", "00:00:03")
                .Set("Connection:PooledConnectionLifetime", "00:01:00")
                .Set("Connection:PooledConnectionIdleTimeout", "00:00:45")
                .Set("Connection:EnableMultipleHttp2Connections", "false"),
            origin: null);

        SocketsHttpHandler handler = PrimaryHandler(provider, "test");

        Assert.Equal(64, handler.MaxConnectionsPerServer);
        Assert.Equal(TimeSpan.FromSeconds(3), handler.ConnectTimeout);
        Assert.Equal(TimeSpan.FromMinutes(1), handler.PooledConnectionLifetime);
        Assert.Equal(TimeSpan.FromSeconds(45), handler.PooledConnectionIdleTimeout);
        Assert.False(handler.EnableMultipleHttp2Connections);
    }

    /// <summary>
    /// PooledConnectionLifetime already bounds connection age and DNS staleness. Leaving the factory's own
    /// rotation on as well would cycle the pool twice as often, and would make the configured lifetime a
    /// value that never actually takes effect.
    /// </summary>
    [Fact]
    public void ConnectionEnabled_DisablesFactoryHandlerRotation()
    {
        using ServiceProvider provider = ResilienceHarness.BuildProvider(
            Settings.Enabled().Set("Connection:Enabled", "true"), origin: null);

        Assert.Equal(Timeout.InfiniteTimeSpan, HandlerLifetime(provider, "test"));
    }

    [Fact]
    public void MaxConnectionsPerServer_DefaultsToTheRuntimeDefault()
    {
        using ServiceProvider provider = ResilienceHarness.BuildProvider(
            Settings.Enabled().Set("Connection:Enabled", "true"), origin: null);

        // Unset means unlimited, not an arbitrary cap that would throttle throughput invisibly.
        Assert.Equal(int.MaxValue, PrimaryHandler(provider, "test").MaxConnectionsPerServer);
    }

    [Fact]
    public async Task PerClientSection_OverridesRootValues_AndInheritsTheRest()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled()
                .Set("Retry:MaxRetries", "2")
                .ForClient("Orders", "Retry:MaxRetries", "4")
                .ForClient("Orders", "Timeout:Total", "00:01:00"),
            sectionName: "Orders");

        await harness.GetAsync();

        // Overridden by the client section.
        Assert.Equal(5, harness.Origin.Count);
    }

    [Fact]
    public async Task ConfigureDelegate_IsAppliedLast()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled().Set("Retry:MaxRetries", "2"),
            configure: options =>
            {
                options.Retry.MaxRetries = 1;
                options.Retry.RetryableMethods = ["POST"];
            });

        await harness.SendAsync(HttpMethod.Post);

        Assert.Equal(2, harness.Origin.Count);
    }

    /// <summary>
    /// A client named after a schema property must not collide with it. Per-client sections live under their
    /// own <c>Clients</c> child precisely so that names like "Retry" or "Timeout" are ordinary client names.
    /// </summary>
    [Theory]
    [InlineData("Retry")]
    [InlineData("Timeout")]
    [InlineData("Hedging")]
    [InlineData("Enabled")]
    public async Task ClientNamesCannotCollideWithSchemaProperties(string clientName)
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled()
                .Set("Retry:MaxRetries", "2")
                .ForClient(clientName, "Retry:MaxRetries", "1"),
            sectionName: clientName);

        await harness.GetAsync();

        Assert.Equal(2, harness.Origin.Count);
    }

    [Fact]
    public async Task RateLimiter_IsAPerClientSingleton_OwnedAndDisposedByTheContainer()
    {
        Settings settings = Settings.Enabled()
            .Set("RateLimiter:Enabled", "true")
            .Set("RateLimiter:PermitLimit", "10");

        RateLimiter limiter;
        await using (ServiceProvider provider = ResilienceHarness.BuildProvider(settings, clientName: "orders"))
        {
            limiter = provider.GetRequiredKeyedService<RateLimiter>(new RateLimiterKey("orders"));
            Assert.Same(limiter, provider.GetRequiredKeyedService<RateLimiter>(new RateLimiterKey("orders")));

            // Statistics prove the limiter is live before disposal.
            Assert.NotNull(limiter.GetStatistics());
        }

        Assert.Throws<ObjectDisposedException>(() => limiter.GetStatistics());
    }

    [Fact]
    public void EachClientGetsItsOwnRateLimiter()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(Settings.Enabled()
                .Set("RateLimiter:Enabled", "true")
                .Set("RateLimiter:PermitLimit", "10")
                .Build())
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilience(configuration);
        services.AddHttpClient("sso").AddHttpResilience();
        services.AddHttpClient("mis").AddHttpResilience();

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.NotSame(
            provider.GetRequiredKeyedService<RateLimiter>(new RateLimiterKey("sso")),
            provider.GetRequiredKeyedService<RateLimiter>(new RateLimiterKey("mis")));
    }

    /// <summary>
    /// Each client contributes one validator scoped to its own options name, plus one shared validator for
    /// the root. What must never happen is several validators all validating the same options instance:
    /// that reports every failure once per client and buries the real message.
    /// </summary>
    [Fact]
    public void EachOptionsInstance_IsValidatedByExactlyOneValidator()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(Settings.Enabled().Build())
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilience(configuration);
        for (int i = 0; i < 5; i++)
        {
            services.AddHttpClient($"client-{i}").AddHttpResilience();
        }

        using ServiceProvider provider = services.BuildServiceProvider();
        IValidateOptions<HttpResilienceOptions>[] validators =
            [.. provider.GetServices<IValidateOptions<HttpResilienceOptions>>()];

        // A deliberately invalid instance: exactly one validator should fail it, the rest should skip.
        var invalid = new HttpResilienceOptions { Enabled = true };
        invalid.Retry.MaxRetries = 0;

        foreach (string name in new[] { "", "client-0", "client-3" })
        {
            int failed = validators.Count(v => v.Validate(name, invalid).Failed);
            Assert.Equal(1, failed);
        }

        // And a name nothing registered is nobody's business.
        Assert.All(validators, v => Assert.True(v.Validate("unregistered", invalid).Skipped));
    }

    private static SocketsHttpHandler PrimaryHandler(IServiceProvider provider, string clientName)
    {
        IHttpMessageHandlerFactory factory = provider.GetRequiredService<IHttpMessageHandlerFactory>();
        HttpMessageHandler handler = factory.CreateHandler(clientName);

        while (handler is DelegatingHandler delegating)
        {
            handler = delegating.InnerHandler!;
        }

        return Assert.IsType<SocketsHttpHandler>(handler);
    }

    private static TimeSpan HandlerLifetime(IServiceProvider provider, string clientName)
    {
        return provider
            .GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>()
            .Get(clientName)
            .HandlerLifetime;
    }
}
