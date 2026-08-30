using HttpResilience.NET.Internal;
using HttpResilience.NET.Options;

namespace HttpResilience.NET.Tests.Internal;

/// <summary>
/// The selector runs on every request of a client using per-authority pipelines. Building a string only to
/// probe a set with it puts an allocation on the hot path of a package whose whole claim is that it costs
/// nothing over the handler it configures.
/// </summary>
public class PipelineKeySelectorAllocationTests
{
    private static Func<HttpRequestMessage, string> Selector(params string[] authorities) =>
        PipelineKeySelector.Create(new PipelineSelectionOptions
        {
            Mode = PipelineSelectionMode.ByAuthority,
            Authorities = [.. authorities]
        });

    [Theory]
    [InlineData("https://orders.internal/x")]          // allow-listed
    [InlineData("https://unlisted.internal/x")]        // shared key
    [InlineData("https://orders.internal:8443/x")]     // right host, wrong port
    public void SelectingAPipeline_AllocatesNothing(string url)
    {
        Func<HttpRequestMessage, string> selector = Selector("https://orders.internal", "https://billing.internal:8443");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        _ = selector(request);   // warm up any lazy state before measuring

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1_000; i++)
        {
            _ = selector(request);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>
    /// The same measurement against a <b>fresh</b> <see cref="Uri"/> each time.
    /// </summary>
    /// <remarks>
    /// The test above reuses one request, which hides anything the selector allocates lazily <i>per URI</i>:
    /// a <see cref="Uri"/> caches what its properties compute, so a first access that allocates is paid once
    /// and then never seen again. Authority matching reads <see cref="Uri.IdnHost"/>, which is exactly that
    /// shape of property, so the reused-request measurement cannot speak for it.
    /// <para>
    /// <see cref="Uri.Host"/> is read first to force the URI's own lazy parse, which every caller has already
    /// paid for by the time a request reaches the pipeline. What is left to measure is the selector's own
    /// marginal cost, which must still be nothing.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("https://orders.internal/x")]
    [InlineData("https://unlisted.internal/x")]
    [InlineData("https://orders.internal./x")]
    public void SelectingAPipeline_AllocatesNothing_ForAFreshUriEachTime(string url)
    {
        Func<HttpRequestMessage, string> selector = Selector("https://orders.internal", "https://billing.internal:8443");

        const int Iterations = 500;
        var requests = new HttpRequestMessage[Iterations];
        for (int i = 0; i < Iterations; i++)
        {
            requests[i] = new HttpRequestMessage(HttpMethod.Get, url);

            // The URI's own lazy parse belongs to whoever built the request, not to the selector.
            _ = requests[i].RequestUri!.Host;
        }

        _ = selector(requests[0]);   // warm up any lazy state in the selector itself

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Iterations; i++)
        {
            _ = selector(requests[i]);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        foreach (HttpRequestMessage request in requests)
        {
            request.Dispose();
        }

        Assert.Equal(0, allocated);
    }

    /// <summary>
    /// An index over no authorities is a real state -- a client whose allow-list is absent -- and the
    /// span-based lookup the index builds must tolerate it rather than throwing at registration.
    /// </summary>
    [Fact]
    public void AnEmptyAllowList_MatchesNothing_AndDoesNotThrow()
    {
        Func<HttpRequestMessage, string> selector = Selector();

        Assert.Equal(PipelineKeySelector.SharedKey,
            selector(new HttpRequestMessage(HttpMethod.Get, "https://orders.internal/x")));
    }

    /// <summary>The allocation-free path must return the same keys the string-building one did.</summary>
    [Fact]
    public void ReturnsTheAllowListedKey_Verbatim()
    {
        Func<HttpRequestMessage, string> selector = Selector("https://orders.internal", "https://billing.internal:8443");

        Assert.Equal("https://orders.internal",
            selector(new HttpRequestMessage(HttpMethod.Get, "https://ORDERS.internal/a/b?c=d")));
        Assert.Equal("https://billing.internal:8443",
            selector(new HttpRequestMessage(HttpMethod.Get, "https://billing.internal:8443/a")));
        Assert.Equal(PipelineKeySelector.SharedKey,
            selector(new HttpRequestMessage(HttpMethod.Get, "https://billing.internal/a")));
        Assert.Equal(PipelineKeySelector.SharedKey,
            selector(new HttpRequestMessage(HttpMethod.Get, "http://orders.internal/a")));
    }
}
