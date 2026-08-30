using HttpResilience.NET.Internal;
using HttpResilience.NET.Options;

namespace HttpResilience.NET.Tests.Internal;

public class PipelineKeySelectorTests
{
    private static Func<HttpRequestMessage, string> Selector(params string[] authorities) =>
        PipelineKeySelector.Create(new PipelineSelectionOptions
        {
            Mode = PipelineSelectionMode.ByAuthority,
            Authorities = [.. authorities]
        });

    [Fact]
    public void AllowListedAuthority_GetsItsOwnKey()
    {
        Func<HttpRequestMessage, string> selector = Selector("https://orders.internal");

        Assert.Equal(
            "https://orders.internal",
            selector(new HttpRequestMessage(HttpMethod.Get, "https://orders.internal/v1/orders")));
    }

    [Fact]
    public void NonDefaultPort_IsPartOfTheKey()
    {
        Func<HttpRequestMessage, string> selector = Selector("https://billing.internal:8443");

        Assert.Equal(
            "https://billing.internal:8443",
            selector(new HttpRequestMessage(HttpMethod.Get, "https://billing.internal:8443/x")));
    }

    /// <summary>
    /// The whole point of the allow-list: no amount of request traffic to novel hosts can create additional
    /// pipeline keys, so pipelines, circuit breakers and metric series stay bounded by configuration.
    /// </summary>
    [Theory]
    [InlineData("https://attacker.example/x")]
    [InlineData("https://orders.internal.attacker.example/x")]
    [InlineData("http://orders.internal/x")]
    [InlineData("https://orders.internal:9999/x")]
    public void AnythingElse_FallsBackToTheSharedKey(string url)
    {
        Func<HttpRequestMessage, string> selector = Selector("https://orders.internal");

        Assert.Equal(PipelineKeySelector.SharedKey, selector(new HttpRequestMessage(HttpMethod.Get, url)));
    }

    [Fact]
    public void KeysAreBounded_ByTheAllowListSize()
    {
        Func<HttpRequestMessage, string> selector = Selector("https://a.test", "https://b.test");

        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < 10_000; i++)
        {
            keys.Add(selector(new HttpRequestMessage(HttpMethod.Get, $"https://host-{i}.test/x")));
        }

        Assert.Equal([PipelineKeySelector.SharedKey], keys);
    }

    [Fact]
    public void MatchingIsCaseInsensitiveOnHost()
    {
        Func<HttpRequestMessage, string> selector = Selector("https://Orders.Internal");

        Assert.Equal(
            "https://orders.internal",
            selector(new HttpRequestMessage(HttpMethod.Get, "https://ORDERS.INTERNAL/x")));
    }

    [Theory]
    [InlineData("https://orders.internal", true)]
    [InlineData("https://orders.internal:8443", true)]
    [InlineData("not a url", false)]
    [InlineData("orders.internal", false)]
    [InlineData("", false)]
    public void TryNormalizeAuthority_RejectsWhatCouldNeverMatch(string value, bool expected)
    {
        Assert.Equal(expected, PipelineKeySelector.TryNormalizeAuthority(value, out _));
    }

    [Fact]
    public void TrackingKey_IsConstant_WhenSelectionIsOff()
    {
        Func<HttpRequestMessage?, string> key = PipelineKeySelector.CreateForTracking(new HttpResilienceOptions(), PipelineKind.Standard);

        Assert.Equal(PipelineKeySelector.SharedKey, key(new HttpRequestMessage(HttpMethod.Get, "https://a.test/x")));
        Assert.Equal(PipelineKeySelector.SharedKey, key(null));
    }
}
