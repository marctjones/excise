# NativeAOT: why a scanned PDF's memory was not returned (#2072), and the new NativeAOT baseline, 2026-10-16

**Build shape of every number here: NativeAOT** (`scripts/build-macos-app.sh --aot`), except where a row says ReadyToRun.
Machine: Apple M5, 24 GB, macOS 26.6.2. In-app scenario runner, launched in the background (`open -g`), no synthetic input.

## Cause

After a scanned PDF is closed, the NativeAOT process held about 430 MB; ReadyToRun fell to 235 MB. `vmmap` at the end of
`scan-scroll-close` (`logs/perf-bg3`, not committed):

| | NativeAOT | ReadyToRun |
|---|---|---|
| physical footprint | 427 MB | 235 MB |
| default malloc zone, fragmented | 219 MB (94%) | 7 MB (28%) |
| `MALLOC_SMALL (empty)` dirty | 211 MB | 0.4 MB |
| `MALLOC_LARGE (empty)` | 65 MB | 0 |
| managed heap committed | 20 MB | 19 MB |

The memory is not managed and not live: Skia's pixel buffers were freed, and libmalloc kept the freed pages dirty in regions
it reports as empty. ReadyToRun gives them back over the next ~30 s; an idle NativeAOT process does not. What the ReadyToRun
runtime does that makes libmalloc evict is not established; "an idle process makes no malloc calls" fits the data (a burst of
native alloc/free after the collection returned about 100 MB, `MallocMaxMagazines=1` and `malloc_zone_pressure_relief`
returned nothing) but was not shown directly.

## Fix

`LSEnvironment` in the bundle's Info.plist sets `MallocSpaceEfficient=1` (libmalloc reads its environment at process start,
so it cannot be set from inside the app). Options tried on the unmodified NativeAOT build, `scan-scroll-close`, footprint 45 s
after close: none 426 MB, `MallocSpaceEfficient=1` 135, `MallocLargeCache=0` 142, `MallocAggressiveMadvise=1` 425,
`MallocRecircRetainedRegions=0` 426, `MallocDeferredReclaim=1` 426, `MallocNanoZone=0` 427 (`malloc-option-sweep-scan.txt`).

Cost, 3 repeats per arm, interleaved with a control: CPU and scroll/zoom wall time unchanged (altona-scroll CPU 2.65 s control,
2.67 s with the option; irs-zoom 1.27 / 1.29 s). Footprint at the end of: altona-scroll 251 -> 215 MB, altona-close 202 -> 148 MB,
irs-zoom 406 -> 239 MB, scan 427 -> 135 MB. Peak during the scan scroll 528 -> 431 MB.

## Window title bar

The NativeAOT binary is linked against the macOS 27.0 SDK, the ReadyToRun apphost against 15.5 (`otool -l`, `LC_BUILD_VERSION`).
macOS draws the window chrome in the style of the SDK an app was linked with, so the left-aligned darker title bar is the
current system look and the ReadyToRun one is the compatibility look. Not a defect.

## NativeAOT baseline

`gui-scenarios-summary.md` / `.tsv`: the default scenario set, 3 repeats, 0 step failures, the Info.plist option in effect.
A history point is in `tests/gui-perf-history.jsonl` (`buildShape: NativeAOT`; the two earlier points predate shape
recording). `tests/perf-budgets/workflow-budgets.json` (engine allocation and wall time of the command-line workflows) is not a
GUI-shape measurement and was not re-baselined here.

## Not done

- `irs-redact-save`: the redaction save blocks the UI thread (CPU 20.6 s in the scenario); see the issue filed from it.
- Real-key responsiveness (`reader_speed_bench.py`) with the option was not re-run: it needs the foreground.
