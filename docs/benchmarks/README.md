# Benchmarks

Every performance claim in this repository comes from the raw BenchmarkDotNet reports beside this file.
`scripts/check-benchmark-docs.py` fails when an **Allocated** figure below disagrees with its report; with
`--against`, when it disagrees with a fresh run. Means and ratios are not checked: they depend on the machine.

```bash
dotnet run --project benchmarks/HttpResilience.NET.Benchmarks -c Release -- --filter "*" --job medium --memory \
  --artifacts /tmp/bench
python3 scripts/check-benchmark-docs.py --against /tmp/bench/results
```

Measured 2026-11-09 against 3.0.0: Apple M4, macOS 27.0.1, .NET 10.0.13, `MediumRun` (15 iterations, 2
launches, 10 warmups). The origin is an in-memory handler, so only pipeline and telemetry work is measured.
The run is noisy (bare-factory StdDev 11% of its mean, limiter rows up to 22%), so **trust the Allocated
column, read dispersion in the raw report before trusting a mean, and compare rows only within one run.**
Where a benchmark has an `Authorities` parameter, the figures are the `Authorities = 1` set.

## Pipeline overhead

| Scenario | Mean | Allocated | vs Microsoft handler |
| --- | ---: | ---: | ---: |
| `IHttpClientFactory` only | 558.0 ns | 792 B | — |
| Microsoft standard handler | 1,674.2 ns | 1032 B | baseline |
| **HttpResilience standard** | **1,838.8 ns** | **1336 B** | **+165 ns, +304 B** |
| + rate limiter | 2,623.4 ns | 1432 B | +949 ns, +400 B |
| + concurrency limiter | 2,599.8 ns | 1472 B | +926 ns, +440 B |
| + rate limiter + concurrency cap | 2,265.7 ns | 1432 B | +592 ns, +400 B |
| + telemetry enrichment | 1,912.9 ns | 1336 B | +239 ns, +304 B |
| + per-authority pipelines | 2,118.2 ns | 1336 B | +444 ns, +304 B |

Raw: [pipeline-overhead.md](pipeline-overhead.md).

- **The time difference for the package's own row is noise** (+165 ns here with a StdDev of 153 ns; −8 ns in
  the `Authorities = 100` set). The limiter rows carry StdDevs of 440–570 ns.
- **+304 B per request is the one fixed cost**: the `CancellationTokenSource` (with its timer and registration)
  that `HttpClient` allocates when `HttpClient.Timeout` is finite. The platform sets the timeout to infinite;
  this package restores `Timeout:Client` so the response body is bounded. About 3 MB/s of Gen0 garbage at 10k
  req/s. `PipelineAllocationTests.StandardPipeline_CostsOneCancellationTokenSourceOverThePlatformHandler`
  holds it (ceiling 384 B, Release only).
- **Limiters.** A rate limiter takes the platform's limiter slot, so the backstop moves into its own handler
  (+96 B). A concurrency limiter is its own handler and the backstop stays in the slot (+136 B). With both, no
  backstop handler is added, because the cap is already the bound
  (`ConcurrencyBackstopTests.ConcurrencyBound_StillHolds_WhenBothLimitersAreEnabled`).
- **Per-authority pipelines allocate nothing extra.** They cost some time (+279 ns over one pipeline, inside the
  dispersion): the authority is matched for pipeline selection and again for the per-authority backstop.

## Authority matching

| Case | Authorities | Mean | Allocated |
| --- | ---: | ---: | ---: |
| allow-listed authority | 1 | 17.16 ns | **0 B** |
| unlisted authority (shared key) | 1 | 12.51 ns | **0 B** |
| right host, wrong port | 1 | 16.62 ns | **0 B** |
| allow-listed authority | 100 | 21.79 ns | **0 B** |
| unlisted authority (shared key) | 100 | 14.97 ns | **0 B** |
| right host, wrong port | 100 | 28.31 ns | **0 B** |

Raw: [authority-matching.md](authority-matching.md). Calls `PipelineKeySelector` directly with a warm `Uri`.

The index is a frozen dictionary keyed by `Uri.IdnHost` (trailing dot removed), so 100 authorities cost a few
nanoseconds more than one, not a scan. `PipelineKeySelectorAllocationTests` asserts zero allocation with a
fresh `Uri` per iteration as well as a reused one.

## Client creation

A typed client is often created per request, and `ConfigureHttpClient` actions run on every `CreateClient`.

| Scenario | Mean | Allocated |
| --- | ---: | ---: |
| `IHttpClientFactory` only | 34.99 ns | 128 B |
| resilience enabled | 54.64 ns | 128 B |
| **resilience registered but disabled** | **37.32 ns** | **128 B** |

Raw: [client-creation.md](client-creation.md).

No extra allocation either way (`PipelineAllocationTests.CreateClient_AllocatesNothingOverTheBareFactory`). A
disabled client is within noise of the bare factory. An enabled one costs about +20 ns for the client actions
that still run: the platform's (which sets `HttpClient.Timeout` to infinite) and this package's two that apply
`Timeout:Client`. Options are read once, when the factory options are built.

## Hedging

A **slow** origin (20 ms) and a 5 ms hedging delay, because a fast origin never starts a hedged attempt. The
delay dominates the wall clock, so read the Allocated column.

| Scenario | Mean | Allocated |
| --- | ---: | ---: |
| standard pipeline, slow origin | 23.79 ms | 4.31 KB |
| hedged GET (attempt is started) | 23.64 ms | 46.43 KB |
| hedged POST (attempt is suppressed) | 22.63 ms | 8.97 KB |

Raw: [hedging-overhead.md](hedging-overhead.md).

- **A hedged GET allocates about 11x the standard pipeline**: the second attempt and the cancellation of the
  loser.
- **A suppressed POST allocates about 2x the standard pipeline.** The hedging pipeline's per-request work runs
  before the guard declines the second attempt; nothing extra goes on the wire. A client that sends only
  POSTs should use `AddHttpResilienceHandler`.
- **Both hedged rows vary between runs** (46.4–48.3 KB and 8.97–9.41 KB), because how much of the second
  attempt is built before the first completes depends on scheduling; the standard row is stable.
  `check-benchmark-docs.py --against` allows these two rows 10%; every other row must match exactly.

## Limiter contention

One operation is N concurrent requests. "No limiter" still has the backstop in the limiter slot; each other row
adds one limiter handler. Budgets are large enough that nothing is rejected, so this measures acquiring a permit.

| Scenario | Concurrency | Mean | Allocated | vs no limiter |
| --- | ---: | ---: | ---: | ---: |
| no limiter | 1 | 2,312 ns | 1.48 KB | baseline |
| rate limiter | 1 | 2,746 ns | 1.58 KB | 1.20x |
| concurrency limiter | 1 | 2,837 ns | 1.62 KB | 1.23x |
| no limiter | 8 | 17,226 ns | 10.78 KB | baseline |
| rate limiter | 8 | 23,078 ns | 11.53 KB | 1.35x |
| concurrency limiter | 8 | 25,276 ns | 11.84 KB | 1.48x |
| no limiter | 64 | 135,922 ns | 85.16 KB | baseline |
| rate limiter | 64 | 178,679 ns | 91.16 KB | 1.32x |
| concurrency limiter | 64 | 195,799 ns | 93.66 KB | 1.45x |

Raw: [limiter-contention.md](limiter-contention.md).

**No contention cliff:** 1.20–1.23x at one request, 1.32–1.48x at 8 and 64, not growing from 8 to 64. The two
limiters are not distinguishable within this run's dispersion (RatioSD up to 0.31).
