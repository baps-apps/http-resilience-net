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
/// The unit tests invoke the probe directly, which proves it creates the clients but not that anything
/// invokes it. That assumption -- <c>IHost.StartAsync</c> runs registered hosted services before the process
/// serves traffic, and surfaces an exception from one of them by failing the start -- is what the whole
/// control rests on. It is the same gap <c>ARealHost_LogsTheDisabledClientWarning_WhileStarting</c> exists to
/// close for the disabled-client notice.
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

                IHttpClientBuilder builder = services.AddHttpClient("orders").AddHttpResilience();
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
    /// Nothing here calls <c>ValidateHttpResilienceClientsOnStart</c>. That is the assertion: the probe is
    /// the default, and a service gets this protection without knowing the method exists.
    /// </remarks>
    [Fact]
    public async Task ARealHost_FailsToStart_WhenAConfiguredClientCannotBeCreated()
    {
        using IHost host = BuildHost(unconfigurableHandler: true);

        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());

        Assert.Contains("Connection:Enabled", exception.Message, StringComparison.Ordinal);
        Assert.Contains("orders", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The contrast, and what the opt-out costs: with the key set to false the same host starts, reports
    /// healthy, and fails on the first request that reaches this client.
    /// </summary>
    /// <remarks>
    /// This is the behavior a service opts back into, so it is worth one test that shows what it is opting
    /// into rather than a sentence in a document. Production change that would make this fail: the opt-out
    /// key not being read, which would make the probe unavoidable during an incident.
    /// </remarks>
    [Fact]
    public async Task TheOptOutKey_LeavesTheSameHostStartingAndFailingLater()
    {
        using IHost host = BuildHost(unconfigurableHandler: true, validateClientsOnStart: "false");

        await host.StartAsync();
        try
        {
            Assert.Throws<InvalidOperationException>(
                () => host.Services.GetRequiredService<IHttpClientFactory>().CreateClient("orders"));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    /// <summary>
    /// A healthy service must still start. A probe that fails a good deployment is worse than no probe.
    /// </summary>
    [Fact]
    public async Task ARealHost_StartsCleanly_WhenEveryClientCanBeCreated()
    {
        using IHost host = BuildHost(unconfigurableHandler: false);

        await host.StartAsync();
        await host.StopAsync();
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }
}
