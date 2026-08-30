using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HttpResilience.NET.IntegrationTests;

/// <summary>
/// The pipeline over a real HTTP/2 connection.
/// </summary>
/// <remarks>
/// <c>Connection:EnableMultipleHttp2Connections</c> defaults to <see langword="true"/>, which is a deliberate
/// deviation from the runtime default of <see langword="false"/> -- and nothing exercised HTTP/2 at all, so
/// neither the deviation nor the pipeline's behavior on a multiplexed connection was covered by anything.
/// <para>
/// Prior-knowledge h2c (HTTP/2 over cleartext) rather than TLS, so the test needs no development certificate
/// and behaves the same on a developer machine and in CI. What is being asserted is the pipeline's behavior
/// over a multiplexed connection, not the TLS handshake, and ALPN is the runtime's business either way.
/// </para>
/// </remarks>
public class Http2Tests
{
    /// <summary>
    /// A request through the full pipeline really is HTTP/2, and is not silently downgraded by anything this
    /// package does to the primary handler.
    /// </summary>
    /// <remarks>
    /// Production change that would make this fail: replacing the primary handler with one that cannot
    /// negotiate HTTP/2, or forcing a version on the request. <c>SocketsHttpHandlerFactory</c> keeps a
    /// consumer's handler and sets six properties on it; if it ever started constructing a fresh handler in
    /// the <c>SocketsHttpHandler</c> branch, a consumer's <c>SslOptions</c> and protocol configuration would
    /// go with it.
    /// </remarks>
    [Fact]
    public async Task ARequestThroughThePipeline_UsesHttp2()
    {
        await using var origin = await Http2Origin.StartAsync();
        await using ServiceProvider provider = Build(connectionEnabled: true);

        using HttpResponseMessage response = await SendAsync(provider, origin, "/ok");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpVersion.Version20, response.Version);
        Assert.Equal(1, origin.Requests);
    }

    /// <summary>
    /// Retries work over a multiplexed connection: the attempts are separate streams on one connection, and
    /// the origin sees every one of them.
    /// </summary>
    /// <remarks>
    /// Worth asserting separately from the HTTP/1.1 retry tests because on HTTP/2 a failed attempt does not
    /// close a connection, so a retry loop that depended on connection teardown to make progress would
    /// behave differently here.
    /// </remarks>
    [Fact]
    public async Task Retries_WorkOverAMultiplexedConnection()
    {
        await using var origin = await Http2Origin.StartAsync();
        await using ServiceProvider provider = Build(connectionEnabled: true);

        using HttpResponseMessage response = await SendAsync(provider, origin, "/fail");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        // 1 + Retry:MaxRetries.
        Assert.Equal(3, origin.Requests);
    }

    /// <summary>
    /// The schema's HTTP/2 setting must actually reach the handler. It is the one connection property whose
    /// default deviates from the runtime's, so a mapping that silently dropped it would leave every client
    /// on <see langword="false"/> while the configuration reference said otherwise.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EnableMultipleHttp2Connections_ReachesThePrimaryHandler(bool enabled)
    {
        using ServiceProvider provider = Build(
            connectionEnabled: true,
            extra: settings =>
                settings["HttpResilience:Connection:EnableMultipleHttp2Connections"] =
                    enabled ? "true" : "false");

        // Force the handler chain to be built, then read the primary handler back out of it.
        using HttpClient _ = provider.GetRequiredService<IHttpClientFactory>().CreateClient("h2");

        SocketsHttpHandler handler = PrimaryHandler(provider);

        Assert.Equal(enabled, handler.EnableMultipleHttp2Connections);
    }

    /// <summary>
    /// With connection tuning off the package must not touch the property at all, leaving whatever the
    /// runtime and the consumer decided.
    /// </summary>
    [Fact]
    public void WithConnectionTuningOff_TheHandlerKeepsItsOwnHttp2Setting()
    {
        using ServiceProvider provider = Build(
            connectionEnabled: false,
            configureClient: builder => builder.ConfigurePrimaryHttpMessageHandler(
                () => new SocketsHttpHandler { EnableMultipleHttp2Connections = true }));

        using HttpClient _ = provider.GetRequiredService<IHttpClientFactory>().CreateClient("h2");

        Assert.True(PrimaryHandler(provider).EnableMultipleHttp2Connections);
    }

    private static SocketsHttpHandler PrimaryHandler(IServiceProvider provider)
    {
        HttpMessageHandler handler = provider
            .GetRequiredService<IHttpMessageHandlerFactory>()
            .CreateHandler("h2");

        while (handler is DelegatingHandler delegating)
        {
            handler = delegating.InnerHandler!;
        }

        return Assert.IsType<SocketsHttpHandler>(handler);
    }

    private static async Task<HttpResponseMessage> SendAsync(
        IServiceProvider provider, Http2Origin origin, string path)
    {
        HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("h2");

        // Prior knowledge: no TLS, so there is no ALPN to negotiate with and the version has to be stated.
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{origin.Authority}{path}")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        return await client.SendAsync(request);
    }

    private static ServiceProvider Build(
        bool connectionEnabled,
        Action<Dictionary<string, string?>>? extra = null,
        Action<IHttpClientBuilder>? configureClient = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["HttpResilience:Enabled"] = "true",
            ["HttpResilience:Timeout:Total"] = "00:00:30",
            ["HttpResilience:Timeout:Attempt"] = "00:00:10",
            ["HttpResilience:Retry:MaxRetries"] = "2",
            ["HttpResilience:Retry:BaseDelay"] = "00:00:00",
            ["HttpResilience:Retry:BackoffType"] = "Constant",
            ["HttpResilience:CircuitBreaker:MinimumThroughput"] = "1000",
            ["HttpResilience:Connection:Enabled"] = connectionEnabled ? "true" : "false",
            ["HttpResilience:Connection:PooledConnectionLifetime"] = "00:01:00",
            ["HttpResilience:Connection:PooledConnectionIdleTimeout"] = "00:00:30",
            ["HttpResilience:Connection:ConnectTimeout"] = "00:00:05"
        };

        extra?.Invoke(settings);

        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilience(configuration);
        IHttpClientBuilder builder = services.AddHttpClient("h2").AddHttpResilience();
        configureClient?.Invoke(builder);

        return services.BuildServiceProvider();
    }
}

