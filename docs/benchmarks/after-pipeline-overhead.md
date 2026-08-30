```

BenchmarkDotNet v0.15.8, macOS Tahoe 26.5.2 (25F84) [Darwin 25.5.0]
Apple M4, 1 CPU, 10 logical and 10 physical cores
.NET SDK 10.0.100
  [Host]    : .NET 10.0.0 (10.0.0, 10.0.25.52411), Arm64 RyuJIT armv8.0-a
  MediumRun : .NET 10.0.0 (10.0.0, 10.0.25.52411), Arm64 RyuJIT armv8.0-a

Job=MediumRun  IterationCount=15  LaunchCount=2  
WarmupCount=10  

```
| Method                             | Authorities | Mean       | Error    | StdDev   | Median     | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|----------------------------------- |------------ |-----------:|---------:|---------:|-----------:|------:|--------:|-------:|----------:|------------:|
| **&#39;IHttpClientFactory only&#39;**          | **1**           |   **250.1 ns** |  **0.57 ns** |  **0.83 ns** |   **250.1 ns** |  **1.00** |    **0.00** | **0.0944** |     **792 B** |        **1.00** |
| &#39;Microsoft standard handler&#39;       | 1           |   807.7 ns |  6.38 ns |  9.35 ns |   809.0 ns |  3.23 |    0.04 | 0.1230 |    1032 B |        1.30 |
| &#39;HttpResilience standard&#39;          | 1           |   818.4 ns |  5.82 ns |  8.35 ns |   818.5 ns |  3.27 |    0.03 | 0.1593 |    1336 B |        1.69 |
| &#39;+ rate limiter&#39;                   | 1           | 1,088.9 ns |  3.89 ns |  5.58 ns | 1,088.3 ns |  4.35 |    0.03 | 0.1698 |    1432 B |        1.81 |
| &#39;+ concurrency limiter&#39;            | 1           | 1,096.3 ns |  6.78 ns |  9.51 ns | 1,099.0 ns |  4.38 |    0.04 | 0.1755 |    1472 B |        1.86 |
| &#39;+ rate limiter + concurrency cap&#39; | 1           | 1,084.8 ns |  4.66 ns |  6.68 ns | 1,083.1 ns |  4.34 |    0.03 | 0.1698 |    1432 B |        1.81 |
| &#39;+ telemetry enrichment&#39;           | 1           |   814.6 ns |  1.67 ns |  2.34 ns |   814.2 ns |  3.26 |    0.01 | 0.1593 |    1336 B |        1.69 |
| &#39;+ per-authority pipelines&#39;        | 1           |   898.7 ns | 12.84 ns | 18.82 ns |   889.4 ns |  3.59 |    0.07 | 0.1593 |    1336 B |        1.69 |
|                                    |             |            |          |          |            |       |         |        |           |             |
| **&#39;IHttpClientFactory only&#39;**          | **100**         |   **255.8 ns** |  **1.53 ns** |  **2.14 ns** |   **255.5 ns** |  **1.00** |    **0.01** | **0.0944** |     **792 B** |        **1.00** |
| &#39;Microsoft standard handler&#39;       | 100         |   806.7 ns |  5.39 ns |  7.74 ns |   807.1 ns |  3.15 |    0.04 | 0.1230 |    1032 B |        1.30 |
| &#39;HttpResilience standard&#39;          | 100         |   862.6 ns | 12.07 ns | 17.70 ns |   852.0 ns |  3.37 |    0.07 | 0.1593 |    1336 B |        1.69 |
| &#39;+ rate limiter&#39;                   | 100         | 1,096.6 ns |  5.79 ns |  8.31 ns | 1,100.6 ns |  4.29 |    0.05 | 0.1698 |    1432 B |        1.81 |
| &#39;+ concurrency limiter&#39;            | 100         | 1,093.8 ns |  1.73 ns |  2.30 ns | 1,093.7 ns |  4.28 |    0.04 | 0.1755 |    1472 B |        1.86 |
| &#39;+ rate limiter + concurrency cap&#39; | 100         | 1,088.8 ns |  3.13 ns |  4.58 ns | 1,087.5 ns |  4.26 |    0.04 | 0.1698 |    1432 B |        1.81 |
| &#39;+ telemetry enrichment&#39;           | 100         |   822.5 ns |  5.97 ns |  8.17 ns |   823.8 ns |  3.22 |    0.04 | 0.1593 |    1336 B |        1.69 |
| &#39;+ per-authority pipelines&#39;        | 100         |   995.2 ns |  8.79 ns | 12.60 ns |   995.8 ns |  3.89 |    0.06 | 0.1583 |    1336 B |        1.69 |
