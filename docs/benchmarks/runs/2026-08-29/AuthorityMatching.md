```

BenchmarkDotNet v0.15.8, macOS Tahoe 26.5.2 (25F84) [Darwin 25.5.0]
Apple M4, 1 CPU, 10 logical and 10 physical cores
.NET SDK 10.0.100
  [Host]    : .NET 10.0.0 (10.0.0, 10.0.25.52411), Arm64 RyuJIT armv8.0-a
  MediumRun : .NET 10.0.0 (10.0.0, 10.0.25.52411), Arm64 RyuJIT armv8.0-a

Job=MediumRun  IterationCount=15  LaunchCount=2  
WarmupCount=10  

```
| Method                            | Authorities | Mean     | Error     | StdDev    | Median   | Ratio | RatioSD | Allocated | Alloc Ratio |
|---------------------------------- |------------ |---------:|----------:|----------:|---------:|------:|--------:|----------:|------------:|
| **&#39;allow-listed authority&#39;**          | **1**           | **6.752 ns** | **0.0608 ns** | **0.0871 ns** | **6.760 ns** |  **1.00** |    **0.02** |         **-** |          **NA** |
| &#39;unlisted authority (shared key)&#39; | 1           | 3.683 ns | 0.4167 ns | 0.5841 ns | 3.355 ns |  0.55 |    0.09 |         - |          NA |
| &#39;right host, wrong port&#39;          | 1           | 6.751 ns | 0.0586 ns | 0.0841 ns | 6.743 ns |  1.00 |    0.02 |         - |          NA |
|                                   |             |          |           |           |          |       |         |           |             |
| **&#39;allow-listed authority&#39;**          | **100**         | **8.597 ns** | **0.0801 ns** | **0.1173 ns** | **8.598 ns** |  **1.00** |    **0.02** |         **-** |          **NA** |
| &#39;unlisted authority (shared key)&#39; | 100         | 3.612 ns | 0.0174 ns | 0.0255 ns | 3.620 ns |  0.42 |    0.01 |         - |          NA |
| &#39;right host, wrong port&#39;          | 100         | 8.304 ns | 0.0394 ns | 0.0565 ns | 8.304 ns |  0.97 |    0.01 |         - |          NA |