/// <summary>A Kestrel server speaking HTTP/2 prior-knowledge over cleartext on loopback.</summary>
internal sealed class Http2Origin : IAsyncDisposable
{
    private readonly IHost _host;
    private int _requests;

    private Http2Origin(IHost host, string authority)
    {
        _host = host;
        Authority = authority;
    }

    public string Authority { get; }

    public int Requests => Volatile.Read(ref _requests);

    public static async Task<Http2Origin> StartAsync()
    {
        Http2Origin? origin = null;

        IHost host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureWebHost(web =>
            {
                web.UseKestrel(options =>
                {
                    // 127.0.0.1 rather than localhost: Kestrel refuses dynamic port binding on the
                    // localhost alias, which resolves to two addresses.
                    options.Listen(System.Net.IPAddress.Loopback, 0, listen =>
                        // Http2 only: no upgrade path, so a client that failed to speak HTTP/2 would be
                        // refused rather than quietly served over 1.1 -- which is what makes the version
                        // assertion mean something.
                        listen.Protocols = HttpProtocols.Http2);
                });
                web.ConfigureServices(services => services.AddRouting());
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/ok", () =>
                        {
                            Interlocked.Increment(ref origin!._requests);
                            return Results.Ok();
                        });

                        endpoints.MapGet("/fail", () =>
                        {
                            Interlocked.Increment(ref origin!._requests);
                            return Results.StatusCode(StatusCodes.Status500InternalServerError);
                        });
                    });
                });
            })
            .Build();

        await host.StartAsync();

        string address = host.Services
            .GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.First();

        origin = new Http2Origin(host, address.TrimEnd('/'));
        return origin;
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }
}
