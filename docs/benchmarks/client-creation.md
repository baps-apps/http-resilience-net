```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M4, 1 CPU, 10 logical and 10 physical cores
.NET SDK 10.0.402
  [Host]    : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a
  MediumRun : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a

Job=MediumRun  IterationCount=15  LaunchCount=2  
WarmupCount=10  

```
| Method                               | Mean     | Error    | StdDev   | Median   | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------------- |---------:|---------:|---------:|---------:|------:|--------:|-------:|----------:|------------:|
| &#39;IHttpClientFactory only&#39;            | 34.99 ns | 2.187 ns | 3.066 ns | 34.21 ns |  1.01 |    0.12 | 0.0153 |     128 B |        1.00 |
| &#39;resilience enabled&#39;                 | 54.64 ns | 5.558 ns | 7.971 ns | 49.67 ns |  1.57 |    0.26 | 0.0153 |     128 B |        1.00 |
| &#39;resilience registered but disabled&#39; | 37.32 ns | 4.688 ns | 6.417 ns | 34.81 ns |  1.07 |    0.20 | 0.0153 |     128 B |        1.00 |
