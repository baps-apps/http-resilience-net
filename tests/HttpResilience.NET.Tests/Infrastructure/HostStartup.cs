using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace HttpResilience.NET.Tests.Infrastructure;

/// <summary>
/// Runs what a generic host runs before its services start: options validation, then every
/// <see cref="IHostedLifecycleService.StartingAsync"/>. The startup notices are logged there.
/// </summary>
internal static class HostStartup
{
    public static void Run(IServiceProvider provider)
    {
        foreach (IStartupValidator validator in provider.GetServices<IStartupValidator>())
        {
            validator.Validate();
        }

        foreach (IHostedService service in provider.GetServices<IHostedService>())
        {
            if (service is IHostedLifecycleService lifecycle)
            {
                lifecycle.StartingAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
        }
    }
}
