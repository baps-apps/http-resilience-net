using System.Net;
using System.Text;
using HttpResilience.NET.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HttpResilience.NET.Tests.Behavior;

/// <summary>
/// <c>AddHttpResilience(Action&lt;HttpResilienceOptions&gt;)</c>: root values in code, with or without configuration.
/// </summary>
/// <remarks>
/// Order of precedence: the root section, then the root delegate, then the client's section, then the client's
/// own <c>configure</c>. The root delegate is held to the same per-client rule as the root section: it cannot
/// let every client repeat a mutating request.
/// </remarks>
public class CodeOnlyRegistrationTests
{
    private static IConfigurationRoot Configuration(Settings settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings.Build()).Build();

    private static HttpResilienceOptions Options(IServiceProvider provider, string name) =>
        provider.GetRequiredService<IOptionsMonitor<HttpResilienceOptions>>().Get(name);

    /// <summary>
    /// Fails if the root delegate is not applied to the client's pipeline.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task CodeOnlyRegistration_ReachesThePipeline()
    {
        var services = new ServiceCollection();
        services.AddHttpResilience(options =>
        {
            options.Retry.MaxRetries = 4;
            options.Retry.BaseDelay = TimeSpan.Zero;
        });

        var origin = new RecordingHandler(HttpStatusCode.InternalServerError);
        services.AddHttpClient("orders")
            .AddHttpResilienceHandler()
            .ConfigurePrimaryHttpMessageHandler(() => origin);

        await using ServiceProvider provider = services.BuildServiceProvider();
        (await provider.GetRequiredService<IHttpClientFactory>().CreateClient("orders")
            .GetAsync("http://origin.test/x", TestContext.Current.CancellationToken)).Dispose();

        Assert.Equal(5, origin.Count);
    }

