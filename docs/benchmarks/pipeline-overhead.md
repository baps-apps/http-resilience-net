```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M4, 1 CPU, 10 logical and 10 physical cores
.NET SDK 10.0.402
  [Host]    : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a
  MediumRun : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a

Job=MediumRun  IterationCount=15  LaunchCount=2  
WarmupCount=10  

```
| Method                             | Authorities | Mean       | Error     | StdDev    | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|----------------------------------- |------------ |-----------:|----------:|----------:|------:|--------:|-------:|----------:|------------:|
| **&#39;IHttpClientFactory only&#39;**          | **1**           |   **558.0 ns** |  **42.48 ns** |  **62.27 ns** |  **1.01** |    **0.15** | **0.0944** |     **792 B** |        **1.00** |
| &#39;Microsoft standard handler&#39;       | 1           | 1,674.2 ns |  42.03 ns |  61.60 ns |  3.03 |    0.30 | 0.1221 |    1032 B |        1.30 |
| &#39;HttpResilience standard&#39;          | 1           | 1,838.8 ns | 104.64 ns | 153.38 ns |  3.33 |    0.41 | 0.1564 |    1336 B |        1.69 |
| &#39;+ rate limiter&#39;                   | 1           | 2,623.4 ns | 404.86 ns | 567.56 ns |  4.75 |    1.11 | 0.1678 |    1432 B |        1.81 |
| &#39;+ concurrency limiter&#39;            | 1           | 2,599.8 ns | 308.55 ns | 442.51 ns |  4.71 |    0.90 | 0.1755 |    1472 B |        1.86 |
| &#39;+ rate limiter + concurrency cap&#39; | 1           | 2,265.7 ns |  61.59 ns |  86.35 ns |  4.10 |    0.41 | 0.1678 |    1432 B |        1.81 |
| &#39;+ telemetry enrichment&#39;           | 1           | 1,912.9 ns |  97.48 ns | 130.13 ns |  3.46 |    0.40 | 0.1583 |    1336 B |        1.69 |
| &#39;+ per-authority pipelines&#39;        | 1           | 2,118.2 ns | 164.79 ns | 231.02 ns |  3.83 |    0.55 | 0.1564 |    1336 B |        1.69 |
|                                    |             |            |           |           |       |         |        |           |             |
| **&#39;IHttpClientFactory only&#39;**          | **100**         |   **669.9 ns** | **176.39 ns** | **264.00 ns** |  **1.11** |    **0.54** | **0.0944** |     **792 B** |        **1.00** |
| &#39;Microsoft standard handler&#39;       | 100         | 1,903.3 ns | 161.51 ns | 231.63 ns |  3.16 |    0.94 | 0.1221 |    1032 B |        1.30 |
| &#39;HttpResilience standard&#39;          | 100         | 1,895.1 ns | 140.15 ns | 196.47 ns |  3.15 |    0.92 | 0.1583 |    1336 B |        1.69 |
| &#39;+ rate limiter&#39;                   | 100         | 2,335.0 ns |  97.14 ns | 142.39 ns |  3.88 |    1.08 | 0.1678 |    1432 B |        1.81 |
| &#39;+ concurrency limiter&#39;            | 100         | 2,752.4 ns | 324.87 ns | 465.91 ns |  4.57 |    1.47 | 0.1755 |    1472 B |        1.86 |
| &#39;+ rate limiter + concurrency cap&#39; | 100         | 2,509.0 ns | 151.96 ns | 208.01 ns |  4.17 |    1.18 | 0.1678 |    1432 B |        1.81 |
| &#39;+ telemetry enrichment&#39;           | 100         | 1,903.2 ns |  98.68 ns | 141.53 ns |  3.16 |    0.89 | 0.1583 |    1336 B |        1.69 |
| &#39;+ per-authority pipelines&#39;        | 100         | 2,156.5 ns | 116.80 ns | 171.20 ns |  3.58 |    1.01 | 0.1564 |    1336 B |        1.69 |
