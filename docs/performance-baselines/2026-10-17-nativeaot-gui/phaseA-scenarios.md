# Live GUI performance scenarios (#1497)

```
sha=e07e050c
tree=clean
load1=4.36
sample=boundary
vmmap=0
repeats=3
set=default
scenarios=business-open business-search irs-redact-save
systemTabbing=fullscreen
appHome=/private/tmp/excise-gui-perf/phaseA
exe=/Users/marc/Documents/GitHub/pdfe/logs/reader-bench-bundle-aot-fix/excise.app/Contents/MacOS/Excise.App
buildShape=NativeAOT
started=2026-10-17T06:20:18Z
```

## How to read this

Every number below is a measurement on ONE machine under whatever load it
had at the time (`load1` is recorded per row). Absolute footprint is not
comparable across machines and is deliberately not gated anywhere.

**What you CAN conclude from this output**

- Whether the footprint after a close or replace returns toward the
  pre-open floor — the `baseline` boundary is written before any step, so
  "comes back down" has something to come back down to (#1461).
- Whether a change moved a metric by more than this machine's measured
  floor. Anything at or below the floor prints `BELOW-FLOOR`.
- How the footprint splits between managed heap (`committedMB`,
  `liveHeapMB`, `fragmentedMB`) and everything else — the #1496 question.
- Band render p50/p99 and the tile-cache counters, per scenario.

**What you CANNOT conclude**

- Whether scrolling *feels* smooth. That stays human; p50/p99 and blank
  tiles are proxies, not the thing.
- That in-app driving equals a user's input path. It skips input dispatch
  and hit testing. The driving-fidelity calibration BOUNDS that residual;
  it does not remove it.
- Anything about another machine, or about this one under different load.
- That a `FLOOR-UNKNOWN` delta is real. It means nobody measured the noise.

## Noise floor

| scenario | metric | n | median | min | max | spread |
|---|---|---:|---:|---:|---:|---:|
| `business-open` | footprintMB | 3 | 382.0 | 370.0 | 382.0 | 12.0 |
| `business-open` | rssMB | 3 | 477.0 | 473.0 | 478.0 | 5.0 |
| `business-open` | committedMB | 3 | 79.3 | 78.9 | 79.6 | 0.7 |
| `business-open` | liveHeapMB | 3 | 112.6 | 112.4 | 113.5 | 1.0 |
| `business-open` | cpuTotalMs | 3 | 3683.1 | 3667.5 | 3688.4 | 21.0 |
| `business-search` | footprintMB | 3 | 319.0 | 318.0 | 334.0 | 16.0 |
| `business-search` | rssMB | 3 | 298.0 | 298.0 | 300.0 | 2.0 |
| `business-search` | committedMB | 3 | 79.2 | 79.1 | 81.5 | 2.4 |
| `business-search` | liveHeapMB | 3 | 112.9 | 112.5 | 113.4 | 0.9 |
| `business-search` | cpuTotalMs | 3 | 1576.8 | 1575.6 | 1581.3 | 5.6 |
| `irs-redact-save` | footprintMB | 3 | 484.0 | 484.0 | 497.0 | 13.0 |
| `irs-redact-save` | rssMB | 3 | 471.0 | 471.0 | 471.0 | 0.0 |
| `irs-redact-save` | committedMB | 3 | 249.4 | 249.4 | 249.5 | 0.1 |
| `irs-redact-save` | liveHeapMB | 3 | 225.0 | 225.0 | 225.1 | 0.0 |
| `irs-redact-save` | cpuTotalMs | 3 | 17337.5 | 17307.2 | 17370.4 | 63.2 |

## The floor every delta is judged against

`floor = max(noise spread, runner overhead, sampler overhead)`

| metric | floor | unit | from |
|---|---:|---|---|
| footprintMB — physical footprint (outer; the #1461/#1496 number) | 16.0 | MB | noiseSpread |
| rssMB — resident set (outer) | 5.0 | MB | noiseSpread |
| committedMB — committed managed heap (in-app) | 2.4 | MB | noiseSpread |
| liveHeapMB — live managed heap (in-app) | 1.0 | MB | noiseSpread |
| cpuTotalMs — cumulative process CPU (in-app) | 63.2 | ms | noiseSpread |

## Calibration

### runnerOverhead — NOT measured in this run

The `null` scenario (launch, settle, idle 10s, quit) with the in-app runner ON, against a plain launch of the same bundle with EXCISE_PERF_SCENARIO unset and the app held open for the same wall time then quit by Apple Event, 5 runs each. Question: does having the harness inside the process change the process? Expected to be near zero — the runner is a static class doing Task.Delay and counter reads — and a near-zero result is the correct one, not a failed measurement.

### samplerOverhead — NOT measured in this run

`w9-launch-idle` with the OUTER sampler at none, 5s and 1s, 3 runs each. Question: does watching cost more than the thing watched? Pick the cheapest interval that still resolves the effect being chased.

### drivingFidelity — NOT measured in this run

NOT measured by this script. `irs-page30` driven in-app against the same 30 Page Downs delivered as real keyboard input, once. The residual is input dispatch plus hit testing. This is the honest limit of the instrument: driving through the view model is not a user's input path, and the residual bounds that gap rather than removing it.

To measure it, run the in-app side and then drive the same step by
real keyboard input through computer-use, and compare `bandRenders`,
`bandRenderP50Ms`/`P99` and peak `footprintMB`:

```bash
scripts/run-gui-perf-scenarios.sh --scenario irs-page30 --repeats 1
# then, with the app launched by hand and EXCISE_TRACE_VIEWER set,
# send 30 Page Down keystrokes and compare the same three numbers.
```

### noiseFloor — measured in this run

Every scenario x5 on a quiet machine, load1 recorded per run. Reported as median and min-max spread of each metric's peak.

## Scenarios

### `business-open`

_Open to settled for the long-document regime: a 455-page, 7 MB book (page index, thumbnail placeholders, text index), which the 126-page irs scenarios do not reach. The open step's wall ms is document load, idle-after-open is time until the viewer is quiescent (first page and the render-ahead band drawn); then 30 page heights of scrolling._

3 run(s), 0 step failure(s), 0 boundary(ies) with no outer sample.

| step | op | wall ms | footprint MB | rss MB | committed MB | live MB | frag MB | tiles MB | sp cache MB | gen2 | ui stall ms | ok |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| baseline | baseline | 0.0 | 114.0 | 158.0 | 11.6 | 24.2 | 3.8 | 0.0 | 0.0 | 0 | 0 | yes |
| pre-open | mark | 0.0 | 113.0 | 155.0 | 11.6 | 24.3 | 3.8 | 0.0 | 0.0 | 0 | 1 | yes |
| open-business | open | 249.7 | 302.0 | 266.0 | 66.0 | 97.8 | 7.1 | 19.2 | 0.0 | 3 | 45 | yes |
| view-continuous | viewmode | 0.0 | 302.0 | 266.0 | 66.0 | 91.8 | 1.2 | 19.2 | 0.0 | 3 | 1 | yes |
| idle-after-open | waitidle | 1120.8 | 315.0 | 280.0 | 79.6 | 112.4 | 7.2 | 19.2 | 0.0 | 6 | 2 | yes |
| scroll-30 | scrollpages | 5229.9 | 382.0 | 473.0 | 74.5 | 108.6 | 15.2 | 199.2 | 0.0 | 38 | 9 | yes |
| idle-after-scroll | waitidle | 492.0 | 382.0 | 473.0 | 74.5 | 108.7 | 15.2 | 199.2 | 0.0 | 38 | 1 | yes |
| idle-10s | idle | 10005.0 | 303.0 | 473.0 | 74.5 | 109.0 | 15.2 | 199.2 | 0.0 | 38 | 2 | yes |

UI stall (the in-app heartbeat's worst gap past its 50 ms interval, per boundary): worst per run, median over 3 run(s) 31 ms (min 24, max 45); boundaries with a stall of 100 ms or more, median 0 of 8; the first run's worst is at `open-business`. No floor is known for this metric yet (FLOOR-UNKNOWN), so a difference is a lead, not a verdict.

Footprint: baseline 114.0 MB -> peak 382.0 MB -> final 303.0 MB (**189.0 MB above baseline at the end**).

Render/trim metrics (from the #1491 sink): bandRenderP50Ms=19.4, bandRenderP99Ms=36.6, bandRenders=23, cacheTrimRequests=0, heapReclaims=0, singleRenders=0

### `business-search`

_Search latency and its memory on the 455-page book: the search step's wall ms is the time to settle on a common term (many hits) and then on a rare one (a whole-document scan with few), after the text index is built. The matching rules changed in 2026-10 (case folding, accents, hyphen/space); nothing in the GUI timed them._

3 run(s), 0 step failure(s), 0 boundary(ies) with no outer sample.

| step | op | wall ms | footprint MB | rss MB | committed MB | live MB | frag MB | tiles MB | sp cache MB | gen2 | ui stall ms | ok |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| baseline | baseline | 0.0 | 114.0 | 159.0 | 11.6 | 24.2 | 3.8 | 0.0 | 0.0 | 0 | 0 | yes |
| pre-open | mark | 0.0 | 114.0 | 159.0 | 11.6 | 24.3 | 3.8 | 0.0 | 0.0 | 0 | 1 | yes |
| open-business | open | 237.3 | 284.0 | 263.0 | 61.5 | 98.7 | 0.6 | 19.2 | 0.0 | 3 | 68 | yes |
| view-continuous | viewmode | 0.0 | 284.0 | 263.0 | 63.0 | 99.3 | 0.7 | 19.2 | 0.0 | 3 | 1 | yes |
| idle-after-open | waitidle | 1121.9 | 303.0 | 283.0 | 81.5 | 112.5 | 9.8 | 19.2 | 0.0 | 6 | 1 | yes |
| search-common | search | 125.5 | 319.0 | 300.0 | 80.9 | 100.5 | 21.8 | 38.3 | 0.0 | 7 | 1 | yes |
| idle-after-common | waitidle | 499.1 | 319.0 | 300.0 | 80.9 | 100.6 | 21.8 | 38.3 | 0.0 | 7 | 1 | yes |
| search-rare | search | 125.5 | 319.0 | 299.0 | 80.8 | 107.3 | 16.9 | 38.3 | 0.0 | 7 | 1 | yes |
| idle-after-rare | waitidle | 493.0 | 319.0 | 299.0 | 80.8 | 107.3 | 16.9 | 38.3 | 0.0 | 7 | 1 | yes |
| idle-10s | idle | 10004.7 | 240.0 | 296.0 | 80.8 | 107.6 | 16.9 | 38.3 | 0.0 | 7 | 2 | yes |

UI stall (the in-app heartbeat's worst gap past its 50 ms interval, per boundary): worst per run, median over 3 run(s) 39 ms (min 33, max 68); boundaries with a stall of 100 ms or more, median 0 of 10; the first run's worst is at `open-business`. No floor is known for this metric yet (FLOOR-UNKNOWN), so a difference is a lead, not a verdict.

Footprint: baseline 114.0 MB -> peak 319.0 MB -> final 240.0 MB (**126.0 MB above baseline at the end**).

Render/trim metrics (from the #1491 sink): bandRenderP50Ms=14.6, bandRenderP99Ms=23.5, bandRenders=2, cacheTrimRequests=0, heapReclaims=0, singleRenders=0

### `irs-redact-save`

_The redaction workflow end to end in the GUI process: the apply-and-save time (the saveAs step's wall ms), the memory it peaks at, and whether the UI thread froze while it ran (the ui stall column). irs-search-redact stops at marking because closing a dirty document would wait on a dialog; saveAs applies the pending redactions through the same engine as `excise redact`, writes a scratch copy (deleted afterwards) and leaves the document clean, so this scenario can close it and show what a redacted document gives back._

3 run(s), 0 step failure(s), 0 boundary(ies) with no outer sample.

| step | op | wall ms | footprint MB | rss MB | committed MB | live MB | frag MB | tiles MB | sp cache MB | gen2 | ui stall ms | ok |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| baseline | baseline | 0.0 | 114.0 | 159.0 | 11.6 | 24.2 | 3.9 | 0.0 | 0.0 | 0 | 0 | yes |
| pre-open | mark | 0.0 | 114.0 | 159.0 | 11.6 | 24.3 | 3.9 | 0.0 | 0.0 | 0 | 2 | yes |
| open-irs | open | 91.8 | 298.0 | 268.0 | 52.6 | 63.8 | 19.5 | 0.0 | 0.0 | 2 | 6 | yes |
| view-continuous | viewmode | 0.0 | 285.0 | 259.0 | 54.6 | 65.0 | 19.6 | 20.6 | 0.0 | 4 | 6 | yes |
| idle-after-open | waitidle | 1163.6 | 321.0 | 293.0 | 80.2 | 114.3 | 4.3 | 20.6 | 0.0 | 8 | 4 | yes |
| search-income | search | 123.4 | 329.0 | 303.0 | 81.9 | 106.8 | 4.2 | 30.9 | 0.0 | 8 | 2 | yes |
| idle-after-search | waitidle | 507.9 | 329.0 | 303.0 | 81.9 | 106.9 | 4.2 | 30.9 | 0.0 | 8 | 2 | yes |
| redact-income | redacttext | 3.7 | 328.0 | 302.0 | 81.9 | 107.0 | 4.2 | 30.9 | 0.0 | 8 | 2 | yes |
| save-redacted | saveas | 16130.6 | 497.0 | 471.0 | 249.5 | 223.5 | 16.7 | 30.9 | 0.0 | 59 | 16112 | yes |
| idle-after-save | waitidle | 500.8 | 497.0 | 471.0 | 249.5 | 223.7 | 16.7 | 30.9 | 0.0 | 59 | 2 | yes |
| close-saved | close | 16.3 | 254.0 | 256.0 | 249.5 | 225.1 | 16.7 | 0.0 | 0.0 | 59 | 10 | yes |
| idle-after-close | waitidle | 521.9 | 254.0 | 256.0 | 53.6 | 17.7 | 22.1 | 0.0 | 0.0 | 60 | 2 | yes |
| idle-20s | idle | 20174.1 | 176.0 | 255.0 | 53.6 | 18.0 | 22.1 | 0.0 | 0.0 | 60 | 200 | yes |

UI stall (the in-app heartbeat's worst gap past its 50 ms interval, per boundary): worst per run, median over 3 run(s) 16142 ms (min 16112, max 16168); boundaries with a stall of 100 ms or more, median 2 of 13; the first run's worst is at `save-redacted`. No floor is known for this metric yet (FLOOR-UNKNOWN), so a difference is a lead, not a verdict.

Footprint: baseline 114.0 MB -> peak 497.0 MB -> final 176.0 MB (**62.0 MB above baseline at the end**).

Render/trim metrics (from the #1491 sink): bandRenders=1, cacheTrimRequests=0, heapReclaims=1, singleRenders=0

