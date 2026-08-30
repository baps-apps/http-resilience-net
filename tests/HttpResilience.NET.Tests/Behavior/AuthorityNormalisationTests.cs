using System.Net;
using HttpResilience.NET.Tests.Infrastructure;

namespace HttpResilience.NET.Tests.Behavior;

/// <summary>
/// The allow-list has to match the host an operator wrote, not the byte sequence a caller happened to build
/// the <see cref="Uri"/> from.
/// </summary>
/// <remarks>
/// <see cref="Uri.Host"/> is not a normal form: it returns the Unicode label for an internationalised host
/// written in Unicode and the punycode label for the same host written in punycode, and it preserves the
/// trailing dot of a fully-qualified name. Matching on it means <c>https://münchen.example</c> in
/// configuration does not match a request built from <c>https://xn--mnchen-3ya.example</c>, and
/// <c>http://orders.internal</c> does not match <c>http://orders.internal.</c>.
/// <para>
/// Both fail <b>closed</b> -- a hedged client throws and a standard client falls back to the shared pipeline
/// -- so this is an availability edge rather than a bypass. It is still an allow-list rejecting a host that
/// is on it. <see cref="Uri.IdnHost"/> is the stable form; the trailing dot has to be trimmed explicitly.
/// </para>
/// </remarks>
public class AuthorityNormalisationTests
{
    private static Settings HedgedTo(string authority) => Settings.Enabled()
        .Set("Retry:Enabled", "false")
        .Set("Hedging:MaxHedgedAttempts", "1")
        .Set("PipelineSelection:Authorities:0", authority);

    /// <summary>
    /// The same host, written the two ways a URI can express it, is one authority.
    /// </summary>
    /// <remarks>
    /// Fails if authority matching goes back to <see cref="Uri.Host"/>: the request is rejected by the
    /// allow-list its own host is on.
    /// </remarks>
    [Theory]
    [InlineData("https://xn--mnchen-3ya.example", "https://münchen.example/x")]
    [InlineData("https://münchen.example", "https://xn--mnchen-3ya.example/x")]
    [InlineData("https://münchen.example", "https://münchen.example/x")]
    public async Task AnInternationalisedHost_MatchesInEitherForm(string configured, string requested)
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            HedgedTo(configured), new RecordingHandler(HttpStatusCode.OK), hedged: true);

        HttpResponseMessage response = await harness.GetAsync(requested);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, harness.Origin.Count);
    }

    /// <summary>
    /// A fully-qualified name with its root label spelled out is the same authority as one without.
    /// </summary>
    [Theory]
    [InlineData("http://orders.internal", "http://orders.internal./x")]
    [InlineData("http://orders.internal.", "http://orders.internal/x")]
    public async Task AFullyQualifiedHost_MatchesWithOrWithoutItsTrailingDot(string configured, string requested)
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            HedgedTo(configured), new RecordingHandler(HttpStatusCode.OK), hedged: true);

        HttpResponseMessage response = await harness.GetAsync(requested);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, harness.Origin.Count);
    }

    /// <summary>
    /// Normalisation must not become a prefix match: a different host, a different scheme and a different
    /// port are each still outside the list.
    /// </summary>
    [Theory]
    [InlineData("http://other.internal/x")]
    [InlineData("https://orders.internal/x")]
    [InlineData("http://orders.internal:8443/x")]
    public async Task ADifferentAuthority_IsStillRejected(string requested)
    {
        await using ResilienceHarness harness = ResilienceHarness.Create(
            HedgedTo("http://orders.internal"), new RecordingHandler(HttpStatusCode.OK), hedged: true);

        await Assert.ThrowsAsync<HttpRequestException>(() => harness.GetAsync(requested));
        Assert.Equal(0, harness.Origin.Count);
    }
}
