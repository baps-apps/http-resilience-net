using BenchmarkDotNet.Attributes;
using HttpResilience.NET.Internal;

namespace HttpResilience.NET.Benchmarks;

/// <summary>
/// The per-request cost of choosing a pipeline for a client using per-authority isolation.
/// </summary>
/// <remarks>
/// This runs on every request such a client makes. The obvious implementation builds
/// <c>scheme://host:port</c> from the request URI and probes a set with it, which allocates a string per
/// request purely to perform a lookup. The number to watch here is <c>Allocated</c>, and it should be zero.
/// </remarks>
[MemoryDiagnoser]
// Only the job column is hidden. Error, StdDev and RatioSD stay so a ratio is never read without its
// dispersion.
[HideColumns("Job")]
public class AuthorityMatchingBenchmarks
{
    private Func<HttpRequestMessage, string> _selector = null!;
    private HttpRequestMessage _allowListed = null!;
    private HttpRequestMessage _unlisted = null!;
    private HttpRequestMessage _wrongPort = null!;

    /// <summary>How many authorities are allow-listed, which is what bounds the pipeline count.</summary>
    [Params(1, 100)]
    public int Authorities { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var selection = new HttpResiliencePipelineSelectionOptions { Mode = HttpResiliencePipelineSelectionMode.ByAuthority };
        foreach (int i in Enumerable.Range(0, Authorities))
        {
            selection.Authorities.Add($"https://host-{i}.bench");
        }

        _selector = PipelineKeySelector.Create(selection);

        _allowListed = new HttpRequestMessage(HttpMethod.Get, $"https://host-{Authorities - 1}.bench/x");
        _unlisted = new HttpRequestMessage(HttpMethod.Get, "https://somewhere-else.bench/x");
        _wrongPort = new HttpRequestMessage(HttpMethod.Get, $"https://host-0.bench:8443/x");

        // Uri caches Host and Scheme on first access; warm them so the benchmark measures matching only.
        _ = _selector(_allowListed);
        _ = _selector(_unlisted);
        _ = _selector(_wrongPort);
    }

    [Benchmark(Baseline = true, Description = "allow-listed authority")]
    public string AllowListed() => _selector(_allowListed);

    [Benchmark(Description = "unlisted authority (shared key)")]
    public string Unlisted() => _selector(_unlisted);

    [Benchmark(Description = "right host, wrong port")]
    public string WrongPort() => _selector(_wrongPort);
}
