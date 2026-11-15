using HttpResilience.NET.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace HttpResilience.NET.Tests.Behavior;

/// <summary>
/// <c>ConfigurePrimaryHttpMessageHandler</c> is last-wins; <c>SetHandlerLifetime</c> is not. A client could
/// end up with factory rotation disabled and a handler whose <c>PooledConnectionLifetime</c> is infinite, so
/// the pool is never recycled and DNS never refreshed.
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
        builder.AddHttpResilienceHandler();
        after(builder);

        return services.BuildServiceProvider(validateScopes: true);
    }

    private static HttpClient CreateClient(ServiceProvider provider) =>
        provider.GetRequiredService<IHttpClientFactory>().CreateClient("test");

    /// <summary>
    /// Factory rotation is disabled because PooledConnectionLifetime bounds connection age, so a replacement
    /// handler with the runtime's infinite default must still get the configured lifetime.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
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
    /// Registration order must not decide whether connection settings take effect.
    /// </summary>
    [Theory(Timeout = TestTimeouts.Default)]
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
    /// A handler configured before registration carries settings this package cannot express (certificate,
    /// proxy, TLS callback), so it is configured, not replaced.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
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
    /// A connection setting the consumer gave its own <see cref="SocketsHttpHandler"/> would be overwritten by a
    /// different configured value, so client creation fails, naming the key, instead.
    /// </summary>
    /// <remarks>
    /// One case per setting the check compares, so removing any one comparison fails its case. Each configured
    /// value differs from the runtime default, and the handler's from both.
    /// </remarks>
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(nameof(SocketsHttpHandler.ConnectTimeout))]
    [InlineData(nameof(SocketsHttpHandler.PooledConnectionIdleTimeout))]
    [InlineData(nameof(SocketsHttpHandler.PooledConnectionLifetime))]
    [InlineData(nameof(SocketsHttpHandler.EnableMultipleHttp2Connections))]
    [InlineData(nameof(SocketsHttpHandler.MaxConnectionsPerServer))]
    public void ASocketsHandlerSetting_TheSchemaWouldOverwrite_FailsClientCreation(string setting)
    {
        var consumerHandler = new SocketsHttpHandler
        {
            ConnectTimeout = setting is nameof(SocketsHttpHandler.ConnectTimeout)
                ? TimeSpan.FromSeconds(42) : Timeout.InfiniteTimeSpan,
            PooledConnectionIdleTimeout = setting is nameof(SocketsHttpHandler.PooledConnectionIdleTimeout)
                ? TimeSpan.FromSeconds(10) : TimeSpan.FromMinutes(1),
            PooledConnectionLifetime = setting is nameof(SocketsHttpHandler.PooledConnectionLifetime)
                ? TimeSpan.FromSeconds(20) : Timeout.InfiniteTimeSpan,
            EnableMultipleHttp2Connections = setting is nameof(SocketsHttpHandler.EnableMultipleHttp2Connections),
            MaxConnectionsPerServer = setting is nameof(SocketsHttpHandler.MaxConnectionsPerServer) ? 7 : int.MaxValue
        };

        using ServiceProvider provider = BuildWith(
            consumerHandler,
            ("Connection:EnableMultipleHttp2Connections", "false"),
            ("Connection:MaxConnectionsPerServer", "42"));

        InvalidOperationException exception =
            Assert.Throws<InvalidOperationException>(() => CreateClient(provider));

        Assert.Contains("'test'", exception.Message, StringComparison.Ordinal);
        Assert.Contains($"Connection:{setting}", exception.Message, StringComparison.Ordinal);
        Assert.Single(ConflictsIn(exception.Message));
    }

    /// <summary>
    /// A handler value equal to the configured one loses nothing, so the client is created.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public void ASocketsHandlerSetting_EqualToTheConfiguredValue_IsAccepted()
    {
        var consumerHandler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            ConnectTimeout = TimeSpan.FromSeconds(3),
            EnableMultipleHttp2Connections = true
        };

        using ServiceProvider provider = BuildWith(consumerHandler);

        CreateClient(provider).Dispose();

        Assert.Equal(TimeSpan.FromMinutes(1), consumerHandler.PooledConnectionLifetime);
    }

    /// <summary>
    /// <c>MaxConnectionsPerServer</c> is written only when stated, so an unstated key leaves the handler's
    /// value in place rather than failing.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public void AnUnstatedMaxConnectionsPerServer_KeepsTheHandlersValue()
    {
        var consumerHandler = new SocketsHttpHandler { MaxConnectionsPerServer = 7 };

        using ServiceProvider provider = BuildWith(consumerHandler);

        CreateClient(provider).Dispose();

        Assert.Equal(7, consumerHandler.MaxConnectionsPerServer);
    }

    private static ServiceProvider BuildWith(SocketsHttpHandler handler, params (string Key, string Value)[] settings)
    {
        Settings values = Settings.Enabled()
            .Set("Connection:Enabled", "true")
            .Set("Connection:PooledConnectionLifetime", "00:01:00")
            .Set("Connection:PooledConnectionIdleTimeout", "00:00:30");
        foreach ((string key, string value) in settings)
        {
            values = values.Set(key, value);
        }

        var services = new ServiceCollection();
        services.AddHttpResilience(new ConfigurationBuilder().AddInMemoryCollection(values.Build()).Build());
        services.AddHttpClient("test")
            .ConfigurePrimaryHttpMessageHandler(() => handler)
            .AddHttpResilienceHandler();

        return services.BuildServiceProvider(validateScopes: true);
    }

    private static string[] ConflictsIn(string message) =>
        message.Split(" (Connection:", StringSplitOptions.None)[1..];

    /// <summary>
    /// A primary handler that is not a <c>SocketsHttpHandler</c> has no pooled-connection lifetime, so
    /// disabling rotation around it must fail instead.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
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
    /// An <see cref="HttpClientHandler"/> with nothing set carries nothing to lose, so it is replaced by a
    /// configured <see cref="SocketsHttpHandler"/> and disposed.
    /// </summary>
    /// <remarks>
    /// Fails if the replacement stops happening (the connection settings would be missing) or if the
    /// discarded handler is no longer disposed.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public void ADefaultHttpClientHandler_IsReplacedAndDisposed()
    {
        var consumerHandler = new HttpClientHandler();

        using ServiceProvider provider = Build(
            before: builder => builder.ConfigurePrimaryHttpMessageHandler(() => consumerHandler),
            after: _ => { });

        var replacement = Assert.IsType<SocketsHttpHandler>(Innermost(provider, "test"));
        Assert.Equal(TimeSpan.FromMinutes(1), replacement.PooledConnectionLifetime);

        // A disposed handler refuses configuration.
        Assert.Throws<ObjectDisposedException>(() => consumerHandler.MaxAutomaticRedirections = 5);
    }

    /// <summary>
    /// Replacing an <see cref="HttpClientHandler"/> that carries settings would drop them without a word: a
    /// client certificate (mTLS), a certificate-validation callback (pinning), a proxy, credentials. Client
    /// creation fails instead, naming each setting.
    /// </summary>
    /// <remarks>
    /// One case per setting the check compares, so removing any one comparison fails its case.
    /// </remarks>
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(nameof(HttpClientHandler.ClientCertificates))]
    [InlineData(nameof(HttpClientHandler.ClientCertificateOptions))]
    [InlineData(nameof(HttpClientHandler.ServerCertificateCustomValidationCallback))]
    [InlineData(nameof(HttpClientHandler.SslProtocols))]
    [InlineData(nameof(HttpClientHandler.CheckCertificateRevocationList))]
    [InlineData(nameof(HttpClientHandler.Proxy))]
    [InlineData(nameof(HttpClientHandler.UseProxy))]
    [InlineData(nameof(HttpClientHandler.DefaultProxyCredentials))]
    [InlineData(nameof(HttpClientHandler.Credentials))]
    [InlineData(nameof(HttpClientHandler.UseDefaultCredentials))]
    [InlineData(nameof(HttpClientHandler.PreAuthenticate))]
    [InlineData(nameof(HttpClientHandler.CookieContainer))]
    [InlineData(nameof(HttpClientHandler.UseCookies))]
    [InlineData(nameof(HttpClientHandler.AutomaticDecompression))]
    [InlineData(nameof(HttpClientHandler.AllowAutoRedirect))]
    [InlineData(nameof(HttpClientHandler.MaxAutomaticRedirections))]
    [InlineData(nameof(HttpClientHandler.MaxConnectionsPerServer))]
    [InlineData(nameof(HttpClientHandler.MaxResponseHeadersLength))]
    [InlineData(nameof(HttpClientHandler.MeterFactory))]
    [InlineData(nameof(HttpClientHandler.Properties))]
    public void AConfiguredHttpClientHandler_FailsClientCreation_NamingTheSetting(string setting)
    {
        HttpClientHandler consumerHandler = ConfiguredClientHandler(setting);

        using ServiceProvider provider = Build(
            before: builder => builder.ConfigurePrimaryHttpMessageHandler(() => consumerHandler),
            after: _ => { });

        InvalidOperationException exception =
            Assert.Throws<InvalidOperationException>(() => CreateClient(provider));

        Assert.Contains($"'test'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Connection:Enabled", exception.Message, StringComparison.Ordinal);
        Assert.Contains(setting, exception.Message, StringComparison.Ordinal);
        Assert.Contains("SocketsHttpHandler", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A subclass may override <c>SendAsync</c>; replacing it would drop that behavior too.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public void AnHttpClientHandlerSubclass_FailsClientCreation()
    {
        using ServiceProvider provider = Build(
            before: builder => builder.ConfigurePrimaryHttpMessageHandler(() => new DerivedClientHandler()),
            after: _ => { });

        InvalidOperationException exception =
            Assert.Throws<InvalidOperationException>(() => CreateClient(provider));

        Assert.Contains(nameof(DerivedClientHandler), exception.Message, StringComparison.Ordinal);
    }

    private static HttpClientHandler ConfiguredClientHandler(string setting)
    {
        var handler = new HttpClientHandler();
        switch (setting)
        {
            case nameof(HttpClientHandler.ClientCertificates):
                handler.ClientCertificates.Add(SelfSignedCertificate());
                break;
            case nameof(HttpClientHandler.ClientCertificateOptions):
                handler.ClientCertificateOptions = ClientCertificateOption.Automatic;
                break;
            case nameof(HttpClientHandler.ServerCertificateCustomValidationCallback):
                handler.ServerCertificateCustomValidationCallback = static (_, _, _, _) => true;
                break;
            case nameof(HttpClientHandler.SslProtocols):
                handler.SslProtocols = System.Security.Authentication.SslProtocols.Tls13;
                break;
            case nameof(HttpClientHandler.CheckCertificateRevocationList):
                handler.CheckCertificateRevocationList = true;
                break;
            case nameof(HttpClientHandler.Proxy):
                handler.Proxy = new System.Net.WebProxy("http://proxy.test:8080");
                break;
            case nameof(HttpClientHandler.UseProxy):
                handler.UseProxy = false;
                break;
            case nameof(HttpClientHandler.DefaultProxyCredentials):
                handler.DefaultProxyCredentials = new System.Net.NetworkCredential("user", "secret");
                break;
            case nameof(HttpClientHandler.Credentials):
                handler.Credentials = new System.Net.NetworkCredential("user", "secret");
                break;
            case nameof(HttpClientHandler.UseDefaultCredentials):
                handler.UseDefaultCredentials = true;
                break;
            case nameof(HttpClientHandler.PreAuthenticate):
                handler.PreAuthenticate = true;
                break;
            case nameof(HttpClientHandler.CookieContainer):
                handler.CookieContainer.Add(new System.Net.Cookie("session", "1", "/", "origin.test"));
                break;
            case nameof(HttpClientHandler.UseCookies):
                handler.UseCookies = false;
                break;
            case nameof(HttpClientHandler.AutomaticDecompression):
                handler.AutomaticDecompression = System.Net.DecompressionMethods.GZip;
                break;
            case nameof(HttpClientHandler.AllowAutoRedirect):
                handler.AllowAutoRedirect = false;
                break;
            case nameof(HttpClientHandler.MaxAutomaticRedirections):
                handler.MaxAutomaticRedirections = 3;
                break;
            case nameof(HttpClientHandler.MaxConnectionsPerServer):
                handler.MaxConnectionsPerServer = 10;
                break;
            case nameof(HttpClientHandler.MaxResponseHeadersLength):
                handler.MaxResponseHeadersLength = 128;
                break;
            case nameof(HttpClientHandler.MeterFactory):
                handler.MeterFactory = new StubMeterFactory();
                break;
            case nameof(HttpClientHandler.Properties):
                handler.Properties["tenant"] = "a";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(setting), setting, "No case for this setting.");
        }

        return handler;
    }

    private static System.Security.Cryptography.X509Certificates.X509Certificate2 SelfSignedCertificate()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=client.test",
            rsa,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);

        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private sealed class DerivedClientHandler : HttpClientHandler;

    private sealed class StubMeterFactory : System.Diagnostics.Metrics.IMeterFactory
    {
        public System.Diagnostics.Metrics.Meter Create(System.Diagnostics.Metrics.MeterOptions options) =>
            new(options);

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// Nothing here applies when the package was never asked to own connection settings.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ConnectionDisabled_LeavesThePrimaryHandlerAlone()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(Settings.Enabled());

        await harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, harness.Origin.Count);
    }

    /// <summary>
    /// The connection filter is process-wide, so it runs on every handler build in the application once any
    /// client enables connection settings. A client this package never registered must come out untouched.
    /// </summary>
    /// <remarks>
    /// Fails if the filter stops reading per-client options, which would put an infinite handler lifetime on a
    /// client that never asked for one.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
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
        services.AddHttpClient("configured").AddHttpResilienceHandler();

        // Never passed through AddHttpResilienceHandler.
        services.AddHttpClient("unrelated");

        using ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);

        // The filter runs on every handler build. On .NET 10 the default primary handler is already a
        // SocketsHttpHandler, so the settings are asserted, not the type.
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
