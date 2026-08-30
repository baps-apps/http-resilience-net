# Benchmarks

Every performance claim in this repository is a number from this directory, not an argument. The raw
BenchmarkDotNet reports are checked in beside this file so a claim can be re-derived rather than trusted —
and `scripts/check-benchmark-docs.py`, which CI runs, fails the build when a figure in the tables below
disagrees with the report it came from.

That script exists because the promise above was not being kept. Every raw report in this directory said
**1,032 B** for the standard pipeline — the parity claim retracted in prose on this very page — while the
tables said **1,336 B**. Re-measured: the tables were right and the reports were stale, left behind by the
change that made `HttpClient.Timeout` finite. Nothing compared them, so the contradiction sat in the
repository through four reviews. Allocation is compared and timings are not, because allocation is
deterministic and a mean is a property of the machine that produced it.

```bash
dotnet run --project benchmarks/HttpResilience.NET.Benchmarks -c Release -- --filter "*" --job medium --memory
```

Apple M4, .NET 10, `MediumRun` (15 iterations, 2 launches, 10 warmups). The origin is an in-memory handler,
so the transport is not the variable: everything measured is per-request pipeline and telemetry work.
`Error`, `StdDev` and `RatioSD` are in every raw report. An earlier revision of these tables hid them, which
is how a row like `+ telemetry enrichment` came to read as *faster* than the plain pipeline without anyone
querying it. Read the dispersion before believing a ratio.

Figures below are the `Authorities = 1` set where a benchmark has that parameter; the raw report carries both.

## Pipeline overhead

| Scenario | Mean | Allocated | vs Microsoft handler |
| --- | ---: | ---: | ---: |
| `IHttpClientFactory` only | 250.1 ns | 792 B | — |
| Microsoft standard handler | 807.7 ns | 1032 B | baseline |
| **HttpResilience standard** | **818.4 ns** | **1336 B** | **+10.7 ns, +304 B** |
| + rate limiter | 1,088.9 ns | 1432 B | +281 ns, +400 B |
| + concurrency limiter | 1,096.3 ns | 1472 B | +289 ns, +440 B |
| + rate limiter + concurrency cap | 1,084.8 ns | 1432 B | +277 ns, +400 B |
| + telemetry enrichment | 814.6 ns | 1336 B | +6.9 ns, +304 B |
| + per-authority pipelines | 898.7 ns | 1336 B | +91 ns, +304 B |

Raw: [after-pipeline-overhead.md](after-pipeline-overhead.md).

### The 304 bytes, and the claim they cost

Earlier revisions of this file said the package allocated **identically** to the handler it configures —
1,032 B against 1,032 B. That was true when it was measured and is **not true now**, and nobody re-ran the
benchmark when it stopped being true.

The 304 bytes are a `CancellationTokenSource`, its timer and its registration, which `HttpClient` creates per
request when `HttpClient.Timeout` is **finite**. `AddStandardResilienceHandler` sets that timeout to infinite;
this package sets it back to a finite `Timeout:Client`, because `Timeout:Total` stops applying when response
headers arrive and nothing else bounds the response *body*. Measured directly: removing the
`ConfigureHttpClient` assignment returns the row to 1,032 B and exact parity.

So the honest statement is not "costs nothing". It is:

> Every strategy this package configures costs nothing over the platform handler. The one fixed cost it adds
> is one `CancellationTokenSource` per request, which is what a finite `HttpClient.Timeout` costs — the price
> of an origin not being able to hold a connection and a buffer open indefinitely by trickling a response body.

304 B per request at 10k req/s is ~3 MB/s of Gen0 garbage. That is a real number and a cheap one for the bound
it buys, but it is a number, and the previous claim of parity should not have survived the change that broke
it. `PipelineAllocationTests` pins it, so the next time it moves a test says so rather than a stale document.

Two later changes touched these paths and were re-measured rather than assumed: the concurrency limiter and
the displaced backstop are now explicit instances passed to Polly (so their statistics can be read) instead of
shapes Polly builds itself, and `Timeout:Client` moved from `ConfigureHttpClient` to a post-configure on
`HttpClientFactoryOptions`. **Every allocation figure in the table above came back byte-identical** — the
limiters are constructed once per client and the post-configure is startup work, so neither is on the request
path. Timings from that run are not quoted, because it was a `--job short` run whose error bars (±48 ns on one
row) make a mean comparison meaningless; allocation is deterministic and is what was checked.

