using HttpResilience.NET.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HttpResilience.NET.Tests.Behavior;

/// <summary>
/// A section under <c>HttpResilience:Clients</c> that no registered client reads must fail startup.
/// </summary>
/// <remarks>
/// This was the last silent-configuration path in the package. Everything else that produces a run-time state
/// indistinguishable from a mistake is loud -- the disabled-client Warning, the allow-list-under-Mode:None
/// failure, the Retry-keys-on-a-hedged-client failure -- but a section named after a client that does not
/// exist bound to nothing and said nothing. The client ran on root defaults, and the way you found out was an
/// incident.
/// <para>
/// Two things make it likely rather than theoretical. A typed client is named after <c>TClient</c>, so
/// <c>AddHttpClient&lt;IOrdersApi, OrdersApi&gt;()</c> reads <c>Clients:IOrdersApi</c> and an operator writes
/// <c>Clients:OrdersApi</c>. And a renamed or deleted client leaves its section behind with nothing to
/// notice.
/// </para>
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
    /// Production change that would make this fail: removing the unused-section check. Without it the
    /// container builds, the host starts, and 'Orders' silently runs on the root's 20-second total timeout
    /// rather than the 10 seconds the section states.
    /// </summary>
    [Fact]
    public async Task AMisspelledClientSection_FailsStartup_AndNamesTheSectionsThatAreRead()
    {
        string message = await AssertFailsAtStartupAsync(services =>
        {
            services.AddHttpResilience(Configuration(Settings.Enabled()
                .ForClient("Ordres", "Timeout:Total", "00:01:00")));
            services.AddHttpClient("Orders").AddHttpResilience();
        });

        Assert.Contains("HttpResilience:Clients:Ordres", message, StringComparison.Ordinal);
        Assert.Contains("Orders", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The exact shape the README's own quick start produces: a typed client is named after
    /// <c>TClient</c>, so the section an operator writes by hand is one letter away from the one that is read.
    /// </summary>
    [Fact]
    public async Task ATypedClientSectionNamedAfterTheImplementation_FailsStartup_AndExplainsTheLeadingI()
    {
        string message = await AssertFailsAtStartupAsync(services =>
        {
            services.AddHttpResilience(Configuration(Settings.Enabled()
                .ForClient("OrdersApi", "Timeout:Total", "00:01:00")));
            services.AddHttpClient<IOrdersApi, OrdersApi>().AddHttpResilience();
        });

        Assert.Contains("HttpResilience:Clients:OrdersApi", message, StringComparison.Ordinal);
        Assert.Contains("IOrdersApi", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The check must not fire on the ordinary case, or it is worse than no check at all.
    /// </summary>
    [Fact]
    public async Task ASectionAClientActuallyReads_StartsCleanly()
    {
        await AssertStartsCleanlyAsync(services =>
        {
            services.AddHttpResilience(Configuration(Settings.Enabled()
                .ForClient("Orders", "Timeout:Total", "00:01:00")));
            services.AddHttpClient("Orders").AddHttpResilience();
        });
    }

    /// <summary>
    /// A client may be pointed at someone else's section. The section it was pointed at is read; the one
    /// named after the client is not, and must not be demanded.
    /// </summary>
    [Fact]
    public async Task ASectionReadUnderAnExplicitName_StartsCleanly()
    {
        await AssertStartsCleanlyAsync(services =>
        {
            services.AddHttpResilience(Configuration(Settings.Enabled()
                .ForClient("Downstream", "Timeout:Total", "00:01:00")));
            services.AddHttpClient("Orders").AddHttpResilience("Downstream");
            services.AddHttpClient("Billing").AddHttpResilience("Downstream");
        });
    }

    /// <summary>
    /// <c>AddHttpResilience(string.Empty)</c> means root values only, so it consumes no client section --
    /// and a section named after that client is therefore inert, which is exactly what this reports.
    /// </summary>
    [Fact]
    public async Task ASectionForAClientThatAskedForRootValuesOnly_FailsStartup()
    {
        string message = await AssertFailsAtStartupAsync(services =>
        {
            services.AddHttpResilience(Configuration(Settings.Enabled()
                .ForClient("Orders", "Timeout:Total", "00:01:00")));
            services.AddHttpClient("Orders").AddHttpResilience(string.Empty);
        });

        Assert.Contains("HttpResilience:Clients:Orders", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A platform team may ship one configuration file to services that register different subsets of the
    /// clients in it. The escape hatch exists for that, is root-only, and defaults to failing.
    /// </summary>
    [Fact]
    public async Task TheEscapeHatch_AllowsASectionNoClientReads()
    {
        await AssertStartsCleanlyAsync(services =>
        {
            services.AddHttpResilience(Configuration(Settings.Enabled()
                .Set("AllowUnusedClientSections", "true")
                .ForClient("Ordres", "Timeout:Total", "00:01:00")));
            services.AddHttpClient("Orders").AddHttpResilience();
        });
    }

    /// <summary>
    /// A key whose value is not a boolean must not quietly mean false: that would leave an operator who
    /// wrote "yes" believing the hatch was open while startup failed for a reason it never mentioned.
    /// </summary>
    [Fact]
    public async Task TheEscapeHatchWithANonBooleanValue_FailsStartup()
    {
        string message = await AssertFailsAtStartupAsync(services =>
        {
            services.AddHttpResilience(Configuration(Settings.Enabled()
                .Set("AllowUnusedClientSections", "yes")));
            services.AddHttpClient("Orders").AddHttpResilience();
        });

        Assert.Contains("AllowUnusedClientSections", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every unused section is reported, not just the first. An operator fixing a configuration file one
    /// restart at a time is a poor use of a validator that has the whole file in front of it.
    /// </summary>
    [Fact]
    public async Task EveryUnusedSection_IsReportedTogether()
    {
        string message = await AssertFailsAtStartupAsync(services =>
        {
            services.AddHttpResilience(Configuration(Settings.Enabled()
                .ForClient("Ordres", "Timeout:Total", "00:01:00")
                .ForClient("Payment", "Timeout:Total", "00:01:00")));
            services.AddHttpClient("Orders").AddHttpResilience();
            services.AddHttpClient("Payments").AddHttpResilience();
        });

        Assert.Contains("HttpResilience:Clients:Ordres", message, StringComparison.Ordinal);
        Assert.Contains("HttpResilience:Clients:Payment", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A hedged client reads its section the same way, so it must count as a reader of it.
    /// </summary>
    [Fact]
    public async Task AHedgedClientsOwnSection_StartsCleanly()
    {
        await AssertStartsCleanlyAsync(services =>
        {
            services.AddHttpResilience(Configuration(Settings.Hedged()
                .ForClient("Search", "Hedging:Delay", "00:00:00.300")));
            services.AddHttpClient("Search").AddHedgedHttpResilience();
        });
    }

    /// <summary>
    /// A container with no client registrations at all -- a library that calls the root registration and
    /// leaves client wiring to the application -- must not fail on sections nobody has claimed yet.
    /// It cannot: the check runs at startup, by which point every registration has been made.
    /// </summary>
    [Fact]
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
