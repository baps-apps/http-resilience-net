```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M4, 1 CPU, 10 logical and 10 physical cores
.NET SDK 10.0.402
  [Host]    : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a
  MediumRun : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a

Job=MediumRun  IterationCount=15  LaunchCount=2  
WarmupCount=10  

```
| Method                                | Mean     | Error    | StdDev   | Ratio | RatioSD | Allocated | Alloc Ratio |
|-------------------------------------- |---------:|---------:|---------:|------:|--------:|----------:|------------:|
| &#39;standard pipeline, slow origin&#39;      | 23.79 ms | 2.239 ms | 2.989 ms |  1.01 |    0.16 |   4.31 KB |        1.00 |
| &#39;hedged GET (attempt is started)&#39;     | 23.64 ms | 1.152 ms | 1.689 ms |  1.01 |    0.12 |  46.43 KB |       10.77 |
| &#39;hedged POST (attempt is suppressed)&#39; | 22.63 ms | 1.198 ms | 1.756 ms |  0.96 |    0.12 |   8.97 KB |        2.08 |
