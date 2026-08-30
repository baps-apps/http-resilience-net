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
| **&#39;IHttpClientFactory only&#39;**          | **1**           |   **253.2 ns** |  **1.67 ns** |  **2.45 ns** |   **252.6 ns** |  **1.00** |    **0.01** | **0.0944** |     **792 B** |        **1.00** |
| &#39;Microsoft standard handler&#39;       | 1           |   815.5 ns |  4.74 ns |  7.09 ns |   820.0 ns |  3.22 |    0.04 | 0.1230 |    1032 B |        1.30 |
| &#39;HttpResilience standard&#39;          | 1           |   832.5 ns | 21.32 ns | 31.91 ns |   830.7 ns |  3.29 |    0.13 | 0.1593 |    1336 B |        1.69 |
| &#39;+ rate limiter&#39;                   | 1           | 1,090.9 ns |  2.62 ns |  3.91 ns | 1,090.9 ns |  4.31 |    0.04 | 0.1698 |    1432 B |        1.81 |
| &#39;+ concurrency limiter&#39;            | 1           | 1,093.8 ns |  7.24 ns |  9.91 ns | 1,095.1 ns |  4.32 |    0.06 | 0.1755 |    1472 B |        1.86 |
| &#39;+ rate limiter + concurrency cap&#39; | 1           | 1,087.1 ns |  5.37 ns |  7.34 ns | 1,086.0 ns |  4.29 |    0.05 | 0.1698 |    1432 B |        1.81 |
| &#39;+ telemetry enrichment&#39;           | 1           |   828.7 ns | 23.15 ns | 33.21 ns |   852.8 ns |  3.27 |    0.13 | 0.1593 |    1336 B |        1.69 |
| &#39;+ per-authority pipelines&#39;        | 1           |   869.9 ns | 13.96 ns | 20.02 ns |   867.7 ns |  3.44 |    0.08 | 0.1593 |    1336 B |        1.69 |
|                                    |             |            |          |          |            |       |         |        |           |             |
| **&#39;IHttpClientFactory only&#39;**          | **100**         |   **264.4 ns** |  **7.44 ns** | **10.90 ns** |   **274.1 ns** |  **1.00** |    **0.06** | **0.0944** |     **792 B** |        **1.00** |
| &#39;Microsoft standard handler&#39;       | 100         |   812.2 ns |  5.71 ns |  8.00 ns |   813.0 ns |  3.08 |    0.13 | 0.1230 |    1032 B |        1.30 |
| &#39;HttpResilience standard&#39;          | 100         |   828.9 ns | 13.34 ns | 18.70 ns |   812.6 ns |  3.14 |    0.15 | 0.1593 |    1336 B |        1.69 |
| &#39;+ rate limiter&#39;                   | 100         | 1,095.7 ns |  3.72 ns |  5.45 ns | 1,096.8 ns |  4.15 |    0.17 | 0.1698 |    1432 B |        1.81 |
| &#39;+ concurrency limiter&#39;            | 100         | 1,094.7 ns |  5.13 ns |  7.52 ns | 1,095.2 ns |  4.15 |    0.17 | 0.1755 |    1472 B |        1.86 |
| &#39;+ rate limiter + concurrency cap&#39; | 100         | 1,087.4 ns |  4.65 ns |  6.36 ns | 1,085.2 ns |  4.12 |    0.17 | 0.1698 |    1432 B |        1.81 |
| &#39;+ telemetry enrichment&#39;           | 100         |   836.8 ns |  2.55 ns |  3.58 ns |   836.8 ns |  3.17 |    0.13 | 0.1593 |    1336 B |        1.69 |
| &#39;+ per-authority pipelines&#39;        | 100         |   949.9 ns | 16.56 ns | 24.27 ns |   965.1 ns |  3.60 |    0.17 | 0.1583 |    1336 B |        1.69 |
