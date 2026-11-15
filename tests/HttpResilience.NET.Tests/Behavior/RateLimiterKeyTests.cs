using System.Diagnostics.Metrics;
using System.Net.Http.Headers;
using System.Net;
using System.Threading.RateLimiting;
using BclConcurrencyLimiterOptions = System.Threading.RateLimiting.ConcurrencyLimiterOptions;
using HttpResilience.NET.Internal;
using HttpResilience.NET.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.RateLimiting;
using Polly.Timeout;

namespace HttpResilience.NET.Tests.Behavior;

/// <summary>
/// A consumer's keyed RateLimiter cannot replace the one the pipeline enforces.
/// </summary>
public class RateLimiterKeyTests
{
    private static IConfigurationRoot Configuration(Settings settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings.Build()).Build();

    /// <summary>
    /// Keyed service keys share one namespace per service type, and <see cref="RateLimiter"/> is a BCL type
    /// an application is likely to register under a domain name of its own.
    /// </summary>
    /// <remarks>
    /// Fails if the limiter is keyed on the bare client name: a consumer's keyed <see cref="RateLimiter"/>
    /// would silently replace the configured one. Asserts how many requests the pipeline admits at once.
    /// </remarks>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task AConsumersKeyedRateLimiter_DoesNotReplaceTheOneThePipelineEnforces()
    {
        var gate = new TaskCompletionSource();
        var origin = new RecordingHandler(async (request, _, cancellationToken) =>
        {
            await gate.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };
        });

        var services = new ServiceCollection();
        services.AddHttpResilience(Configuration(Settings.Enabled()
            .Set("Retry:Enabled", "false")
            .Set("RateLimiter:Enabled", "true")
            .Set("RateLimiter:PermitLimit", "5")
            .Set("RateLimiter:Window", "00:01:00")));

        services.AddHttpClient("Search")
            .AddHttpResilienceHandler()
            .ConfigurePrimaryHttpMessageHandler(() => origin);

        // The collision. An inbound rate-limit policy or a domain limiter keyed by the same name is an
        // ordinary shape, and AddKeyedSingleton is not TryAdd, so the last registration wins.
        services.AddKeyedSingleton<RateLimiter>("Search", (_, _) =>
            new ConcurrencyLimiter(new BclConcurrencyLimiterOptions { PermitLimit = 1, QueueLimit = 0 }));

        await using ServiceProvider provider = services.BuildServiceProvider();
        using HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("Search");

        Task<HttpResponseMessage>[] inFlight =
            [.. Enumerable.Range(0, 3).Select(_ => client.GetAsync("http://origin.test/x"))];

        while (origin.Count < 3 && !inFlight.Any(t => t.IsFaulted))
        {
            await Task.Yield();
        }

        gate.SetResult();
        foreach (HttpResponseMessage response in await Task.WhenAll(inFlight))
        {
            response.Dispose();
        }

        // Three requests against a budget of five. Under the consumer's limiter of one, two would have been
        // rejected with RateLimiterRejectedException before ever reaching the origin.
        Assert.Equal(3, origin.Count);
        Assert.Equal(3, origin.MaxConcurrent);

        // The other half, and the one that matters as much: the *configured* limiter is genuinely in force.
        // The assertion above alone would also pass against a pipeline carrying no rate limiter at all --
        // found by running the reproduction probe again after the fix rather than by re-reading the test.
        // Two more requests exhaust the window of five; the third is refused.
        for (int i = 0; i < 2; i++)
        {
            (await client.GetAsync("http://origin.test/x", TestContext.Current.CancellationToken)).Dispose();
        }

        await Assert.ThrowsAsync<RateLimiterRejectedException>(
            () => client.GetAsync("http://origin.test/x", TestContext.Current.CancellationToken));
        Assert.Equal(5, origin.Count);

        // And the two registrations coexist: the consumer still gets its own.
        Assert.IsType<ConcurrencyLimiter>(provider.GetRequiredKeyedService<RateLimiter>("Search"));
        Assert.IsType<FixedWindowRateLimiter>(
            provider.GetRequiredKeyedService<RateLimiter>(new RateLimiterKey("Search")));
    }
}
