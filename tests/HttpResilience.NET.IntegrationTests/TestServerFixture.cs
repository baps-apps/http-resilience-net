using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace HttpResilience.NET.IntegrationTests;

/// <summary>
/// A real ASP.NET Core server, for the assertions that need genuine HTTP semantics rather than a stub.
/// </summary>
internal sealed class TestServerFixture : IAsyncDisposable
{
    private readonly IHost _host;
    private int _flakyCalls;
    private int _slowOrderDeliveries;

    private TestServerFixture(IHost host)
    {
        _host = host;
        TestServer server = host.GetTestServer();
        BaseAddress = server.BaseAddress?.ToString() ?? "http://localhost/";
        CreateHandler = server.CreateHandler;
    }

    public string BaseAddress { get; }

    public Func<HttpMessageHandler> CreateHandler { get; }

    /// <summary>Requests that reached each route, so tests can count what actually crossed the wire.</summary>
    public Dictionary<string, int> Calls { get; } = [];

    /// <summary>Bodies delivered to <c>/slow-orders</c>, in arrival order.</summary>
    public List<string> SlowOrderBodies { get; } = [];

    /// <summary>Bodies delivered to <c>/echo-orders</c>, in arrival order.</summary>
    public List<string> EchoedOrderBodies { get; } = [];

    /// <summary>How many requests reached <c>/slow-orders</c>, counted as they arrive rather than complete.</summary>
    public int SlowOrderDeliveries => Volatile.Read(ref _slowOrderDeliveries);

    public static async Task<TestServerFixture> StartAsync()
    {
        TestServerFixture? fixture = null;

        IHost host = await Host.CreateDefaultBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services => services.AddRouting());
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/ok", () => Results.Ok(new { status = "ok" }));
                        endpoints.MapPost("/orders", () => Results.StatusCode(500));
                        // Records what each attempt actually delivered, so a retry that replays an
                        // exhausted request stream shows up as an empty body rather than as a bare count.
                        endpoints.MapPost("/echo-orders", async (HttpContext context) =>
                        {
                            using var reader = new StreamReader(context.Request.Body);
                            string body = await reader.ReadToEndAsync(context.RequestAborted);
                            lock (fixture!.EchoedOrderBodies)
                            {
                                fixture.EchoedOrderBodies.Add(body);
                            }

                            return Results.StatusCode(500);
                        });
                        endpoints.MapGet("/error", () => Results.StatusCode(503));
                        endpoints.MapGet("/flaky", () =>
                            Interlocked.Increment(ref fixture!._flakyCalls) <= 2
                                ? Results.StatusCode(503)
                                : Results.Ok(new { status = "recovered" }));
                        // A mutating endpoint that is slow rather than failing. Hedging starts a
                        // supplementary attempt on a timer regardless of any outcome, so this is the shape
                        // of request that a guard written only as an outcome predicate would duplicate.
                        endpoints.MapPost("/slow-orders", async (HttpContext context) =>
                        {
                            Interlocked.Increment(ref fixture!._slowOrderDeliveries);
                            using var reader = new StreamReader(context.Request.Body);
                            string body = await reader.ReadToEndAsync(context.RequestAborted);
                            lock (fixture.SlowOrderBodies)
                            {
                                fixture.SlowOrderBodies.Add(body);
                            }

                            await Task.Delay(TimeSpan.FromSeconds(2), context.RequestAborted);
                            return Results.Ok();
                        });
                        // Honours the request abort token so a cancelled request tears down immediately
                        // instead of holding the test host open until the delay elapses.
                        endpoints.MapGet("/slow", async (HttpContext context) =>
                        {
                            await Task.Delay(TimeSpan.FromSeconds(30), context.RequestAborted);
                            return Results.Ok();
                        });
                    });
                });
            })
            .StartAsync();

        fixture = new TestServerFixture(host);
        return fixture;
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }
}
