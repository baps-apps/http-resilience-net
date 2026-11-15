using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HttpResilience.NET.Internal;

/// <summary>
/// Logs the startup notices (events 6, 10, 11, 14, 15, 21 once per client; 18 once per application), from each
/// client's final, validated options, before any hosted service starts.
/// </summary>
/// <remarks>
/// Runs in <see cref="IHostedLifecycleService.StartingAsync"/>, which the host calls after options validation
/// and before any <see cref="IHostedService.StartAsync"/>. Reading options through
/// <see cref="IOptionsMonitor{TOptions}"/> here, rather than logging from the options pipeline, means every
/// consumer <c>Configure</c> and <c>PostConfigure</c> has been applied. Independent of
/// <c>ValidateClientsOnStart</c>, so opting out of client creation does not silence these notices.
/// </remarks>
internal sealed class ClientStartupReport : IHostedLifecycleService
{
    private readonly HttpResilienceRegistration _registration;
    private readonly IOptionsMonitor<HttpResilienceOptions> _options;
    private readonly IOptions<HttpResilienceStartupOptions> _startup;
    private readonly ILogger? _logger;

    public ClientStartupReport(
        HttpResilienceRegistration registration,
        IOptionsMonitor<HttpResilienceOptions> options,
        IOptions<HttpResilienceStartupOptions> startup,
        ILoggerFactory? loggerFactory = null)
    {
        _registration = registration;
        _options = options;
        _startup = startup;
        _logger = loggerFactory?.CreateLogger(HttpResilienceLogging.ConfigurationCategory);
    }

    public Task StartingAsync(CancellationToken cancellationToken)
    {
        if (_logger is null)
        {
            return Task.CompletedTask;
        }

        // Once for the application: the keys belong to the file, not to a client.
        SchemaVersionNotice.Report(_logger, _registration, _startup.Value);

        foreach (RegisteredClient client in _registration.RegisteredClients)
        {
            cancellationToken.ThrowIfCancellationRequested();

            HttpResilienceOptions options = _options.Get(client.Name);
            if (!options.Enabled)
            {
                DisabledClientNotice.Report(_logger, client, options, _registration.Section);
                continue;
            }

            ClientSummaryNotice.Report(_logger, client, options);
            UnsafeMethodNotice.Report(_logger, client, options, _registration.Section);
            CircuitBreakerReachNotice.Report(_logger, client, options);
            RetryBudgetNotice.Report(_logger, client, options);
            HedgingFanOutNotice.Report(_logger, client, options);
        }

        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
