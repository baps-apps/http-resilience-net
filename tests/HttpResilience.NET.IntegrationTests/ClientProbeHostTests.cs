using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HttpResilience.NET.IntegrationTests;

/// <summary>
/// The client startup probe has to run when a real host starts, not merely when a test resolves
/// <see cref="IHostedService"/> and calls it by hand.
/// </summary>
/// <remarks>
/// The unit tests invoke the probe directly; this checks that <c>IHost.StartAsync</c> runs it and fails the
/// start on an exception. Same idea as <c>ARealHost_LogsTheDisabledClientWarning_WhileStarting</c>.
/// </remarks>
public class ClientProbeHostTests
{
    private static IHost BuildHost(bool unconfigurableHandler, string? validateClientsOnStart = null)
    {
        return Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services =>
            {
                var settings = new Dictionary<string, string?>
                {
                    ["HttpResilience:Enabled"] = "true",
                    ["HttpResilience:Connection:Enabled"] = "true",
                    ["HttpResilience:Connection:PooledConnectionLifetime"] = "00:01:00",
                    ["HttpResilience:Connection:PooledConnectionIdleTimeout"] = "00:00:30"
                };

                if (validateClientsOnStart is not null)
                {
                    settings["HttpResilience:ValidateClientsOnStart"] = validateClientsOnStart;
                }

                IConfigurationRoot configuration = new ConfigurationBuilder()
                    .AddInMemoryCollection(settings)
                    .Build();

                services.AddHttpResilience(configuration);

                IHttpClientBuilder builder = services.AddHttpClient("orders").AddHttpResilienceHandler();
                if (unconfigurableHandler)
                {
                    // Not a SocketsHttpHandler, so Connection:Enabled cannot be applied to it and the
                    // package refuses rather than disabling factory rotation around a pool nothing recycles.
                    builder.ConfigurePrimaryHttpMessageHandler(() => new StubHandler());
                }
            })
            .Build();
    }

    /// <summary>
    /// Production change that would make this fail: the probe not being an <see cref="IHostedService"/>,
    /// not being registered by <c>AddHttpResilience</c>, or swallowing the exception. Without it this host
    /// starts and serves traffic with a client that throws on every request.
    /// </summary>
    /// <remarks>
    /// Nothing here asks for the probe. That is the assertion: it is the default.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ARealHost_FailsToStart_WhenAConfiguredClientCannotBeCreated()
    {
        using IHost host = BuildHost(unconfigurableHandler: true);

        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("Connection:Enabled", exception.Message, StringComparison.Ordinal);
        Assert.Contains("orders", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The contrast, and what the opt-out costs: with the key set to false the same host starts, reports
    /// healthy, and fails on the first request that reaches this client.
    /// </summary>
    /// <remarks>
    /// Fails if the opt-out key is not read.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task TheOptOutKey_LeavesTheSameHostStartingAndFailingLater()
    {
        using IHost host = BuildHost(unconfigurableHandler: true, validateClientsOnStart: "false");

        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            Assert.Throws<InvalidOperationException>(
                () => host.Services.GetRequiredService<IHttpClientFactory>().CreateClient("orders"));
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// A healthy service must still start. A probe that fails a good deployment is worse than no probe.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ARealHost_StartsCleanly_WhenEveryClientCanBeCreated()
    {
        using IHost host = BuildHost(unconfigurableHandler: false);

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }
}
