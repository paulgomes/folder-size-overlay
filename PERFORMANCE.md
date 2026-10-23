# Performance measurements — 6.0

These measurements compare 6.0 with the 5.1 source preserved before the changes. The original measurement date is October 21, 2026.

**Scope:** managed-component microbenchmarks on Linux/.NET 8. They do not measure real Explorer, UI Automation, GDI, disk throughput, physical USB devices, or the application's total Windows CPU and memory use. The production executable targets Windows/.NET Framework 4.8. Allocation means temporary objects created within the measured section, not resident memory or peak RAM.

## Idle scheduling

Besides reducing the cost of repeated operations, 6.0 changes how often those operations are scheduled.

| Component | 5.1 | 6.0 when stable |
| --- | --- | --- |
| Desktop capture, Balanced | Fixed 375 ms wait | Backoff up to 2 seconds; relevant events wake it earlier |
| Explorer capture, Balanced | Fixed 250 ms wait | Backoff up to 2 seconds; relevant events wake it earlier |
| Drawing | Periodic 125 ms timer | Changes grouped within 32 ms; 1-second heartbeat |
| Coordinator, Balanced | Periodic 175 ms wait | Signal/deadline wait, maximum 1-second fallback |

These are scheduling intervals, not measured latency or percentages of CPU saved. Incomplete captures continue at the faster cadence. Validated labels remain drawn during the wait; identity and position events immediately revoke validity. Geometry has a conservative ten-second validity ceiling.

## Coordinator: 2,048 folders, 200 rounds

Five alternating samples per version and scenario were collected after warmup. The production loop uses controlled host, process, and notification adapters, with sleep disabled. These scenarios do not traverse real directory trees.

| Scenario | 5.1 time | 6.0 time | 5.1 allocation | 6.0 allocation |
| --- | ---: | ---: | ---: | ---: |
| Fresh totals | 425.46 ms | 111.94 ms | 124.34 MB | 0.16 MB |
| Monitored totals | 347.94 ms | 118.07 ms | 150.55 MB | 0.16 MB |
| Two busy readers | 207.08 ms | 49.07 ms | 114.53 MB | 0.15 MB |

Temporary allocations decreased by approximately 99.87%–99.89% in these paths. Baseline timing for fresh totals varied substantially in the sandbox, approximately 299–725 ms. The timings are therefore indicative; reduced work, allocations, and correctness checks are stronger evidence than a precise speed multiplier. CPU measurements from this experiment are omitted because sandbox accounting was unsuitable.

The complete samples, adapters, and input hashes are in [the coordinator evidence](evidencias/desempenho-coordenador.json).

## Visual identification: all measured scenarios

Seven alternating trials were collected per case, with result digests compared. The reference algorithm was checked against frozen 5.1 sources. Regression tests also compare ambiguity handling and randomized inputs.

| Operation and batch | 5.1 time | 6.0 time | Allocation, 5.1 → 6.0 |
| --- | ---: | ---: | ---: |
| Initial build: 100,000 ordered names, 6 builds | 52.62 ms | 29.26 ms | 36.23 → 9.75 MB |
| Initial build: 100,000 shuffled names, 6 builds | 66.18 ms | 319.81 ms | 36.23 → 9.75 MB |
| Stable filtering: 100,000 names, 120 captures | 1,057.67 ms | 7.46 ms | 724.60 → 0.00 MB |
| Filtering: 10 new exclusions/capture, 60 captures | 557.53 ms | 4.74 ms | 362.30 → 0.75 MB |
| Desktop: 1,000 items/200 folders, 60 captures | 58.65 ms | 3.42 ms | 0.00 → 0.20 MB |
| Desktop: 50 items/10 folders, 300 captures | 1.05 ms | 0.52 ms | 0.00 → 0.11 MB |

The stable filter's rounded 0.00 MB represents **80 bytes allocated across the measured batch**, not zero memory use by the application. The 1,000-item desktop case allocates about 204.6 KB more per batch to build indices, approximately 3.4 KB per capture, while reducing repeated comparisons.

**A cost that increased:** building the initial index from 100,000 shuffled names requires approximately 42 ms extra per index in this environment. One-time sorting enables reuse and retains approximately 825 KB of additional index data on x64, sharing existing strings. This cost recurs when rebuilding the index, including metadata renewal after 30 seconds. The change does not promise a faster first opening in every case.

See [visual results](evidencias/desempenho-visual.json) and [source verification](evidencias/desempenho-visual-fontes.json).

## Disk reading and persistence

6.0 retains the existing entry-rate budgets, requested CPU limits, and reader concurrency. It reuses valid results/subtotals and adds persistent cache with identity revalidation. The cache can display the last known total sooner, always marked as refreshing. It neither removes the need to calculate an initial total nor independently verifies current contents.

No HD, SSD, or USB throughput improvement was measured for this release. Scanner results previously published for 5.1 belong to that earlier comparison and are not presented as an additional 6.0 gain.

## Verification and reproduction

The final original run passed 29 groups and five compilation checks, reporting 26,320 checks. Counts include repetitions, randomized inputs, and progress assertions; they are not independent Windows scenarios. Source and test hashes remained stable during the run. The installer embeds the exact validated executable and configuration bytes. See [verification](VERIFICATION.md) and [raw records](evidencias/verification.json).

On Linux with Python 3 and the .NET 8 SDK, preserve the repository layout:

```bash
python3 codigo-fonte/testes/Run-LinuxTests.py --dotnet /path/to/dotnet
python3 benchmarks/Run-Performance.py --dotnet /path/to/dotnet
```

The benchmark accepts `--only coordinator`, `--only visual`, and `--out /path/to/results`. It uses a temporary workspace and the included 5.1 sources. See [benchmark instructions](benchmarks/README.md).

## Measuring on Windows

Compare versions on the same machine and Windows build, using the same folders, storage device, profile, DPI, and view mode. Measure first scans, warm navigation, idle periods, continuous changes, and resume separately.

Use **“Desempenho…”** to export the main process's history and measure separate readers as well. For disk, I/O, and aggregate CPU, use an appropriate [Windows Performance Recorder profile](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/recording-for-resource-based-analysis).

Record time to the first value, label hiding during scroll, GDI/USER/handle counts after repeated cycles, and the correctness of displayed totals. Acceptance must identify the exact package hash, not only the version label.
