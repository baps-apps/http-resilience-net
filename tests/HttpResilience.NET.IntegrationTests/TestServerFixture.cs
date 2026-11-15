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
    private int _orderDeliveries;
    private int _slowOrderDeliveries;
    private int _slowFirstArrivals;
    private int _heldInFlight;
    private int _maxHeldInFlight;
    private readonly TaskCompletionSource _releaseHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _slowArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _slowAborted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private TestServerFixture(IHost host)
    {
        _host = host;
        TestServer server = host.GetTestServer();
        BaseAddress = server.BaseAddress?.ToString() ?? "http://localhost/";
        CreateHandler = server.CreateHandler;
    }

    public string BaseAddress { get; }

    public Func<HttpMessageHandler> CreateHandler { get; }

    /// <summary>How many requests reached <c>/orders</c>, counted as they arrive.</summary>
    public int OrderDeliveries => Volatile.Read(ref _orderDeliveries);

    /// <summary>Bodies delivered to <c>/orders</c>, in arrival order.</summary>
    public List<string> OrderBodies { get; } = [];

    /// <summary>How many requests reached <c>/slow-first</c>.</summary>
    public int SlowFirstArrivals => Volatile.Read(ref _slowFirstArrivals);

    /// <summary>Bodies delivered to <c>/slow-orders</c>, in arrival order.</summary>
    public List<string> SlowOrderBodies { get; } = [];

    /// <summary>How many requests reached <c>/slow-orders</c>, counted as they arrive rather than complete.</summary>
    public int SlowOrderDeliveries => Volatile.Read(ref _slowOrderDeliveries);

    /// <summary>Requests currently held at <c>/held</c>.</summary>
    public int HeldInFlight => Volatile.Read(ref _heldInFlight);

    /// <summary>The most requests held at <c>/held</c> at the same time.</summary>
    public int MaxHeldInFlight => Volatile.Read(ref _maxHeldInFlight);

    /// <summary>Completes once a request has reached <c>/slow</c>.</summary>
    public Task SlowArrived => _slowArrived.Task;

    /// <summary>Completes once the server has seen a <c>/slow</c> request aborted by its caller.</summary>
    public Task SlowAborted => _slowAborted.Task;

    /// <summary>Lets every request held at <c>/held</c>, and every later one, complete.</summary>
    public void ReleaseHeld() => _releaseHeld.TrySetResult();

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
                        endpoints.MapPost("/orders", async (HttpContext context) =>
                        {
                            Interlocked.Increment(ref fixture!._orderDeliveries);
                            using var reader = new StreamReader(context.Request.Body);
                            string body = await reader.ReadToEndAsync(context.RequestAborted);
                            lock (fixture.OrderBodies)
                            {
                                fixture.OrderBodies.Add(body);
                            }

                            return Results.StatusCode(500);
                        });
                        // The first request stalls; every later one answers at once with a different body,
                        // so a response saying "hedge" can only have come from a hedged attempt.
                        endpoints.MapGet("/slow-first", async (HttpContext context) =>
                        {
                            if (Interlocked.Increment(ref fixture!._slowFirstArrivals) == 1)
                            {
                                await Task.Delay(TimeSpan.FromSeconds(5), context.RequestAborted);
                                return Results.Text("primary");
                            }

                            return Results.Text("hedge");
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
                        // Records arrival and the caller's abort, so cancellation is asserted on the server side.
                        endpoints.MapGet("/slow", async (HttpContext context) =>
                        {
                            fixture!._slowArrived.TrySetResult();
                            try
                            {
                                await Task.Delay(TimeSpan.FromSeconds(30), context.RequestAborted);
                            }
                            catch (OperationCanceledException)
                            {
                                fixture._slowAborted.TrySetResult();
                                throw;
                            }

                            return Results.Ok();
                        });
                        // Holds every request until ReleaseHeld, counting how many are in flight at once, so a
                        // concurrency limit is visible at the server.
                        endpoints.MapGet("/held", async (HttpContext context) =>
                        {
                            int now = Interlocked.Increment(ref fixture!._heldInFlight);
                            int seen;
                            while (now > (seen = Volatile.Read(ref fixture._maxHeldInFlight)) &&
                                   Interlocked.CompareExchange(ref fixture._maxHeldInFlight, now, seen) != seen)
                            {
                            }

                            try
                            {
                                await fixture._releaseHeld.Task.WaitAsync(context.RequestAborted);
                                return Results.Ok();
                            }
                            finally
                            {
                                Interlocked.Decrement(ref fixture._heldInFlight);
                            }
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
        // A test that fails while requests are held must not wait for them in shutdown.
        ReleaseHeld();
        await _host.StopAsync();
        _host.Dispose();
    }
}
