# Benchmark run — 2026-08-29

A full re-measurement of every benchmark in `benchmarks/HttpResilience.NET.Benchmarks`, archived so the
next run has something to be compared against that is not the release baseline itself.

```bash
dotnet run --project benchmarks/HttpResilience.NET.Benchmarks -c Release -- --filter "*" --job medium --memory
```

BenchmarkDotNet v0.15.8, macOS Tahoe 26.5.2 (Darwin 25.5.0), Apple M4 (10 cores), .NET SDK 10.0.100,
`MediumRun` — 15 iterations, 2 launches, 10 warmups. 37 cases, exit 0, no BenchmarkDotNet validation
warnings. Same machine and job as the checked-in `after-*.md` reports, so the comparison below is a
like-for-like one.

Raw reports: [PipelineOverhead.md](PipelineOverhead.md), [AuthorityMatching.md](AuthorityMatching.md),
[ClientCreation.md](ClientCreation.md), [HedgingOverhead.md](HedgingOverhead.md),
[LimiterContention.md](LimiterContention.md).

## Verdict

**No regression.** Every allocation figure reproduced byte-for-byte except one, and every mean moved by less
than the dispersion of the rows it sits between.

Allocation is the column worth reading — it is deterministic, which is why
`scripts/check-benchmark-docs.py` compares only that one.

| Benchmark | Allocation vs `docs/benchmarks/README.md` |
| --- | --- |
| Pipeline overhead (16 rows) | identical |
| Authority matching (6 rows) | identical — 0 B throughout |
| Client creation (3 rows) | identical — 128 B throughout |
| Limiter contention (9 rows) | identical |
| Hedging (3 rows) | **one row moved**: hedged GET 47.16 KB → 45.31 KB |

### The one that moved

`hedged GET (attempt is started)` allocated **45.31 KB**, against the **47.16 KB** the summary table quotes
from `after-hedging-overhead.md`. −1.85 KB, −3.9%.

This row is the only one in the suite whose allocation is not deterministic: it is the only benchmark with a
real timer, a 20 ms origin delay and a supplementary attempt that races the primary, so how much of the
second attempt is built before the first completes is a scheduling outcome rather than a fixed code path.
The two rows around it — the standard pipeline against the same slow origin (4.31 KB) and the suppressed POST
(9.15 KB) — both reproduced exactly, which is what says the difference is in the hedged attempt itself and
not in the harness.

The operational claim the table carries is unaffected: a hedged GET still allocates about **ten times** the
standard pipeline for the same call (10.51x here, against the "about eleven times" in the summary), and that
is still the fan-out rather than overhead.

`docs/benchmarks/README.md` and `after-hedging-overhead.md` were **not** updated from this run. Promoting
these numbers means copying all five reports over the `after-*.md` files and editing the summary tables in
the same commit — `scripts/check-benchmark-docs.py` fails the build otherwise, which is the point of it.

## Means, against the summary tables

Timings are a property of the machine that produced them; these were produced on the same one, so they are
comparable here and nowhere else. Read the `Error` and `StdDev` columns in the raw reports before believing a
delta — two of the rows below carry a StdDev over 30 ns.

### Pipeline overhead (`Authorities = 1`)

| Scenario | Summary | This run | Δ |
| --- | ---: | ---: | ---: |
| `IHttpClientFactory` only | 250.1 ns | 253.2 ns | +3.1 |
| Microsoft standard handler | 807.7 ns | 815.5 ns | +7.8 |
| HttpResilience standard | 818.4 ns | 832.5 ns | +14.1 |
| + rate limiter | 1,088.9 ns | 1,090.9 ns | +2.0 |
| + concurrency limiter | 1,096.3 ns | 1,093.8 ns | −2.5 |
| + rate limiter + concurrency cap | 1,084.8 ns | 1,087.1 ns | +2.3 |
| + telemetry enrichment | 814.6 ns | 828.7 ns | +14.1 |
| + per-authority pipelines | 898.7 ns | 869.9 ns | −28.8 |

The package's cost over the platform handler — the number the page is about — is **+17.0 ns** in this run
against +10.7 ns in the summary. Both are inside the ±31.9 ns StdDev this run measured on that row; the
honest reading is that the difference is not resolvable at this iteration count, not that it grew.

### Everything else

| Benchmark | Summary | This run |
| --- | --- | --- |
| Authority matching, 1 authority, allow-listed | 6.842 ns | 6.752 ns |
| Authority matching, 100 authorities, allow-listed | 8.386 ns | 8.597 ns |
| `CreateClient`, bare factory | 16.09 ns | 15.39 ns |
| `CreateClient`, resilience enabled | 32.55 ns | 32.13 ns |
| `CreateClient`, registered but disabled | 15.69 ns | 15.31 ns |
| Hedged GET, slow origin | 22.43 ms | 22.50 ms |
| Limiter, rate, 64 concurrent | 70,105.0 ns | 70,135.0 ns |
| Limiter, concurrency, 64 concurrent | 71,363.7 ns | 70,990.5 ns |

The two claims these rows exist to hold both survive: a **disabled** client is still inside the noise of the
bare factory (15.31 ns against 15.39 ns), and an **enabled** one still costs about +16 ns per `CreateClient`
(+16.74 ns here, +16.46 ns in the summary) for the `IOptionsMonitor.Get(name)` probe that applies
`Timeout:Client`. Limiter overhead under contention is 1.27x at one concurrent request and 1.35–1.37x at
sixty-four — the same bounded rise, still no contention cliff.
