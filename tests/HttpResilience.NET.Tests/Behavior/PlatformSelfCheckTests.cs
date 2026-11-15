using System.Diagnostics.Metrics;
using HttpResilience.NET.Internal;
using HttpResilience.NET.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;

namespace HttpResilience.NET.Tests.Behavior;

/// <summary>
/// The handler groups and the defaults replacement rely on <see cref="IHttpClientFactory"/> running handler and
/// client actions in registration order, which it does but does not document. The self-check runs that mechanism
/// against the platform version the application resolved, when the host starts, so a platform release that changes
/// the order fails the first start in any environment instead of misbehaving in production.
/// </summary>
public class PlatformSelfCheckTests
{
    /// <summary>
    /// Stands in for a platform release that runs the actions in a different order.
    /// </summary>
    public static TheoryData<string> OrderChanges() => ["HandlerActions", "ClientActions"];

    private static void Reverse(IServiceCollection services, string list) =>
        services.PostConfigureAll<HttpClientFactoryOptions>(options =>
        {
            if (list == "HandlerActions")
            {
                Reverse(options.HttpMessageHandlerBuilderActions);
            }
            else
            {
                Reverse(options.HttpClientActions);
            }
        });

    private static void Reverse<T>(IList<T> items)
    {
        List<T> reversed = [.. items.Reverse()];
        items.Clear();
        foreach (T item in reversed)
        {
            items.Add(item);
        }
    }

    /// <summary>The platform this suite runs on (latest, or the declared minimums) passes.</summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public void TheResolvedPlatform_PassesTheSelfCheck()
    {
        Assert.Null(new PlatformSelfCheck().Failure);
    }

    /// <summary>
    /// Fails if the self-check stops judging either list's order: the reversed list then passes.
    /// </summary>
    [Theory(Timeout = TestTimeouts.Default)]
    [MemberData(nameof(OrderChanges))]
    public void APlatformThatRunsActionsOutOfOrder_FailsTheSelfCheck(string list)
    {
        string? failure = new PlatformSelfCheck(services => Reverse(services, list)).Failure;

        Assert.NotNull(failure);
        Assert.Contains("Microsoft.Extensions.Http ", failure, StringComparison.Ordinal);
        Assert.Contains("Microsoft.Extensions.Http.Resilience ", failure, StringComparison.Ordinal);
        Assert.Contains("in registration order", failure, StringComparison.Ordinal);
        Assert.Contains("ValidateClientsOnStart", failure, StringComparison.Ordinal);
    }

    /// <summary>
    /// The host does not start on a platform that fails the check. Fails if the probe stops running it.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task HostStart_FailsOnAPlatformThatFailsTheSelfCheck()
    {
        await using ServiceProvider provider = Build(validateClientsOnStart: null);

        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(provider));

        Assert.Contains("in registration order", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>ValidateClientsOnStart: false</c> turns the self-check off with the client probe: both build clients at
    /// host start, which is what the switch declines.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task TheProbesOptOut_TurnsTheSelfCheckOff()
    {
        await using ServiceProvider provider = Build(validateClientsOnStart: "false");

        await StartAsync(provider);
    }

    /// <summary>
    /// The check builds clients with this package's gauges in a private container; none of it may reach the
    /// application's telemetry under the exported meter name.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public void TheSelfCheck_PublishesNothingUnderTheExportedMeterName()
    {
        List<string> selfCheckMeters = [];
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, _) =>
            {
                if (instrument.Meter.Scope is PlatformSelfCheck.MeterFactory)
                {
                    lock (selfCheckMeters)
                    {
                        selfCheckMeters.Add(instrument.Meter.Name);
                    }
                }
            }
        };
        listener.Start();

        Assert.Null(new PlatformSelfCheck(_ => { }).Failure);

        lock (selfCheckMeters)
        {
            Assert.NotEmpty(selfCheckMeters);
            Assert.DoesNotContain(HttpResilienceTelemetryExtensions.MeterName, selfCheckMeters);
        }
    }

    private static ServiceProvider Build(string? validateClientsOnStart)
    {
        Settings settings = Settings.Enabled();
        if (validateClientsOnStart is not null)
        {
            settings.Set("ValidateClientsOnStart", validateClientsOnStart);
        }

        var services = new ServiceCollection();
        services.AddHttpResilience(new ConfigurationBuilder().AddInMemoryCollection(settings.Build()).Build());
        services.AddHttpClient("test").AddHttpResilienceHandler()
            .ConfigurePrimaryHttpMessageHandler(() => new RecordingHandler());
        services.AddSingleton(new PlatformSelfCheck(check => Reverse(check, "HandlerActions")));

        return services.BuildServiceProvider(validateScopes: true);
    }

    private static async Task StartAsync(ServiceProvider provider)
    {
        foreach (IHostedService service in provider.GetServices<IHostedService>())
        {
            await service.StartAsync(TestContext.Current.CancellationToken);
        }
    }
}