### Limiters

Enabling a rate limiter displaces the standard handler's own limiter, which used to mean turning on rate
limiting silently removed a 1,000-concurrency cap. The backstop is re-applied as its own handler, and one
extra resilience handler is what that costs (+96 B over the plain pipeline). When a client already has a
concurrency cap of its own, validation holds it at or below the backstop and the extra handler is skipped —
the `+ rate limiter + concurrency cap` row costs the same as `+ rate limiter` alone. That skip is the one
configuration in which the backstop is not the thing bounding concurrency; the client's own `Limit` is, and
`ConcurrencyBackstopTests.ConcurrencyBound_StillHolds_WhenBothLimitersAreEnabled` is what proves the bound
still exists.

### Per-authority selection allocates nothing

1,336 B with per-authority pipelines, identical to without: matching an authority costs no allocation at all.
The micro-benchmark below is where that is visible. It does cost time — +91 ns at one authority, and more at
a hundred, because the platform looks the pipeline up per request.

## Authority matching — the per-request hot path

| Case | Authorities | Mean | Allocated |
| --- | ---: | ---: | ---: |
| allow-listed authority | 1 | 6.842 ns | **0 B** |
| unlisted authority (shared key) | 1 | 3.326 ns | **0 B** |
| right host, wrong port | 1 | 6.748 ns | **0 B** |
| allow-listed authority | 100 | 8.386 ns | **0 B** |
| unlisted authority (shared key) | 100 | 3.595 ns | **0 B** |
| right host, wrong port | 100 | 8.328 ns | **0 B** |

Raw: [after-authority-matching.md](after-authority-matching.md). The before-and-after of host normalisation —
which moved matching from `Uri.Host` to `Uri.IdnHost` with the root label removed, at about +1.4 ns, so that
an allow-list stops rejecting a listed host written in punycode or with a trailing dot — is recorded
separately in [authority-matching-normalisation.md](authority-matching-normalisation.md). It is a real
regression, recorded rather than rounded away, and it is under 0.2% of the 818 ns a full pipeline costs.

Zero allocation is the assertion, and there is a unit test (`PipelineKeySelectorAllocationTests`) that fails
on a single byte, measured with `GC.GetAllocatedBytesForCurrentThread`. It measures a **fresh** `Uri` per
iteration as well as a reused one: a `Uri` caches what its properties compute, so a reused request hides
anything a matcher allocates lazily on first access — which is precisely the shape of `IdnHost`. Matching
stays nearly flat from 1 to 100 authorities because the index is keyed by host; the residual growth is the
scheme-and-port scan within a host.

## Client creation — what a typed client pays per request

`ConfigureHttpClient` actions run on every `IHttpClientFactory.CreateClient` call, not once per client, and a
typed client is commonly created per request. A disabled client carries one such action: the notice saying
resilience is registered but switched off.

| Scenario | Mean | Allocated |
| --- | ---: | ---: |
| `IHttpClientFactory` only | 16.09 ns | 128 B |
| resilience enabled | 32.55 ns | 128 B |
| **resilience registered but disabled** | **15.69 ns** | **128 B** |

Raw: [after-client-creation.md](after-client-creation.md).

Read the **delta over the bare factory in the same run**, not absolute numbers across runs. A disabled client
once cost a container resolve and a `ConcurrentDictionary` probe on every call, forever, to re-check a line
already logged; it is now inside the noise of the bare factory — a volatile read on captured state, and no
extra allocation.

An **enabled** client costs about +16 ns per `CreateClient`, which is what applying `Timeout:Client` costs:
an `IOptionsMonitor.Get(name)` probe. Per request for a typed client, and negligible against the ~818 ns the
request itself costs, but it is not free and this table is where that is said. An earlier revision of this
file reported +4.8 ns for the same row, from a run that predates the finite client timeout.

Re-measured after the twelfth review, which added a **second** `HttpClientActions` entry to this path: an
action at index 0 that normalises `HttpClient.Timeout` to infinite, so that "nothing assigned one" is
established rather than inferred from the framework's 100-second default. The delta over the bare factory
moved from 16.60 ns to 16.46 ns — that is, the extra action is not distinguishable from noise at an error of
±0.3 ns, and allocation is byte-identical. Re-measured rather than assumed, because assuming is what put a
retracted parity claim on this page for four reviews.

