```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M4, 1 CPU, 10 logical and 10 physical cores
.NET SDK 10.0.402
  [Host]    : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a
  MediumRun : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a

Job=MediumRun  IterationCount=15  LaunchCount=2  
WarmupCount=10  

```
| Method                            | Authorities | Mean     | Error    | StdDev   | Median    | Ratio | RatioSD | Allocated | Alloc Ratio |
|---------------------------------- |------------ |---------:|---------:|---------:|----------:|------:|--------:|----------:|------------:|
| **&#39;allow-listed authority&#39;**          | **1**           | **17.16 ns** | **1.368 ns** | **1.873 ns** | **16.996 ns** |  **1.01** |    **0.15** |         **-** |          **NA** |
| &#39;unlisted authority (shared key)&#39; | 1           | 12.51 ns | 4.349 ns | 6.509 ns |  9.311 ns |  0.74 |    0.39 |         - |          NA |
| &#39;right host, wrong port&#39;          | 1           | 16.62 ns | 1.120 ns | 1.571 ns | 16.575 ns |  0.98 |    0.14 |         - |          NA |
|                                   |             |          |          |          |           |       |         |           |             |
| **&#39;allow-listed authority&#39;**          | **100**         | **21.79 ns** | **1.714 ns** | **2.512 ns** | **21.215 ns** |  **1.01** |    **0.16** |         **-** |          **NA** |
| &#39;unlisted authority (shared key)&#39; | 100         | 14.97 ns | 5.867 ns | 8.414 ns | 11.740 ns |  0.70 |    0.39 |         - |          NA |
| &#39;right host, wrong port&#39;          | 100         | 28.31 ns | 4.794 ns | 7.027 ns | 25.143 ns |  1.31 |    0.35 |         - |          NA |
