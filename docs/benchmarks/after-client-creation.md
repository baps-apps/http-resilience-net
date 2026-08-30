```

BenchmarkDotNet v0.15.8, macOS Tahoe 26.5.2 (25F84) [Darwin 25.5.0]
Apple M4, 1 CPU, 10 logical and 10 physical cores
.NET SDK 10.0.100
  [Host]    : .NET 10.0.0 (10.0.0, 10.0.25.52411), Arm64 RyuJIT armv8.0-a
  MediumRun : .NET 10.0.0 (10.0.0, 10.0.25.52411), Arm64 RyuJIT armv8.0-a

Job=MediumRun  IterationCount=15  LaunchCount=2  
WarmupCount=10  

```
| Method                               | Mean     | Error    | StdDev   | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------------- |---------:|---------:|---------:|------:|--------:|-------:|----------:|------------:|
| &#39;IHttpClientFactory only&#39;            | 16.09 ns | 0.277 ns | 0.379 ns |  1.00 |    0.03 | 0.0153 |     128 B |        1.00 |
| &#39;resilience enabled&#39;                 | 32.55 ns | 0.302 ns | 0.403 ns |  2.02 |    0.05 | 0.0153 |     128 B |        1.00 |
| &#39;resilience registered but disabled&#39; | 15.69 ns | 0.107 ns | 0.146 ns |  0.98 |    0.02 | 0.0153 |     128 B |        1.00 |
