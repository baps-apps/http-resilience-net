using HttpResilience.NET.Options;
using HttpResilience.NET.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HttpResilience.NET.Tests.Behavior;

/// <summary>
/// What the options report is what the pipeline runs, and that holds <i>by construction</i> rather than by a
/// validator comparing two copies of the same values.
/// </summary>
/// <remarks>
/// The pipeline configurators read <see cref="IOptionsMonitor{TOptions}"/> inside the delegate the platform
/// invokes when it first builds the pipeline -- after every <c>Configure</c> and <c>PostConfigure</c> has
/// run. The values the pipeline is built from are therefore the same object a consumer reads back, so there
/// is nothing to keep in step.
/// <para>
/// The exception is the handful of options that decide <b>which handlers exist</b>. Handler composition is
/// fixed once the service collection is built, so those cannot honour a later change and are refused at
/// startup instead. That set is small and explicit; everything else composes the way the options pattern
/// says it should.
/// </para>
/// </remarks>
public class OptionsCompositionTests
{
    private static IConfigurationSection Configuration(Settings settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings.Build()).Build()
            .GetSection("HttpResilience");

    private static (ServiceCollection Services, RecordingHandler Origin) Client(
        Settings settings, string name = "t")
    {
        var services = new ServiceCollection();
        services.AddHttpResilience(Configuration(settings));

        var origin = new RecordingHandler();
        services.AddHttpClient(name).AddHttpResilience().ConfigurePrimaryHttpMessageHandler(() => origin);
        return (services, origin);
    }

    /// <summary>
    /// A <c>PostConfigure</c> registered after the client reaches the pipeline, exactly as it does for the
    /// platform's own options.
    /// </summary>
    /// <remarks>
    /// Fails if the pipeline goes back to being built from a snapshot captured at registration: the origin
    /// sees three calls rather than five, while the options report four attempts.
    /// </remarks>
    [Fact]
    public async Task PostConfigure_ReachesThePipeline_AndTheReportedOptions()
    {
        // A short attempt budget so that five attempts still fit inside the total one -- the live values are
        // validated like any others, and a schedule that cannot fit is rejected rather than silently run.
        (ServiceCollection services, RecordingHandler origin) =
            Client(Settings.Enabled().Set("Retry:MaxRetries", "2").Set("Timeout:Attempt", "00:00:02"));

        services.PostConfigure<HttpResilienceOptions>("t", options => options.Retry.MaxRetries = 4);

        await using ServiceProvider provider = services.BuildServiceProvider();
        (await provider.GetRequiredService<IHttpClientFactory>().CreateClient("t")
            .GetAsync("http://origin.test/x")).Dispose();

        Assert.Equal(4, provider.GetRequiredService<IOptionsMonitor<HttpResilienceOptions>>()
            .Get("t").Retry.MaxRetries);
        Assert.Equal(5, origin.Count);
    }

    /// <summary>
    /// The same for <c>Configure</c>, which runs in the earlier phase and used to lose to the snapshot.
    /// </summary>
    [Fact]
    public async Task Configure_ReachesThePipeline_AndTheReportedOptions()
    {
        (ServiceCollection services, RecordingHandler origin) =
            Client(Settings.Enabled().Set("Retry:MaxRetries", "2"));

        services.Configure<HttpResilienceOptions>("t", options => options.Retry.MaxRetries = 1);

        await using ServiceProvider provider = services.BuildServiceProvider();
        (await provider.GetRequiredService<IHttpClientFactory>().CreateClient("t")
            .GetAsync("http://origin.test/x")).Dispose();

        Assert.Equal(1, provider.GetRequiredService<IOptionsMonitor<HttpResilienceOptions>>()
            .Get("t").Retry.MaxRetries);
        Assert.Equal(2, origin.Count);
    }

