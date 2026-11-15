using System.Net;
using HttpResilience.NET.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;

namespace HttpResilience.NET.Tests.Behavior;

/// <summary>
/// The platform options this package writes are the validated pipeline: code that changes them afterwards
/// fails client creation instead of silently replacing validated values.
/// </summary>
/// <remarks>
/// The platform names a standard client's options <c>{client}-standard</c> and a hedged client's <c>{client}</c>,
/// and every registration on the client shares them, including a consumer's own
/// <c>AddStandardResilienceHandler(configure)</c>. Each case here turns a POST into a repeated request (or drops
/// the concurrency bound); without the guard the origin sees the POST several times.
/// </remarks>
public class PlatformOptionsOwnershipTests
{
    private const string StandardOptionsName = "test-standard";

    private static readonly Func<RetryPredicateArguments<HttpResponseMessage>, ValueTask<bool>> RetryAnyFailure =
        args => new ValueTask<bool>(args.Outcome.Exception is not null ||
                                    args.Outcome.Result?.IsSuccessStatusCode == false);

    public static TheoryData<string> StandardRewrites() =>
    [
        "Configure",
        "PostConfigure",
        "ConfigureAll",
        "AConsumersStandardHandler",
        "RemoveTheLimiter"
    ];

    [Theory(Timeout = TestTimeouts.Default)]
    [MemberData(nameof(StandardRewrites))]
    public async Task CodeThatRewritesTheStandardOptions_FailsClientCreation_BeforeAnyRequest(string rewrite)
    {
        var services = new ServiceCollection();
        services.AddHttpResilience(Configuration(Settings.Enabled()));
        var origin = new RecordingHandler(HttpStatusCode.ServiceUnavailable);
        IHttpClientBuilder builder = services.AddHttpClient("test").AddHttpResilienceHandler();
        builder.ConfigurePrimaryHttpMessageHandler(() => origin);

        static void RetryPost(HttpStandardResilienceOptions options)
        {
            options.Retry.MaxRetryAttempts = 6;
            options.Retry.ShouldHandle = RetryAnyFailure;
        }

        string property = "Retry.ShouldHandle";
        switch (rewrite)
        {
            case "Configure":
                services.Configure<HttpStandardResilienceOptions>(StandardOptionsName, RetryPost);
                break;
            case "PostConfigure":
                services.PostConfigure<HttpStandardResilienceOptions>(StandardOptionsName, RetryPost);
                break;
            case "ConfigureAll":
                services.ConfigureAll<HttpStandardResilienceOptions>(RetryPost);
                break;
            case "AConsumersStandardHandler":
                builder.AddStandardResilienceHandler(RetryPost);
                break;
            case "RemoveTheLimiter":
                services.PostConfigure<HttpStandardResilienceOptions>(
                    StandardOptionsName, options => options.RateLimiter.RateLimiter = null);
                property = "RateLimiter.RateLimiter";
                break;
        }

        await using ServiceProvider provider = services.BuildServiceProvider();

        OptionsValidationException exception = await SendPostAsync(provider);

        Assert.Equal(0, origin.Count);
        string message = string.Join(Environment.NewLine, exception.Failures);
        Assert.Contains(StandardOptionsName, message, StringComparison.Ordinal);
        Assert.Contains(property, message, StringComparison.Ordinal);
        Assert.Contains("HttpResilience:Clients:test", message, StringComparison.Ordinal);
        Assert.Contains("AddResilienceHandler", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The hedging guard has two halves: <c>ShouldHandle</c> after a failed attempt, and the action generator
    /// for the hedge the delay starts. Replacing either, or the attempt count, fails client creation.
    /// </summary>
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData("Hedging.ActionGenerator")]
    [InlineData("Hedging.ShouldHandle")]
    [InlineData("Hedging.MaxHedgedAttempts")]
    public async Task CodeThatRewritesTheHedgingOptions_FailsClientCreation_BeforeAnyRequest(string property)
    {
        var services = new ServiceCollection();
        services.AddHttpResilience(Configuration(Settings.Hedged()));
        var origin = new RecordingHandler(HttpStatusCode.ServiceUnavailable);
        services.AddHttpClient("test")
            .AddHttpResilienceHedgingHandler()
            .ConfigurePrimaryHttpMessageHandler(() => origin);

        services.PostConfigure<HttpStandardHedgingResilienceOptions>("test", options =>
        {
            switch (property)
            {
                case "Hedging.ActionGenerator":
                    options.Hedging.ActionGenerator = args => () => args.Callback(args.ActionContext);
                    break;
                case "Hedging.ShouldHandle":
                    options.Hedging.ShouldHandle = _ => new ValueTask<bool>(true);
                    break;
                default:
                    options.Hedging.MaxHedgedAttempts = 5;
                    break;
            }
        });

        await using ServiceProvider provider = services.BuildServiceProvider();

        OptionsValidationException exception = await SendPostAsync(provider);

        Assert.Equal(0, origin.Count);
        string message = string.Join(Environment.NewLine, exception.Failures);
        Assert.Contains(property, message, StringComparison.Ordinal);
        Assert.Contains("HttpResilience:Clients:test", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A consumer's own <c>AddStandardHedgingHandler()</c>, with no configure at all, re-runs the platform's
    /// post-configure on the shared options and replaces the action generator that stops the delay-started
    /// hedge of a POST. Without the guard one POST on a slow origin delivers three bodies.
    /// </summary>
    /// <remarks>
    /// Driven against the mechanism (rule 8): a primary slower than the hedging delay. Fails if the guard stops
    /// judging <c>Hedging.ActionGenerator</c>, because the request is then sent and hedged.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task AConsumersOwnHedgingHandler_FailsClientCreation_BecauseItWouldHedgeAPost()
    {
        var services = new ServiceCollection();
        services.AddHttpResilience(Configuration(Settings.Hedged().Set("Hedging:Delay", "00:00:00.050")));
        var origin = new RecordingHandler(async (_, attempt, cancellationToken) =>
        {
            if (attempt == 1)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        IHttpClientBuilder builder = services.AddHttpClient("test")
            .AddHttpResilienceHedgingHandler()
            .ConfigurePrimaryHttpMessageHandler(() => origin);
        builder.AddStandardHedgingHandler();

        await using ServiceProvider provider = services.BuildServiceProvider();

        OptionsValidationException exception = await SendPostAsync(provider);

        Assert.Empty(origin.Bodies);
        string message = string.Join(Environment.NewLine, exception.Failures);
        Assert.Contains("Hedging.ActionGenerator", message, StringComparison.Ordinal);
        Assert.Contains("AddStandardHedgingHandler", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The failure stops the deployment: the host's startup validation and client probe build the options before
    /// any request is served.
    /// </summary>
    /// <remarks>Fails if the guard only runs at first client creation.</remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ARewrite_FailsAtHostStart()
    {
        var services = new ServiceCollection();
        services.AddHttpResilience(Configuration(Settings.Enabled()));
        services.AddHttpClient("test").AddHttpResilienceHandler()
            .ConfigurePrimaryHttpMessageHandler(() => new RecordingHandler(HttpStatusCode.OK));
        services.Configure<HttpStandardResilienceOptions>(
            StandardOptionsName, options => options.Retry.MaxRetryAttempts = 6);

        await using ServiceProvider provider = services.BuildServiceProvider();

        Exception? exception = await Record.ExceptionAsync(async () =>
        {
            foreach (IStartupValidator validator in provider.GetServices<IStartupValidator>())
            {
                validator.Validate();
            }

            foreach (IHostedService service in provider.GetServices<IHostedService>())
            {
                await service.StartAsync(TestContext.Current.CancellationToken);
            }
        });

        OptionsValidationException failure = Assert.IsType<OptionsValidationException>(exception);
        Assert.Contains("Retry.MaxRetryAttempts (2, now 6)", string.Join(Environment.NewLine, failure.Failures),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Configuring platform options of a client this package does not own is the consumer's business: the guard
    /// judges only the options it wrote.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task AnotherClientsPlatformOptions_AreLeftAlone()
    {
        var services = new ServiceCollection();
        services.AddHttpResilience(Configuration(Settings.Enabled()));
        services.AddHttpClient("test").AddHttpResilienceHandler()
            .ConfigurePrimaryHttpMessageHandler(() => new RecordingHandler(HttpStatusCode.OK));

        var origin = new RecordingHandler(HttpStatusCode.ServiceUnavailable);
        services.AddHttpClient("other")
            .AddStandardResilienceHandler(options =>
            {
                options.Retry.MaxRetryAttempts = 1;
                options.Retry.Delay = TimeSpan.Zero;
                options.Retry.ShouldHandle = RetryAnyFailure;
            });
        services.AddHttpClient("other").ConfigurePrimaryHttpMessageHandler(() => origin);

        await using ServiceProvider provider = services.BuildServiceProvider();
        HttpClient other = provider.GetRequiredService<IHttpClientFactory>().CreateClient("other");
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://origin.test/x");

        (await other.SendAsync(request, TestContext.Current.CancellationToken)).Dispose();

        Assert.Equal(2, origin.Count);
    }

    /// <summary>
    /// A disabled client's handler is removed and its platform options are never written, so there is
    /// nothing to guard.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ADisabledClient_IsNotJudged()
    {
        var services = new ServiceCollection();
        services.AddHttpResilience(Configuration(Settings.Enabled().ForClient("test", "Enabled", "false")));
        var origin = new RecordingHandler(HttpStatusCode.OK);
        services.AddHttpClient("test").AddHttpResilienceHandler().ConfigurePrimaryHttpMessageHandler(() => origin);
        services.PostConfigure<HttpStandardResilienceOptions>(
            StandardOptionsName, options => options.Retry.MaxRetryAttempts = 6);

        await using ServiceProvider provider = services.BuildServiceProvider();
        HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("test");

        (await client.GetAsync("http://origin.test/x", TestContext.Current.CancellationToken)).Dispose();

        Assert.Equal(1, origin.Count);
    }

    private static IConfigurationSection Configuration(Settings settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings.Build()).Build().GetSection("HttpResilience");

    /// <summary>Creates the client and sends one POST, returning the validation failure either step raised.</summary>
    private static async Task<OptionsValidationException> SendPostAsync(ServiceProvider provider)
    {
        Exception? exception = await Record.ExceptionAsync(async () =>
        {
            HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("test");
            using var request = new HttpRequestMessage(HttpMethod.Post, "http://origin.test/x")
            {
                Content = new StringContent("order")
            };
            (await client.SendAsync(request, TestContext.Current.CancellationToken)).Dispose();
        });

        return Assert.IsType<OptionsValidationException>(exception);
    }
}
