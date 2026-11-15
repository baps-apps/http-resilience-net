using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace HttpResilience.NET.Internal;

/// <summary>
/// Creates every client this package configured, once, while the host is starting.
/// </summary>
internal sealed class ClientStartupProbe : IHostedService
{
    private readonly IHttpClientFactory _factory;
    private readonly HttpResilienceRegistration _registration;
    private readonly IOptions<HttpResilienceStartupOptions> _startup;
    private readonly PlatformSelfCheck _selfCheck;

    public ClientStartupProbe(
        IHttpClientFactory factory,
        HttpResilienceRegistration registration,
        IOptions<HttpResilienceStartupOptions> startup,
        PlatformSelfCheck selfCheck)
    {
        _factory = factory;
        _registration = registration;
        _startup = startup;
        _selfCheck = selfCheck;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_startup.Value.ValidateClientsOnStart || _registration.Clients.Count == 0)
        {
            return Task.CompletedTask;
        }

        // First: on a platform that runs actions out of order, the clients below would be built wrongly, not fail.
        _selfCheck.ThrowIfFailed();

        foreach (string client in _registration.Clients)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _factory.CreateClient(client).Dispose();
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
