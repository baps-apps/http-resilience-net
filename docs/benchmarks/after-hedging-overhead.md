```

BenchmarkDotNet v0.15.8, macOS Tahoe 26.5.2 (25F84) [Darwin 25.5.0]
Apple M4, 1 CPU, 10 logical and 10 physical cores
.NET SDK 10.0.100
  [Host]    : .NET 10.0.0 (10.0.0, 10.0.25.52411), Arm64 RyuJIT armv8.0-a
  MediumRun : .NET 10.0.0 (10.0.0, 10.0.25.52411), Arm64 RyuJIT armv8.0-a

Job=MediumRun  IterationCount=15  LaunchCount=2  
WarmupCount=10  

```
| Method                                | Mean     | Error    | StdDev   | Ratio | Allocated | Alloc Ratio |
|-------------------------------------- |---------:|---------:|---------:|------:|----------:|------------:|
| &#39;standard pipeline, slow origin&#39;      | 21.86 ms | 0.080 ms | 0.120 ms |  1.00 |   4.31 KB |        1.00 |
| &#39;hedged GET (attempt is started)&#39;     | 22.43 ms | 0.094 ms | 0.138 ms |  1.03 |  47.16 KB |       10.94 |
| &#39;hedged POST (attempt is suppressed)&#39; | 20.59 ms | 0.050 ms | 0.074 ms |  0.94 |   9.15 KB |        2.12 |
