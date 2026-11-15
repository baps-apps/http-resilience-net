using HttpResilience.NET.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HttpResilience.NET.Tests.Behavior;

/// <summary>
/// A section under <c>HttpResilience:Clients</c> that no registered client reads must fail startup.
/// </summary>
/// <remarks>
/// Otherwise such a section binds to nothing and the client silently runs on root defaults. Common causes: a
/// typed client reads <c>Clients:{TClient}</c> (<c>IOrdersApi</c>, not <c>OrdersApi</c>), or a renamed or
/// deleted client leaves its section behind.
/// </remarks>
public class UnusedClientSectionTests
{
    private static async Task<string> AssertFailsAtStartupAsync(Action<IServiceCollection> register)
    {
        ServiceProvider? provider = null;
        Exception? captured = Record.Exception(() =>
        {
            var services = new ServiceCollection();
            register(services);
            provider = services.BuildServiceProvider(validateScopes: true);
            foreach (IStartupValidator validator in provider.GetServices<IStartupValidator>())
            {
                validator.Validate();
            }
        });

        if (provider is not null)
        {
            await provider.DisposeAsync();
        }

        Assert.NotNull(captured);
        return captured is AggregateException aggregate
            ? string.Join(" | ", aggregate.InnerExceptions.Select(e => e.Message))
            : captured.Message;
    }

    private static async Task AssertStartsCleanlyAsync(Action<IServiceCollection> register)
    {
        var services = new ServiceCollection();
        register(services);
        await using ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);
        foreach (IStartupValidator validator in provider.GetServices<IStartupValidator>())
        {
            validator.Validate();
        }
    }

    private static IConfigurationRoot Configuration(Settings settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings.Build()).Build();

    /// <summary>
    /// Fails if the unused-section check is removed: 'Orders' would then run on the root's 20-second total
    /// timeout instead of its section's 10 seconds.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task AMisspelledClientSection_FailsStartup_AndNamesTheSectionsThatAreRead()
    {
        string message = await AssertFailsAtStartupAsync(services =>
        {
            services.AddHttpResilience(Configuration(Settings.Enabled()
                .ForClient("Ordres", "Timeout:Total", "00:01:00")));
            services.AddHttpClient("Orders").AddHttpResilienceHandler();
        });

        Assert.Contains("HttpResilience:Clients:Ordres", message, StringComparison.Ordinal);
        Assert.Contains("Orders", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The typed-client shape from the README quick start: the section is named after <c>TClient</c>.
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ATypedClientSectionNamedAfterTheImplementation_FailsStartup_AndExplainsTheLeadingI()
    {
        string message = await AssertFailsAtStartupAsync(services =>
        {
            services.AddHttpResilience(Configuration(Settings.Enabled()
                .ForClient("OrdersApi", "Timeout:Total", "00:01:00")));
            services.AddHttpClient<IOrdersApi, OrdersApi>().AddHttpResilienceHandler();
        });

        Assert.Contains("HttpResilience:Clients:OrdersApi", message, StringComparison.Ordinal);
        Assert.Contains("IOrdersApi", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The ordinary case must not fire.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ASectionAClientActuallyReads_StartsCleanly()
    {
        await AssertStartsCleanlyAsync(services =>
        {
            services.AddHttpResilience(Configuration(Settings.Enabled()
                .ForClient("Orders", "Timeout:Total", "00:01:00")));
            services.AddHttpClient("Orders").AddHttpResilienceHandler();
        });
    }

    /// <summary>
    /// A client pointed at another section reads that section; the one named after the client is not
    /// demanded.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ASectionReadUnderAnExplicitName_StartsCleanly()
    {
        await AssertStartsCleanlyAsync(services =>
        {
            services.AddHttpResilience(Configuration(Settings.Enabled()
                .ForClient("Downstream", "Timeout:Total", "00:01:00")));
            services.AddHttpClient("Orders").AddHttpResilienceHandler("Downstream");
            services.AddHttpClient("Billing").AddHttpResilienceHandler("Downstream");
        });
    }

    /// <summary>
    /// One configuration file may be shared by services registering different clients. The escape hatch is
    /// root-only and defaults to failing.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task TheEscapeHatch_AllowsASectionNoClientReads()
    {
        await AssertStartsCleanlyAsync(services =>
        {
            services.AddHttpResilience(Configuration(Settings.Enabled()
                .Set("AllowUnusedClientSections", "true")
                .ForClient("Ordres", "Timeout:Total", "00:01:00")));
            services.AddHttpClient("Orders").AddHttpResilienceHandler();
        });
    }

    /// <summary>
    /// A non-boolean value fails rather than quietly meaning false.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task TheEscapeHatchWithANonBooleanValue_FailsStartup()
    {
        string message = await AssertFailsAtStartupAsync(services =>
        {
            services.AddHttpResilience(Configuration(Settings.Enabled()
                .Set("AllowUnusedClientSections", "yes")));
            services.AddHttpClient("Orders").AddHttpResilienceHandler();
        });

        Assert.Contains("AllowUnusedClientSections", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every unused section is reported, not just the first.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task EveryUnusedSection_IsReportedTogether()
    {
        string message = await AssertFailsAtStartupAsync(services =>
        {
            services.AddHttpResilience(Configuration(Settings.Enabled()
                .ForClient("Ordres", "Timeout:Total", "00:01:00")
                .ForClient("Payment", "Timeout:Total", "00:01:00")));
            services.AddHttpClient("Orders").AddHttpResilienceHandler();
            services.AddHttpClient("Payments").AddHttpResilienceHandler();
        });

        Assert.Contains("HttpResilience:Clients:Ordres", message, StringComparison.Ordinal);
        Assert.Contains("HttpResilience:Clients:Payment", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A hedged client reads its section the same way, so it must count as a reader of it.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task AHedgedClientsOwnSection_StartsCleanly()
    {
        await AssertStartsCleanlyAsync(services =>
        {
            services.AddHttpResilience(Configuration(Settings.Hedged()
                .ForClient("Search", "Hedging:Delay", "00:00:00.300")));
            services.AddHttpClient("Search").AddHttpResilienceHedgingHandler();
        });
    }

    /// <summary>
    /// A container with no client registrations (a library registering only the root) must not fail: the
    /// check runs at startup, after every registration.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task SectionsWithNoClientsRegisteredAtAll_FailStartup()
    {
        string message = await AssertFailsAtStartupAsync(services =>
            services.AddHttpResilience(Configuration(Settings.Enabled()
                .ForClient("Orders", "Timeout:Total", "00:01:00"))));

        Assert.Contains("HttpResilience:Clients:Orders", message, StringComparison.Ordinal);
    }

    internal interface IOrdersApi;

    internal sealed class OrdersApi : IOrdersApi
    {
        public OrdersApi(HttpClient client) => Client = client;

        public HttpClient Client { get; }
    }
}
