using System.Net;
using System.Threading.RateLimiting;
using HttpResilience.NET.Internal;
using HttpResilience.NET.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace HttpResilience.NET.Tests.Behavior;

public class RegistrationTests
{
    /// <summary>
    /// Without <c>services.AddHttpResilience</c> there is no section to read. That surfaces when the client is
    /// created, naming the missing call, rather than as a client silently running without a pipeline.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public void AddingResilienceWithoutRegisteringConfiguration_FailsOnCreation_WithAnActionableMessage()
    {
        var services = new ServiceCollection();
        services.AddHttpClient("orders").AddHttpResilienceHandler();

        using ServiceProvider provider = services.BuildServiceProvider();
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<IHttpClientFactory>().CreateClient("orders"));

        Assert.Contains("AddHttpResilience(configuration)", exception.Message, StringComparison.Ordinal);
        Assert.Contains("orders", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Registering the client is the opt-in: with no configuration at all it gets the default pipeline.
    /// </summary>
    /// <remarks>
    /// Three origin calls are the default two retries. The delay is zeroed only to keep the test fast.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task NoConfigurationAtAll_AppliesTheDefaultPipeline()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Empty(),
            configure: options => options.Retry.BaseDelay = TimeSpan.Zero);

        await harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, harness.Origin.Count);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task Disabled_AddsNoPipeline_AndPassesRequestsStraightThrough()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Empty().Set("Enabled", "false"));

        HttpResponseMessage response = await harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, harness.Origin.Count);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    /// <summary>
    /// Turning the pipeline off during an incident must not also revert connection settings.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
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
    /// The connection defaults apply to every consumer that enables <c>Connection:Enabled</c>, so they are
    /// pinned.
    /// </summary>
    /// <remarks>
    /// <c>ConnectTimeout</c> (3 s) covers TCP and TLS and leaves a margin below the 5-second attempt budget.
    /// <c>PooledConnectionIdleTimeout</c> (1 minute, the runtime default) is below the lifetime so it can fire.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
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

    [Fact(Timeout = TestTimeouts.Default)]
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
    /// PooledConnectionLifetime already bounds connection age; factory rotation on top would cycle the pool
    /// twice as often.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public void ConnectionEnabled_DisablesFactoryHandlerRotation()
    {
        using ServiceProvider provider = ResilienceHarness.BuildProvider(
            Settings.Enabled().Set("Connection:Enabled", "true"), origin: null);

        Assert.Equal(Timeout.InfiniteTimeSpan, HandlerLifetime(provider, "test"));
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void MaxConnectionsPerServer_DefaultsToTheRuntimeDefault()
    {
        using ServiceProvider provider = ResilienceHarness.BuildProvider(
            Settings.Enabled().Set("Connection:Enabled", "true"), origin: null);

        // Unset means unlimited, not an arbitrary cap that would throttle throughput invisibly.
        Assert.Equal(int.MaxValue, PrimaryHandler(provider, "test").MaxConnectionsPerServer);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task PerClientSection_OverridesRootValues_AndInheritsTheRest()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled()
                .Set("Retry:MaxRetries", "2")
                .ForClient("Orders", "Retry:MaxRetries", "4")
                .ForClient("Orders", "Timeout:Total", "00:01:00"),
            sectionName: "Orders");

        await harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Overridden by the client section.
        Assert.Equal(5, harness.Origin.Count);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ConfigureDelegate_IsAppliedLast()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled().Set("Retry:MaxRetries", "2"),
            configure: options =>
            {
                options.Retry.MaxRetries = 1;
                options.Retry.RetryableMethods.Add("POST");
            });

        await harness.SendAsync(HttpMethod.Post, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, harness.Origin.Count);
    }

    /// <summary>
    /// A client named after a schema property must not collide with it; client sections live under
    /// <c>Clients</c>.
    /// </summary>
    [Theory(Timeout = TestTimeouts.Default)]
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

        await harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, harness.Origin.Count);
    }

    [Fact(Timeout = TestTimeouts.Default)]
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

    [Fact(Timeout = TestTimeouts.Default)]
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
        services.AddHttpClient("sso").AddHttpResilienceHandler();
        services.AddHttpClient("mis").AddHttpResilienceHandler();

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.NotSame(
            provider.GetRequiredKeyedService<RateLimiter>(new RateLimiterKey("sso")),
            provider.GetRequiredKeyedService<RateLimiter>(new RateLimiterKey("mis")));
    }

    /// <summary>
    /// Each client has one validator scoped to its options name, plus one for the root. Several validators on
    /// one instance would repeat every failure once per client.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public void EachOptionsInstance_IsValidatedByExactlyOneValidator()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(Settings.Enabled().Build())
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilience(configuration);
        for (int i = 0; i < 5; i++)
        {
            services.AddHttpClient($"client-{i}").AddHttpResilienceHandler();
        }

        using ServiceProvider provider = services.BuildServiceProvider();
        IValidateOptions<HttpResilienceOptions>[] validators =
            [.. provider.GetServices<IValidateOptions<HttpResilienceOptions>>()];

        // A deliberately invalid instance: exactly one validator should fail it, the rest should skip. A value
        // every pipeline reads, so the root judges it as well as each client.
        var invalid = new HttpResilienceOptions { Enabled = true };
        invalid.CircuitBreaker.FailureRatio = 0;

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
