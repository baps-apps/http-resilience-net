using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace HttpResilience.NET.IntegrationTests;

/// <summary>
/// What a hedged client's authority allow-list does and does not bound.
/// </summary>
/// <remarks>
/// <see cref="System.Net.Http.SocketsHttpHandler"/> resolves a 3xx internally, below every
/// <see cref="DelegatingHandler"/> in the chain, so the allow-list handler never sees the second hop. These
/// tests need real sockets and a real primary handler: <c>TestServer</c>'s handler is not a
/// <c>SocketsHttpHandler</c> and does not follow redirects at all, so it could not fail.
/// </remarks>
public class RedirectTests
{
    /// <summary>
    /// A hedged client has declared its complete destination set, so it does not leave it.
    /// </summary>
    /// <remarks>
    /// The allow-list handler cannot enforce this -- a 3xx is resolved inside
    /// <see cref="System.Net.Http.SocketsHttpHandler"/>, below every handler in the chain -- so the bound has
    /// to come from not following the redirect at all. Production change that would make this fail: resolving
    /// <c>AllowAutoRedirect</c> to <see langword="true"/> for the hedging pipeline.
    /// </remarks>
    [Fact]
    public async Task HedgedClient_DoesNotLeaveItsAllowList_ByDefault()
    {
        await using var origin = await RedirectOrigin.StartAsync();

        using HttpResponseMessage response = await SendAsync(origin);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal(0, origin.UnlistedHits);
    }

    /// <summary>
    /// The bound holds without <c>Connection:Enabled</c>, which is an unrelated infrastructure switch.
    /// </summary>
    /// <remarks>
    /// A safety bound whose enforcement depends on a connection-pool flag is not a bound. Production change
    /// that would make this fail: applying the redirect setting only inside the <c>Connection:Enabled</c>
    /// branch of <c>ConnectionHandlerFilter</c>.
    /// </remarks>
    [Fact]
    public async Task HedgedClient_DoesNotLeaveItsAllowList_EvenWithConnectionTuningOff()
    {
        await using var origin = await RedirectOrigin.StartAsync();

        using HttpResponseMessage response = await SendAsync(origin, connectionEnabled: false);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal(0, origin.UnlistedHits);
    }

    /// <summary>
    /// Following redirects off the list stays possible, but only by saying so -- which puts the decision in
    /// the diff a reviewer reads, rather than in a default.
    /// </summary>
    [Fact]
    public async Task HedgedClient_FollowsRedirects_WhenExplicitlyOptedIn()
    {
        await using var origin = await RedirectOrigin.StartAsync();

        using HttpResponseMessage response = await SendAsync(origin, allowAutoRedirect: true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, origin.UnlistedHits);
    }

    /// <summary>
    /// A standard client keeps the runtime default. It has declared no destination set to leave: under
    /// <c>Mode: None</c> every host shares one pipeline, and even under <c>ByAuthority</c> an unlisted host is
    /// explicitly allowed and served by the shared pipeline. Changing this would break every client that talks
    /// to a CDN, a pre-signed URL or anything else that answers with a 302.
    /// </summary>
    [Fact]
    public async Task StandardClient_FollowsRedirects_LikeTheRuntimeDoes()
    {
        await using var origin = await RedirectOrigin.StartAsync();

        using HttpResponseMessage response = await SendAsync(origin, hedged: false);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, origin.UnlistedHits);
    }

    private static async Task<HttpResponseMessage> SendAsync(
        RedirectOrigin origin,
        bool? allowAutoRedirect = null,
        bool connectionEnabled = true,
        bool hedged = true)
    {
        var settings = new Dictionary<string, string?>
        {
            ["HttpResilience:Enabled"] = "true",
            ["HttpResilience:Connection:Enabled"] = connectionEnabled ? "true" : "false",
            ["HttpResilience:CircuitBreaker:MinimumThroughput"] = "1000",
            // Only the listed host. The redirect target is deliberately absent.
            ["HttpResilience:PipelineSelection:Authorities:0"] = origin.ListedAuthority
        };

        if (allowAutoRedirect is { } value)
        {
            settings["HttpResilience:Connection:AllowAutoRedirect"] = value ? "true" : "false";
        }

        if (!hedged)
        {
            // A standard client consults the list only to partition pipelines, so it must not state one
            // under the default selection mode.
            settings.Remove("HttpResilience:PipelineSelection:Authorities:0");
        }

        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilience(configuration);
        IHttpClientBuilder builder = services.AddHttpClient("search");
        _ = hedged ? builder.AddHedgedHttpResilience() : builder.AddHttpResilience();

        await using ServiceProvider provider = services.BuildServiceProvider();
        HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("search");

        return await client.GetAsync($"{origin.ListedAuthority}/start");
    }
}

/// <summary>
/// Two Kestrel servers on loopback: a listed one that redirects to an unlisted one that counts its hits.
/// </summary>
internal sealed class RedirectOrigin : IAsyncDisposable
{
    private readonly IHost _listed;
    private readonly IHost _unlisted;
    private int _unlistedHits;

    private RedirectOrigin(IHost listed, IHost unlisted, string listedAuthority, string unlistedAuthority)
    {
        _listed = listed;
        _unlisted = unlisted;
        ListedAuthority = listedAuthority;
        UnlistedAuthority = unlistedAuthority;
    }

    public string ListedAuthority { get; }

    public string UnlistedAuthority { get; }

    /// <summary>Requests that actually reached the authority the allow-list does not contain.</summary>
    public int UnlistedHits => Volatile.Read(ref _unlistedHits);

    public static async Task<RedirectOrigin> StartAsync()
    {
        RedirectOrigin? origin = null;

        IHost unlisted = await StartAsync(endpoints => endpoints.MapGet("/final", () =>
        {
            Interlocked.Increment(ref origin!._unlistedHits);
            return Results.Ok();
        }));

        string unlistedAuthority = AuthorityOf(unlisted);

        IHost listed = await StartAsync(endpoints => endpoints.MapGet(
            "/start", () => Results.Redirect($"{unlistedAuthority}/final", permanent: false)));

        origin = new RedirectOrigin(listed, unlisted, AuthorityOf(listed), unlistedAuthority);
        return origin;
    }

    public async ValueTask DisposeAsync()
    {
        await _listed.StopAsync();
        _listed.Dispose();
        await _unlisted.StopAsync();
        _unlisted.Dispose();
    }

    private static Task<IHost> StartAsync(Action<IEndpointRouteBuilder> routes)
    {
        IHost host = Host.CreateDefaultBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseKestrel();
                web.UseUrls("http://127.0.0.1:0");
                web.ConfigureServices(services => services.AddRouting());
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => routes(endpoints));
                });
            })
            .Build();

        return host.StartAsync().ContinueWith(_ => host, TaskScheduler.Default);
    }

    private static string AuthorityOf(IHost host) =>
        host.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/');
}