    /// <summary>
    /// Values set in code are validated exactly like values from configuration.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public void CodeOnlyRegistration_IsValidated()
    {
        var services = new ServiceCollection();
        services.AddHttpResilience(options => options.Timeout.Attempt = options.Timeout.Total);

        OptionsValidationException exception = StartupAssert.Fails(
            services, () => services.AddHttpClient("orders").AddHttpResilienceHandler());

        Assert.Contains("Timeout:Attempt", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The root delegate supplies defaults: it overrides the root section, and a client's section overrides it.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task RootDelegate_AppliesAfterTheRootSection_AndBeforeTheClientSection()
    {
        var services = new ServiceCollection();
        services.AddHttpResilience(
            Configuration(Settings.Enabled()
                .Set("Timeout:Total", "00:00:30")
                .ForClient("orders", "Timeout:Total", "00:00:12")),
            options => options.Timeout.Total = TimeSpan.FromSeconds(15));

        services.AddHttpClient("orders").AddHttpResilienceHandler();
        services.AddHttpClient("search").AddHttpResilienceHandler();

        await using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Equal(TimeSpan.FromSeconds(12), Options(provider, "orders").Timeout.Total);
        Assert.Equal(TimeSpan.FromSeconds(15), Options(provider, "search").Timeout.Total);
    }

    /// <summary>
    /// The same order with an explicit section: the section's root, then the delegate, then the section's own
    /// <c>Clients</c> child.
    /// </summary>
    /// <remarks>
    /// Fails if the overload reads the default <c>HttpResilience</c> section (both clients would get 30 s, the
    /// decoy) or drops the delegate (search would get 20 s).
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task AnExplicitSectionAndARootDelegate_ComposeLikeTheDefaultSection()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HttpResilience:Timeout:Total"] = "00:00:30",
                ["Platform:Http:Timeout:Total"] = "00:00:20",
                ["Platform:Http:Clients:orders:Timeout:Total"] = "00:00:12"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddHttpResilience(
            configuration.GetSection("Platform:Http"),
            options => options.Timeout.Total = TimeSpan.FromSeconds(15));
        services.AddHttpClient("orders").AddHttpResilienceHandler();
        services.AddHttpClient("search").AddHttpResilienceHandler();

        await using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Equal(TimeSpan.FromSeconds(12), Options(provider, "orders").Timeout.Total);
        Assert.Equal(TimeSpan.FromSeconds(15), Options(provider, "search").Timeout.Total);
    }

    /// <summary>
    /// A shared library may register configuration and the application code, in either order.
    /// </summary>
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConfigurationAndCode_CombineInEitherOrder(bool codeFirst)
    {
        var services = new ServiceCollection();
        IConfigurationRoot configuration = Configuration(Settings.Enabled().Set("Timeout:Attempt", "00:00:04"));
        Action<HttpResilienceOptions> code = options => options.Retry.MaxRetries = 5;

        if (codeFirst)
        {
            services.AddHttpResilience(code);
            services.AddHttpResilience(configuration);
        }
        else
        {
            services.AddHttpResilience(configuration);
            services.AddHttpResilience(code);
        }

        services.AddHttpClient("orders").AddHttpResilienceHandler();
        await using ServiceProvider provider = services.BuildServiceProvider();

        HttpResilienceOptions options = Options(provider, "orders");
        Assert.Equal(TimeSpan.FromSeconds(4), options.Timeout.Attempt);
        Assert.Equal(5, options.Retry.MaxRetries);
    }

    /// <summary>
    /// Two root delegates both apply, in registration order.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task TwoRootDelegates_BothApply_InOrder()
    {
        var services = new ServiceCollection();
        services.AddHttpResilience(options => options.Retry.MaxRetries = 3);
        services.AddHttpResilience(options => options.Retry.MaxRetries += 1);
        services.AddHttpClient("orders").AddHttpResilienceHandler();

        await using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Equal(4, Options(provider, "orders").Retry.MaxRetries);
    }

    /// <summary>
    /// Rule 4 holds for the root delegate as it does for the root section: every client inherits it, so it
    /// cannot switch off a safe-method guard or add an unsafe method.
    /// </summary>
    /// <remarks>
    /// Fails if the root delegate's statements are not checked: the POST below would be retried and reach the
    /// origin three times.
    /// </remarks>
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData("retry-flag")]
    [InlineData("hedging-flag")]
    [InlineData("allow-list")]
    public async Task RootDelegate_CannotLetEveryClientRepeatAMutatingRequest(string statement)
    {
        var services = new ServiceCollection();
        services.AddHttpResilience(options =>
        {
            options.Retry.BaseDelay = TimeSpan.Zero;
            switch (statement)
            {
                case "retry-flag": options.Retry.DisableForUnsafeHttpMethods = false; break;
                case "hedging-flag": options.Hedging.DisableForUnsafeHttpMethods = false; break;
                default: options.Retry.RetryableMethods.Add("POST"); break;
            }
        });

        var origin = new RecordingHandler(HttpStatusCode.InternalServerError);
        services.AddHttpClient("orders")
            .AddHttpResilienceHandler()
            .ConfigurePrimaryHttpMessageHandler(() => origin);

        await using ServiceProvider provider = services.BuildServiceProvider();

        // Without a host nothing runs startup validation, so this is the first point the client is built.
        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IHttpClientFactory>().CreateClient("orders"));

        Assert.Contains("AddHttpResilience", exception.Message, StringComparison.Ordinal);
        Assert.Contains("HttpResilience:Clients:{name}", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, origin.Count);
    }

    /// <summary>
    /// The per-client route stays open: a client's own <c>configure</c> may opt in, as its section may.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task APerClientOptIn_IsStillAllowed_AlongsideARootDelegate()
    {
        var services = new ServiceCollection();
        services.AddHttpResilience(options => options.Retry.BaseDelay = TimeSpan.Zero);

        var origin = new RecordingHandler(HttpStatusCode.InternalServerError);
        services.AddHttpClient("payments")
            .AddHttpResilienceHandler(options => options.Retry.RetryableMethods.Add("POST"))
            .ConfigurePrimaryHttpMessageHandler(() => origin);

        await using ServiceProvider provider = services.BuildServiceProvider();
        using var content = new StringContent("x", Encoding.UTF8, "text/plain");
        (await provider.GetRequiredService<IHttpClientFactory>().CreateClient("payments")
            .PostAsync("http://origin.test/x", content, TestContext.Current.CancellationToken)).Dispose();

        Assert.Equal(3, origin.Count);
    }
}
