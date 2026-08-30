# Authority matching

Two runs, same machine and same job. The second was taken after authority matching moved from `Uri.Host` to
`Uri.IdnHost` with the root label removed, which is what made the allow-list match a listed host written in
punycode or with a trailing dot -- see `AuthorityNormalisationTests`.

**Normalisation costs about 1.4 ns and no allocation.** That is a real regression and it is recorded here
rather than left to be discovered, because the previous revision of this directory shipped a claim that had
quietly stopped being true. In context: the figure is a per-request cost on a client using per-authority
pipelines or a hedged allow-list, against a full pipeline measured at 855 ns, so it is under 0.2% of the work
a request already does -- and it buys an allow-list that no longer rejects hosts that are on it. Allocation is
unchanged at zero, which is the property that mattered enough to shape the design; `IdnHost` is a cached
property on the `Uri`, and the root label is removed by slicing a span rather than by trimming a string.

| Case | Before | After | Delta |
| --- | ---: | ---: | ---: |
| allow-listed authority (1) | 5.440 ns | 6.802 ns | +1.36 ns |
| unlisted authority, shared key (1) | 2.614 ns | 3.434 ns | +0.82 ns |
| right host, wrong port (1) | 5.404 ns | 6.930 ns | +1.53 ns |
| allow-listed authority (100) | 6.866 ns | 8.469 ns | +1.60 ns |
| unlisted authority, shared key (100) | 2.624 ns | 3.590 ns | +0.97 ns |
| right host, wrong port (100) | 6.347 ns | 8.616 ns | +2.27 ns |
| **Allocated, every case** | **0 B** | **0 B** | **unchanged** |

The deltas are five to ten times the reported `Error` on every row, so this is a measured change rather than
run-to-run noise.

## After -- `Uri.IdnHost`, root label removed

```

BenchmarkDotNet v0.15.8, macOS Tahoe 26.5.2 (25F84) [Darwin 25.5.0]
Apple M4, 1 CPU, 10 logical and 10 physical cores
.NET SDK 10.0.100
  [Host]    : .NET 10.0.0 (10.0.0, 10.0.25.52411), Arm64 RyuJIT armv8.0-a
  MediumRun : .NET 10.0.0 (10.0.0, 10.0.25.52411), Arm64 RyuJIT armv8.0-a

Job=MediumRun  IterationCount=15  LaunchCount=2  
WarmupCount=10  

```
| Method                            | Authorities | Mean     | Error     | StdDev    | Ratio | RatioSD | Allocated | Alloc Ratio |
|---------------------------------- |------------ |---------:|----------:|----------:|------:|--------:|----------:|------------:|
| **&#39;allow-listed authority&#39;**          | **1**           | **6.802 ns** | **0.0895 ns** | **0.1283 ns** |  **1.00** |    **0.03** |         **-** |          **NA** |
| &#39;unlisted authority (shared key)&#39; | 1           | 3.434 ns | 0.2162 ns | 0.3101 ns |  0.51 |    0.05 |         - |          NA |
| &#39;right host, wrong port&#39;          | 1           | 6.930 ns | 0.3197 ns | 0.4481 ns |  1.02 |    0.07 |         - |          NA |
|                                   |             |          |           |           |       |         |           |             |
| **&#39;allow-listed authority&#39;**          | **100**         | **8.469 ns** | **0.1786 ns** | **0.2504 ns** |  **1.00** |    **0.04** |         **-** |          **NA** |
| &#39;unlisted authority (shared key)&#39; | 100         | 3.590 ns | 0.0168 ns | 0.0247 ns |  0.42 |    0.01 |         - |          NA |
| &#39;right host, wrong port&#39;          | 100         | 8.616 ns | 0.0954 ns | 0.1369 ns |  1.02 |    0.03 |         - |          NA |

## Before -- `Uri.Host`

```

BenchmarkDotNet v0.15.8, macOS Tahoe 26.5.2 (25F84) [Darwin 25.5.0]
Apple M4, 1 CPU, 10 logical and 10 physical cores
.NET SDK 10.0.100
  [Host]    : .NET 10.0.0 (10.0.0, 10.0.25.52411), Arm64 RyuJIT armv8.0-a
  MediumRun : .NET 10.0.0 (10.0.0, 10.0.25.52411), Arm64 RyuJIT armv8.0-a

Job=MediumRun  IterationCount=15  LaunchCount=2  
WarmupCount=10  

```
| Method                            | Authorities | Mean     | Error     | StdDev    | Ratio | RatioSD | Allocated | Alloc Ratio |
|---------------------------------- |------------ |---------:|----------:|----------:|------:|--------:|----------:|------------:|
| **&#39;allow-listed authority&#39;**          | **1**           | **5.440 ns** | **0.0890 ns** | **0.1248 ns** |  **1.00** |    **0.03** |         **-** |          **NA** |
| &#39;unlisted authority (shared key)&#39; | 1           | 2.614 ns | 0.0679 ns | 0.0929 ns |  0.48 |    0.02 |         - |          NA |
| &#39;right host, wrong port&#39;          | 1           | 5.404 ns | 0.2438 ns | 0.3574 ns |  0.99 |    0.07 |         - |          NA |
|                                   |             |          |           |           |       |         |           |             |
| **&#39;allow-listed authority&#39;**          | **100**         | **6.866 ns** | **0.1250 ns** | **0.1711 ns** |  **1.00** |    **0.03** |         **-** |          **NA** |
| &#39;unlisted authority (shared key)&#39; | 100         | 2.624 ns | 0.0529 ns | 0.0792 ns |  0.38 |    0.01 |         - |          NA |
| &#39;right host, wrong port&#39;          | 100         | 6.347 ns | 0.0876 ns | 0.1139 ns |  0.92 |    0.03 |         - |          NA |
