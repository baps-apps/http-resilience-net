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
/// Which configuration section a client reads.
/// </summary>
public class SectionSelectionTests
{
    /// <summary>
    /// A client reads the section named after it, without repeating the name.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task SectionName_DefaultsToTheClientName()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled()
                .Set("Retry:MaxRetries", "2")
                .ForClient("orders", "Retry:MaxRetries", "4")
                .ForClient("orders", "Timeout:Total", "00:01:00"),
            clientName: "orders");

        await harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(5, harness.Origin.Count);
    }

    /// <summary>
    /// A client with no section of its own reads the root values: no special section name is needed for that.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task AClientWithNoSectionOfItsOwn_ReadsTheRootValues()
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            Settings.Enabled().Set("Retry:MaxRetries", "2"),
            clientName: "orders");

        await harness.GetAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, harness.Origin.Count);
    }

    /// <summary>
    /// An empty or blank section name is refused rather than given a meaning. It used to mean "root values
    /// only", which is what a client without a section of its own already gets.
    /// </summary>
    /// <remarks>Fails if a blank name is accepted and silently selects the root.</remarks>
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyOrBlankSectionName_IsRejected(string sectionName)
    {
        var services = new ServiceCollection();
        Action<HttpResilienceOptions> configure = _ => { };

        Action[] calls =
        [
            () => services.AddHttpClient("a").AddHttpResilienceHandler(sectionName),
            () => services.AddHttpClient("b").AddHttpResilienceHandler(sectionName, configure),
            () => services.AddHttpClient("c").AddHttpResilienceHedgingHandler(sectionName),
            () => services.AddHttpClient("d").AddHttpResilienceHedgingHandler(sectionName, configure)
        ];

        foreach (Action call in calls)
        {
            ArgumentException exception = Assert.Throws<ArgumentException>(call);
            Assert.Equal("sectionName", exception.ParamName);
            Assert.Contains("root values", exception.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// No argument means "the default": each overload exists to state a value, and the default is the
    /// overload without that argument. So <see langword="null"/> is refused by the two-argument overloads too.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public void TheTwoArgumentOverloads_RejectNull()
    {
        var services = new ServiceCollection();
        Action<HttpResilienceOptions> configure = _ => { };

        Assert.Equal("sectionName", Assert.Throws<ArgumentNullException>(
            () => services.AddHttpClient("a").AddHttpResilienceHandler(null!, configure)).ParamName);
        Assert.Equal("configure", Assert.Throws<ArgumentNullException>(
            () => services.AddHttpClient("b").AddHttpResilienceHandler("b", null!)).ParamName);
        Assert.Equal("sectionName", Assert.Throws<ArgumentNullException>(
            () => services.AddHttpClient("c").AddHttpResilienceHedgingHandler(null!, configure)).ParamName);
        Assert.Equal("configure", Assert.Throws<ArgumentNullException>(
            () => services.AddHttpClient("d").AddHttpResilienceHedgingHandler("d", null!)).ParamName);
    }

    /// <summary>
    /// The single-argument overloads exist to state a value, so a null there is a caller bug.
    /// </summary>
    [Fact(Timeout = TestTimeouts.Default)]
    public void SingleArgumentOverloads_RejectNull()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentNullException>(
            () => services.AddHttpClient("a").AddHttpResilienceHandler((string)null!));
        Assert.Throws<ArgumentNullException>(
            () => services.AddHttpClient("b").AddHttpResilienceHandler((Action<HttpResilienceOptions>)null!));
        Assert.Throws<ArgumentNullException>(
            () => services.AddHttpClient("c").AddHttpResilienceHedgingHandler((string)null!));
        Assert.Throws<ArgumentNullException>(
            () => services.AddHttpClient("d").AddHttpResilienceHedgingHandler((Action<HttpResilienceOptions>)null!));
    }
}
