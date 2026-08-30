```

BenchmarkDotNet v0.15.8, macOS Tahoe 26.5.2 (25F84) [Darwin 25.5.0]
Apple M4, 1 CPU, 10 logical and 10 physical cores
.NET SDK 10.0.100
  [Host]    : .NET 10.0.0 (10.0.0, 10.0.25.52411), Arm64 RyuJIT armv8.0-a
  MediumRun : .NET 10.0.0 (10.0.0, 10.0.25.52411), Arm64 RyuJIT armv8.0-a

Job=MediumRun  IterationCount=15  LaunchCount=2  
WarmupCount=10  

```
| Method                       | Authorities | Mean       | Median     | Ratio | Gen0   | Allocated | Alloc Ratio |
|----------------------------- |------------ |-----------:|-----------:|------:|-------:|----------:|------------:|
| **&#39;IHttpClientFactory only&#39;**    | **1**           |   **267.6 ns** |   **264.3 ns** |  **1.00** | **0.0944** |     **792 B** |        **1.00** |
| &#39;Microsoft standard handler&#39; | 1           |   988.9 ns |   933.7 ns |  3.70 | 0.1230 |    1032 B |        1.30 |
| &#39;HttpResilience standard&#39;    | 1           | 1,339.1 ns | 1,355.6 ns |  5.01 | 0.1221 |    1032 B |        1.30 |
| &#39;+ rate limiter&#39;             | 1           | 1,298.3 ns | 1,353.2 ns |  4.86 | 0.1183 |     992 B |        1.25 |
| &#39;+ concurrency limiter&#39;      | 1           | 1,650.2 ns | 1,713.4 ns |  6.18 | 0.1392 |    1168 B |        1.47 |
| &#39;+ telemetry enrichment&#39;     | 1           |   967.9 ns |   990.3 ns |  3.62 | 0.1221 |    1032 B |        1.30 |
| &#39;+ per-authority pipelines&#39;  | 1           |   885.3 ns |   881.6 ns |  3.32 | 0.1307 |    1096 B |        1.38 |
|                              |             |            |            |       |        |           |             |
| **&#39;IHttpClientFactory only&#39;**    | **100**         |   **257.7 ns** |   **257.5 ns** |  **1.00** | **0.0944** |     **792 B** |        **1.00** |
| &#39;Microsoft standard handler&#39; | 100         |   831.0 ns |   832.1 ns |  3.22 | 0.1230 |    1032 B |        1.30 |
| &#39;HttpResilience standard&#39;    | 100         |   844.6 ns |   842.1 ns |  3.28 | 0.1230 |    1032 B |        1.30 |
| &#39;+ rate limiter&#39;             | 100         |   809.9 ns |   808.2 ns |  3.14 | 0.1183 |     992 B |        1.25 |
| &#39;+ concurrency limiter&#39;      | 100         | 1,068.4 ns | 1,069.1 ns |  4.15 | 0.1392 |    1168 B |        1.47 |
| &#39;+ telemetry enrichment&#39;     | 100         |   789.9 ns |   786.1 ns |  3.06 | 0.1230 |    1032 B |        1.30 |
| &#39;+ per-authority pipelines&#39;  | 100         |   936.9 ns |   948.8 ns |  3.64 | 0.1307 |    1096 B |        1.38 |