    /// <summary>
    /// A timeout changed after registration is the value the pipeline runs on, not merely the value reported.
    /// </summary>
    [Fact]
    public async Task PostConfigure_ReachesTheTimeoutTheClientRunsOn()
    {
        (ServiceCollection services, RecordingHandler _) = Client(Settings.Enabled());

        services.PostConfigure<HttpResilienceOptions>("t", options =>
        {
            options.Timeout.Total = TimeSpan.FromSeconds(40);

            // Deliberately not Total + the default body allowance, which the registration snapshot would also
            // have produced -- the value has to be one only the late change can explain.
            options.Timeout.Client = TimeSpan.FromSeconds(154);
        });

        await using ServiceProvider provider = services.BuildServiceProvider();
        using HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("t");

        Assert.Equal(TimeSpan.FromSeconds(154), client.Timeout);
    }

    /// <summary>
    /// A value a later stage makes invalid still fails startup, with the rule's own message.
    /// </summary>
    /// <remarks>
    /// Reading live rather than from a snapshot must not mean the pipeline can be built from a value the
    /// validator never saw.
    /// </remarks>
    [Fact]
    public void PostConfigure_ToAnInvalidValue_StillFailsValidation()
    {
        (ServiceCollection services, RecordingHandler _) = Client(Settings.Enabled());

        services.PostConfigure<HttpResilienceOptions>("t",
            options => options.Timeout.Attempt = options.Timeout.Total + TimeSpan.FromSeconds(1));

        using ServiceProvider provider = services.BuildServiceProvider();

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptionsMonitor<HttpResilienceOptions>>().Get("t"));

        Assert.Contains("Timeout.Attempt", string.Join(" ", exception.Failures), StringComparison.Ordinal);
    }

    /// <summary>
    /// An option that decides whether a handler exists cannot be honoured after registration, so it is
    /// refused rather than reported without being in effect.
    /// </summary>
    /// <remarks>
    /// This is the residue of the snapshot comparison, and it is what stops the live read from turning a
    /// silent divergence into a first-request crash: enabling the rate limiter here would have the pipeline
    /// resolve a keyed limiter that was never registered.
    /// </remarks>
    [Theory]
    [InlineData("RateLimiter")]
    [InlineData("ConcurrencyLimiter")]
    [InlineData("Enabled")]
    public void PostConfiguringAStructuralOption_FailsAtStartup(string option)
    {
        (ServiceCollection services, RecordingHandler _) = Client(Settings.Enabled());

        services.PostConfigure<HttpResilienceOptions>("t", options =>
        {
            switch (option)
            {
                case "RateLimiter":
                    options.RateLimiter.Enabled = true;
                    options.RateLimiter.PermitLimit = 10;
                    break;
                case "ConcurrencyLimiter":
                    options.ConcurrencyLimiter.Enabled = true;
                    options.ConcurrencyLimiter.Limit = 10;
                    break;
                default:
                    options.Enabled = false;
                    break;
            }
        });

        using ServiceProvider provider = services.BuildServiceProvider();

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptionsMonitor<HttpResilienceOptions>>().Get("t"));

        string message = string.Join(" ", exception.Failures);
        Assert.Contains("registration", message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The <c>configure</c> parameter still reaches everything, and remains the clearest way to say it.
    /// </summary>
    [Fact]
    public async Task ConfigureParameter_StillReachesThePipelineAndTheOptions()
    {
        var services = new ServiceCollection();
        services.AddHttpResilience(Configuration(Settings.Enabled().Set("Retry:MaxRetries", "2")));

        var origin = new RecordingHandler();
        services.AddHttpClient("t")
            .AddHttpResilience(configure: options => options.Retry.MaxRetries = 1)
            .ConfigurePrimaryHttpMessageHandler(() => origin);

        await using ServiceProvider provider = services.BuildServiceProvider();
        (await provider.GetRequiredService<IHttpClientFactory>().CreateClient("t")
            .GetAsync("http://origin.test/x")).Dispose();

        Assert.Equal(1, provider.GetRequiredService<IOptionsMonitor<HttpResilienceOptions>>()
            .Get("t").Retry.MaxRetries);
        Assert.Equal(2, origin.Count);
    }
}