## Hedging — the path nothing measured until now

Both arms use a **slow** origin (20 ms), because an origin that answers immediately never starts a hedged
attempt at all: a fast-origin benchmark would measure the hedging pipeline with its distinguishing feature
switched off. That is the same mistake that let a whole suite of hedging tests pass while POST bodies arrived
four times.

The delay dominates the wall clock, so read the **allocation** column.

| Scenario | Mean | Allocated |
| --- | ---: | ---: |
| standard pipeline, slow origin | 21.86 ms | 4.31 KB |
| hedged GET (attempt is started) | 22.43 ms | 47.16 KB |
| hedged POST (attempt is suppressed) | 20.59 ms | 9.15 KB |

Raw: [after-hedging-overhead.md](after-hedging-overhead.md).

Two operational facts fall out of this, neither of which was previously measured:

- **Hedging is not a small change.** A hedged request that actually starts a supplementary attempt allocates
  about **eleven times** what the standard pipeline allocates for the same call. Most of that is the second
  attempt's own request, response and context — it is the fan-out, not overhead — but "turn on hedging" is a
  request-path cost as well as an outbound-traffic cost, and the traffic arithmetic in
  [OPERATIONS.md](../OPERATIONS.md) is only half the picture.
- **The safety guard is not free either.** A suppressed POST still costs about twice the standard pipeline:
  the hedging strategy sets up its timer and its attempt context before the `ActionGenerator` declines to
  produce an action. Nothing goes on the wire, which is the guarantee that matters — but a client that hedges
  nothing because everything it sends is a POST is paying for a strategy it never uses, and should be
  registered with `AddHttpResilience` instead.

## Limiter contention

Every other benchmark here is single-threaded, which is the one shape in which a lock is free. A limiter is a
shared mutable object on the request path of every client that enables one, so the number that matters at
scale is the one taken under contention. One operation is N requests issued together and awaited.

| Scenario | Concurrency | Mean | Allocated | vs no limiter |
| --- | ---: | ---: | ---: | ---: |
| no limiter | 1 | 890.0 ns | 1.48 KB | baseline |
| rate limiter | 1 | 1,118.0 ns | 1.58 KB | 1.26x |
| concurrency limiter | 1 | 1,121.7 ns | 1.62 KB | 1.26x |
| no limiter | 8 | 6,713.0 ns | 10.78 KB | baseline |
| rate limiter | 8 | 8,792.1 ns | 11.53 KB | 1.31x |
| concurrency limiter | 8 | 8,846.8 ns | 11.84 KB | 1.32x |
| no limiter | 64 | 52,010.4 ns | 85.16 KB | baseline |
| rate limiter | 64 | 70,105.0 ns | 91.16 KB | 1.35x |
| concurrency limiter | 64 | 71,363.7 ns | 93.66 KB | 1.37x |

Raw: [after-limiter-contention.md](after-limiter-contention.md).

**There is no contention cliff.** The overhead is 1.26x at one concurrent request and 1.35–1.37x at
sixty-four — a mild, bounded rise, not the sharp degradation a contended lock would produce. Both limiters
behave the same, which is expected: they are the same Polly strategy over two `System.Threading.RateLimiting`
types, and this package supplies neither implementation. Budgets here are large enough that nothing is ever
rejected, so what is measured is the cost of *acquiring* a permit, not of refusing one.

## Raw reports

| File | Contents |
| --- | --- |
| `after-pipeline-overhead.md` | Standard pipeline against the platform handler, and each limiter |
| `after-authority-matching.md` | Authority matching, 1 and 100 authorities |
| `after-client-creation.md` | `CreateClient`, enabled and disabled |
| `after-hedging-overhead.md` | Hedged GET and suppressed POST against a slow origin |
| `after-limiter-contention.md` | Both limiters at 1, 8 and 64 concurrent requests |
| `authority-matching-normalisation.md` | Authority matching before and after `IdnHost` normalisation (+1.4 ns, 0 B) |
| `before-pipeline-overhead.md` | Pipeline overhead before the third review — **not** a valid baseline for the current code |
| `before-client-creation.md` | `CreateClient` before the third review — **not** a valid baseline |
| `runs/2026-08-29/` | A full re-measurement on the same machine and job — no regression, one hedging row moved |

The `before-*.md` files predate the fourth review and are kept only to show what moved.
