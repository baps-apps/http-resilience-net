using HttpResilience.NET.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HttpResilience.NET.Tests.Behavior;

/// <summary>
/// What the options report is what the pipeline runs, by construction.
/// </summary>
/// <remarks>
/// The configurators read <see cref="IOptionsMonitor{TOptions}"/> inside the platform's build delegate, after
/// every <c>Configure</c> and <c>PostConfigure</c>, including the options that decide which handlers exist.
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
        services.AddHttpClient(name).AddHttpResilienceHandler().ConfigurePrimaryHttpMessageHandler(() => origin);
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
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task PostConfigure_ReachesThePipeline_AndTheReportedOptions()
    {
        // A short attempt budget so that five attempts still fit inside the total one -- the live values are
        // validated like any others, and a schedule that cannot fit is rejected rather than silently run.
        (ServiceCollection services, RecordingHandler origin) =
            Client(Settings.Enabled().Set("Retry:MaxRetries", "2").Set("Timeout:Attempt", "00:00:02"));

        services.PostConfigure<HttpResilienceOptions>("t", options => options.Retry.MaxRetries = 4);

        await using ServiceProvider provider = services.BuildServiceProvider();
        (await provider.GetRequiredService<IHttpClientFactory>().CreateClient("t")
            .GetAsync("http://origin.test/x", TestContext.Current.CancellationToken)).Dispose();

        Assert.Equal(4, provider.GetRequiredService<IOptionsMonitor<HttpResilienceOptions>>()
            .Get("t").Retry.MaxRetries);
        Assert.Equal(5, origin.Count);
    }

    /// <summary>
    /// The same for <c>Configure</c>, which runs in the earlier phase.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task Configure_ReachesThePipeline_AndTheReportedOptions()
    {
        (ServiceCollection services, RecordingHandler origin) =
            Client(Settings.Enabled().Set("Retry:MaxRetries", "2"));

        services.Configure<HttpResilienceOptions>("t", options => options.Retry.MaxRetries = 1);

        await using ServiceProvider provider = services.BuildServiceProvider();
        (await provider.GetRequiredService<IHttpClientFactory>().CreateClient("t")
            .GetAsync("http://origin.test/x", TestContext.Current.CancellationToken)).Dispose();

        Assert.Equal(1, provider.GetRequiredService<IOptionsMonitor<HttpResilienceOptions>>()
            .Get("t").Retry.MaxRetries);
        Assert.Equal(2, origin.Count);
    }

    /// <summary>
    /// A timeout changed after registration is the value the pipeline runs on, not merely the value reported.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
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
    [Fact(Timeout = TestTimeouts.Default)]
    public void PostConfigure_ToAnInvalidValue_StillFailsValidation()
    {
        (ServiceCollection services, RecordingHandler _) = Client(Settings.Enabled());

        services.PostConfigure<HttpResilienceOptions>("t",
            options => options.Timeout.Attempt = options.Timeout.Total + TimeSpan.FromSeconds(1));

        using ServiceProvider provider = services.BuildServiceProvider();

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptionsMonitor<HttpResilienceOptions>>().Get("t"));

        Assert.Contains("Timeout:Attempt", string.Join(" ", exception.Failures), StringComparison.Ordinal);
    }

    /// <summary>
    /// Options that decide which handlers exist are read when the handler chain is built, so a late
    /// <c>PostConfigure</c> of one takes effect at the origin like any other value.
    /// </summary>
    /// <remarks>
    /// Fails if handler composition goes back to being decided at registration: the late rate limiter never
    /// rejects, and the late disable still retries.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task PostConfiguringTheRateLimiterOn_TakesEffect()
    {
        (ServiceCollection services, RecordingHandler origin) = Client(Settings.Enabled());
        services.PostConfigure<HttpResilienceOptions>("t", options =>
        {
            options.RateLimiter.Enabled = true;
            options.RateLimiter.PermitLimit = 1;
            options.RateLimiter.Window = TimeSpan.FromHours(1);
        });

        await using ServiceProvider provider = services.BuildServiceProvider();
        HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("t");

        (await client.GetAsync("http://origin.test/x", TestContext.Current.CancellationToken)).Dispose();
        await Assert.ThrowsAsync<Polly.RateLimiting.RateLimiterRejectedException>(
            () => client.GetAsync("http://origin.test/x", TestContext.Current.CancellationToken));

        // One permit covered the first request and its retries.
        Assert.Equal(3, origin.Count);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task PostConfiguringTheConcurrencyLimiterOn_TakesEffect()
    {
        var gate = new TaskCompletionSource();
        var origin = new RecordingHandler(async (request, _, cancellationToken) =>
        {
            await gate.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { RequestMessage = request };
        });

        var services = new ServiceCollection();
        services.AddHttpResilience(Configuration(Settings.Enabled()));
        services.AddHttpClient("t").AddHttpResilienceHandler().ConfigurePrimaryHttpMessageHandler(() => origin);
        services.PostConfigure<HttpResilienceOptions>("t", options =>
        {
            options.ConcurrencyLimiter.Enabled = true;
            options.ConcurrencyLimiter.Limit = 1;
        });

        await using ServiceProvider provider = services.BuildServiceProvider();
        HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("t");

        Task<HttpResponseMessage> held = client.GetAsync("http://origin.test/x", TestContext.Current.CancellationToken);
        await origin.WaitForCountAsync(1);

        await Assert.ThrowsAsync<Polly.RateLimiting.RateLimiterRejectedException>(
            () => client.GetAsync("http://origin.test/x", TestContext.Current.CancellationToken));

        gate.SetResult();
        (await held).Dispose();
        Assert.Equal(1, origin.Count);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task PostConfiguringEnabledOff_RemovesThePipeline()
    {
        (ServiceCollection services, RecordingHandler origin) = Client(Settings.Enabled());
        services.PostConfigure<HttpResilienceOptions>("t", options => options.Enabled = false);

        await using ServiceProvider provider = services.BuildServiceProvider();
        HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("t");

        (await client.GetAsync("http://origin.test/x", TestContext.Current.CancellationToken)).Dispose();

        // No retries, and HttpClient.Timeout is the framework default rather than the pipeline's.
        Assert.Equal(1, origin.Count);
        Assert.Equal(TimeSpan.FromSeconds(100), client.Timeout);
    }

    /// <summary>
    /// The <c>configure</c> parameter still reaches everything, and remains the clearest way to say it.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ConfigureParameter_StillReachesThePipelineAndTheOptions()
    {
        var services = new ServiceCollection();
        services.AddHttpResilience(Configuration(Settings.Enabled().Set("Retry:MaxRetries", "2")));

        var origin = new RecordingHandler();
        services.AddHttpClient("t")
            .AddHttpResilienceHandler(configure: options => options.Retry.MaxRetries = 1)
            .ConfigurePrimaryHttpMessageHandler(() => origin);

        await using ServiceProvider provider = services.BuildServiceProvider();
        (await provider.GetRequiredService<IHttpClientFactory>().CreateClient("t")
            .GetAsync("http://origin.test/x", TestContext.Current.CancellationToken)).Dispose();

        Assert.Equal(1, provider.GetRequiredService<IOptionsMonitor<HttpResilienceOptions>>()
            .Get("t").Retry.MaxRetries);
        Assert.Equal(2, origin.Count);
    }
}
