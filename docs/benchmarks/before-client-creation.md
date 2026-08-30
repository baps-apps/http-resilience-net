```

BenchmarkDotNet v0.15.8, macOS Tahoe 26.5.2 (25F84) [Darwin 25.5.0]
Apple M4, 1 CPU, 10 logical and 10 physical cores
.NET SDK 10.0.100
  [Host]    : .NET 10.0.0 (10.0.0, 10.0.25.52411), Arm64 RyuJIT armv8.0-a
  MediumRun : .NET 10.0.0 (10.0.0, 10.0.25.52411), Arm64 RyuJIT armv8.0-a

Job=MediumRun  IterationCount=15  LaunchCount=2  
WarmupCount=10  

```
| Method                               | Mean     | Ratio | Gen0   | Allocated | Alloc Ratio |
|------------------------------------- |---------:|------:|-------:|----------:|------------:|
| &#39;IHttpClientFactory only&#39;            | 15.65 ns |  1.00 | 0.0153 |     128 B |        1.00 |
| &#39;resilience enabled&#39;                 | 19.53 ns |  1.25 | 0.0153 |     128 B |        1.00 |
| &#39;resilience registered but disabled&#39; | 35.57 ns |  2.27 | 0.0153 |     128 B |        1.00 |
