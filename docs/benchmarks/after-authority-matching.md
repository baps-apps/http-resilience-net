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
| **&#39;allow-listed authority&#39;**          | **1**           | **6.842 ns** | **0.0909 ns** | **0.1245 ns** |  **1.00** |    **0.03** |         **-** |          **NA** |
| &#39;unlisted authority (shared key)&#39; | 1           | 3.326 ns | 0.0282 ns | 0.0395 ns |  0.49 |    0.01 |         - |          NA |
| &#39;right host, wrong port&#39;          | 1           | 6.748 ns | 0.0504 ns | 0.0690 ns |  0.99 |    0.02 |         - |          NA |
|                                   |             |          |           |           |       |         |           |             |
| **&#39;allow-listed authority&#39;**          | **100**         | **8.386 ns** | **0.0355 ns** | **0.0474 ns** |  **1.00** |    **0.01** |         **-** |          **NA** |
| &#39;unlisted authority (shared key)&#39; | 100         | 3.595 ns | 0.0127 ns | 0.0187 ns |  0.43 |    0.00 |         - |          NA |
| &#39;right host, wrong port&#39;          | 100         | 8.328 ns | 0.0515 ns | 0.0723 ns |  0.99 |    0.01 |         - |          NA |
