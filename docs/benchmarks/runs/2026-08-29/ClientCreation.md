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
| &#39;IHttpClientFactory only&#39;            | 15.39 ns | 0.078 ns | 0.101 ns |  1.00 |    0.01 | 0.0153 |     128 B |        1.00 |
| &#39;resilience enabled&#39;                 | 32.13 ns | 0.085 ns | 0.122 ns |  2.09 |    0.02 | 0.0153 |     128 B |        1.00 |
| &#39;resilience registered but disabled&#39; | 15.31 ns | 0.031 ns | 0.042 ns |  1.00 |    0.01 | 0.0153 |     128 B |        1.00 |
