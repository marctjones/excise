# Live GUI performance scenarios (#1497)

```
sha=86aa2150
tree=clean
load1=1.93
sample=boundary
vmmap=0
repeats=3
set=default
scenarios=null w9-launch-idle irs-page30 irs-zoom altona-scroll altona-close altona-replace-w9 soft-trim-cycle irs-search-redact
systemTabbing=fullscreen
appHome=/private/tmp/excise-gui-perf/r2r
exe=/Users/marc/Documents/GitHub/pdfe/logs/reader-bench-bundle-r2r-head/excise.app/Contents/MacOS/Excise.App
started=2026-10-15T22:17:18Z
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
| `altona-close` | footprintMB | 3 | 713.0 | 703.0 | 714.0 | 11.0 |
| `altona-close` | rssMB | 3 | 668.0 | 661.0 | 671.0 | 10.0 |
| `altona-close` | committedMB | 3 | 364.5 | 358.2 | 367.2 | 9.0 |
| `altona-close` | liveHeapMB | 3 | 349.2 | 346.6 | 349.3 | 2.8 |
| `altona-close` | cpuTotalMs | 3 | 5857.1 | 5807.1 | 5873.9 | 66.9 |
| `altona-replace-w9` | footprintMB | 3 | 710.0 | 701.0 | 713.0 | 12.0 |
| `altona-replace-w9` | rssMB | 3 | 668.0 | 659.0 | 671.0 | 12.0 |
| `altona-replace-w9` | committedMB | 3 | 362.5 | 353.1 | 365.7 | 12.5 |
| `altona-replace-w9` | liveHeapMB | 3 | 348.2 | 347.0 | 348.8 | 1.8 |
| `altona-replace-w9` | cpuTotalMs | 3 | 6498.9 | 6472.4 | 6536.3 | 63.9 |
| `altona-scroll` | footprintMB | 3 | 727.0 | 698.0 | 729.0 | 31.0 |
| `altona-scroll` | rssMB | 3 | 684.0 | 670.0 | 701.0 | 31.0 |
| `altona-scroll` | committedMB | 3 | 379.6 | 366.0 | 398.1 | 32.0 |
| `altona-scroll` | liveHeapMB | 3 | 355.2 | 346.9 | 359.4 | 12.5 |
| `altona-scroll` | cpuTotalMs | 3 | 5555.2 | 5505.4 | 5715.2 | 209.8 |
| `irs-page30` | footprintMB | 3 | 544.0 | 531.0 | 548.0 | 17.0 |
| `irs-page30` | rssMB | 3 | 492.0 | 490.0 | 494.0 | 4.0 |
| `irs-page30` | committedMB | 3 | 80.5 | 77.8 | 80.5 | 2.7 |
| `irs-page30` | liveHeapMB | 3 | 102.4 | 98.3 | 102.6 | 4.3 |
| `irs-page30` | cpuTotalMs | 3 | 4823.8 | 4797.5 | 4849.6 | 52.1 |
| `irs-search-redact` | footprintMB | 3 | 483.0 | 470.0 | 484.0 | 14.0 |
| `irs-search-redact` | rssMB | 3 | 430.0 | 429.0 | 431.0 | 2.0 |
| `irs-search-redact` | committedMB | 3 | 79.6 | 78.1 | 80.0 | 1.8 |
| `irs-search-redact` | liveHeapMB | 3 | 105.5 | 105.4 | 105.6 | 0.2 |
| `irs-search-redact` | cpuTotalMs | 3 | 3301.8 | 3295.9 | 3320.9 | 25.0 |
| `irs-zoom` | footprintMB | 3 | 564.0 | 563.0 | 565.0 | 2.0 |
| `irs-zoom` | rssMB | 3 | 533.0 | 532.0 | 534.0 | 2.0 |
| `irs-zoom` | committedMB | 3 | 79.7 | 79.7 | 79.8 | 0.1 |
| `irs-zoom` | liveHeapMB | 3 | 105.6 | 105.4 | 105.7 | 0.3 |
| `irs-zoom` | cpuTotalMs | 3 | 3565.2 | 3558.9 | 3602.9 | 44.0 |
| `null` | footprintMB | 3 | 199.0 | 199.0 | 199.0 | 0.0 |
| `null` | rssMB | 3 | 247.0 | 247.0 | 247.0 | 0.0 |
| `null` | committedMB | 3 | 8.6 | 8.6 | 8.6 | 0.0 |
| `null` | liveHeapMB | 3 | 21.9 | 21.8 | 21.9 | 0.1 |
| `null` | cpuTotalMs | 3 | 741.8 | 740.9 | 751.3 | 10.4 |
| `soft-trim-cycle` | footprintMB | 3 | 796.0 | 793.0 | 868.0 | 75.0 |
| `soft-trim-cycle` | rssMB | 3 | 746.0 | 746.0 | 823.0 | 77.0 |
| `soft-trim-cycle` | committedMB | 3 | 437.0 | 436.1 | 515.3 | 79.1 |
| `soft-trim-cycle` | liveHeapMB | 3 | 426.8 | 425.2 | 428.4 | 3.1 |
| `soft-trim-cycle` | cpuTotalMs | 3 | 7612.4 | 7542.2 | 7743.9 | 201.7 |
| `w9-launch-idle` | footprintMB | 3 | 377.0 | 376.0 | 378.0 | 2.0 |
| `w9-launch-idle` | rssMB | 3 | 357.0 | 355.0 | 358.0 | 3.0 |
| `w9-launch-idle` | committedMB | 3 | 35.6 | 35.6 | 35.6 | 0.0 |
| `w9-launch-idle` | liveHeapMB | 3 | 47.0 | 47.0 | 47.1 | 0.1 |
| `w9-launch-idle` | cpuTotalMs | 3 | 1795.2 | 1791.9 | 1797.5 | 5.7 |

## The floor every delta is judged against

`floor = max(noise spread, runner overhead, sampler overhead)`

| metric | floor | unit | from |
|---|---:|---|---|
| footprintMB — physical footprint (outer; the #1461/#1496 number) | 75.0 | MB | noiseSpread |
| rssMB — resident set (outer) | 77.0 | MB | noiseSpread |
| committedMB — committed managed heap (in-app) | 79.1 | MB | noiseSpread |
| liveHeapMB — live managed heap (in-app) | 12.5 | MB | noiseSpread |
| cpuTotalMs — cumulative process CPU (in-app) | 209.8 | ms | noiseSpread |

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

### `altona-close`

_#1461's HEADLINE claim, stated as a measurement: does the footprint come back down? Close is the strictly stronger test than replace, because nothing legitimately stays resident afterwards. This is the scenario the whole harness exists for._

3 run(s), 0 step failure(s), 0 boundary(ies) with no outer sample.

| step | op | wall ms | footprint MB | rss MB | committed MB | live MB | frag MB | tiles MB | sp cache MB | gen2 | ok |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| baseline | baseline | 0.0 | 198.0 | 242.0 | 8.6 | 21.5 | 2.5 | 0.0 | 0.0 | 0 | yes |
| pre-open | mark | 0.1 | 199.0 | 242.0 | 8.6 | 21.6 | 2.5 | 0.0 | 0.0 | 0 | yes |
| open-altona | open | 30.9 | 449.0 | 429.0 | 16.8 | 45.0 | 5.8 | 0.0 | 0.0 | 0 | yes |
| view-continuous | viewmode | 0.0 | 492.0 | 469.0 | 170.8 | 192.6 | 23.8 | 1.5 | 0.0 | 4 | yes |
| idle-after-open | waitidle | 2423.3 | 557.0 | 603.0 | 334.0 | 247.7 | 112.4 | 2.5 | 0.0 | 7 | yes |
| scroll-17 | scrollpages | 747.7 | 713.0 | 663.0 | 342.6 | 324.1 | 46.3 | 10.9 | 0.0 | 10 | yes |
| idle-after-scroll | waitidle | 1089.6 | 640.0 | 668.0 | 364.5 | 348.9 | 42.9 | 11.4 | 0.0 | 10 | yes |
| close-altona | close | 16.4 | 352.0 | 338.0 | 364.5 | 349.3 | 42.9 | 0.0 | 0.0 | 10 | yes |
| idle-after-close | waitidle | 484.4 | 353.0 | 338.0 | 32.9 | 23.9 | 9.0 | 0.0 | 0.0 | 11 | yes |
| idle-45s | idle | 45001.7 | 306.0 | 338.0 | 32.8 | 23.0 | 9.2 | 0.0 | 0.0 | 12 | yes |

Footprint: baseline 198.0 MB -> peak 713.0 MB -> final 306.0 MB (**108.0 MB above baseline at the end**).

Render/trim metrics (from the #1491 sink): bandRenderP50Ms=102.1, bandRenderP99Ms=670.4, bandRenders=16, cacheTrimRequests=2.0, heapReclaimP50Ms=12.0, heapReclaims=2, singleRenders=0

### `altona-replace-w9`

_#1481: replacing a document runs ReleasedMemoryReclaimer (aggressive, blocking, compacting) and dropped footprint to 421 MB in 18 ms by hand. This is that path, repeatable._

3 run(s), 0 step failure(s), 0 boundary(ies) with no outer sample.

| step | op | wall ms | footprint MB | rss MB | committed MB | live MB | frag MB | tiles MB | sp cache MB | gen2 | ok |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| baseline | baseline | 0.0 | 198.0 | 242.0 | 8.6 | 21.6 | 2.4 | 0.0 | 0.0 | 0 | yes |
| pre-open | mark | 0.2 | 199.0 | 242.0 | 8.6 | 21.6 | 2.4 | 0.0 | 0.0 | 0 | yes |
| open-altona | open | 32.4 | 440.0 | 422.0 | 16.8 | 45.0 | 5.8 | 0.0 | 0.0 | 0 | yes |
| view-continuous | viewmode | 0.0 | 489.0 | 464.0 | 192.5 | 199.6 | 12.2 | 2.0 | 0.0 | 4 | yes |
| idle-after-open | waitidle | 2311.6 | 558.0 | 605.0 | 335.7 | 262.6 | 100.2 | 2.5 | 0.0 | 6 | yes |
| scroll-17 | scrollpages | 738.8 | 701.0 | 654.0 | 331.2 | 321.6 | 35.3 | 10.9 | 0.0 | 9 | yes |
| idle-after-scroll | waitidle | 1209.2 | 631.0 | 659.0 | 353.1 | 347.0 | 32.0 | 11.4 | 0.0 | 9 | yes |
| replace-with-w9 | open | 41.8 | 446.0 | 394.0 | 35.0 | 28.7 | 8.6 | 0.0 | 0.0 | 10 | yes |
| idle-after-replace | waitidle | 1331.2 | 450.0 | 398.0 | 30.6 | 47.5 | 0.0 | 20.6 | 0.0 | 11 | yes |
| idle-45s | idle | 45001.7 | 371.0 | 397.0 | 30.6 | 37.0 | 2.3 | 10.3 | 0.0 | 12 | yes |

Footprint: baseline 198.0 MB -> peak 701.0 MB -> final 371.0 MB (**173.0 MB above baseline at the end**).

Render/trim metrics (from the #1491 sink): bandRenderP50Ms=80.0, bandRenderP99Ms=696.1, bandRenders=17, cacheTrimRequests=2.0, heapReclaimP50Ms=13.0, heapReclaims=2, singleRenders=0

### `altona-scroll`

_excise's worst measured metric against a native viewer: #1461 recorded 1440-2432 MB RSS after a full scroll against Preview's 446 MB on the same file. 122 MB, 17 pages, image-heavy._

3 run(s), 0 step failure(s), 0 boundary(ies) with no outer sample.

| step | op | wall ms | footprint MB | rss MB | committed MB | live MB | frag MB | tiles MB | sp cache MB | gen2 | ok |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| baseline | baseline | 0.0 | 199.0 | 242.0 | 8.6 | 21.6 | 2.4 | 0.0 | 0.0 | 0 | yes |
| pre-open | mark | 0.1 | 199.0 | 242.0 | 8.6 | 21.6 | 2.4 | 0.0 | 0.0 | 0 | yes |
| open-altona | open | 31.3 | 448.0 | 430.0 | 16.8 | 45.0 | 5.8 | 0.0 | 0.0 | 0 | yes |
| view-continuous | viewmode | 0.0 | 486.0 | 463.0 | 170.8 | 195.4 | 21.1 | 2.0 | 0.0 | 4 | yes |
| idle-after-open | waitidle | 2060.9 | 554.0 | 600.0 | 333.1 | 245.9 | 111.0 | 2.5 | 0.0 | 7 | yes |
| scroll-17 | scrollpages | 736.0 | 729.0 | 697.0 | 398.1 | 359.3 | 63.5 | 11.4 | 0.0 | 10 | yes |
| idle-after-scroll | waitidle | 968.5 | 661.0 | 701.0 | 398.1 | 359.4 | 63.5 | 11.4 | 0.0 | 10 | yes |
| idle-45s | idle | 45001.5 | 359.0 | 406.0 | 103.4 | 107.2 | 3.1 | 4.4 | 0.0 | 11 | yes |

Footprint: baseline 199.0 MB -> peak 729.0 MB -> final 359.0 MB (**160.0 MB above baseline at the end**).

Render/trim metrics (from the #1491 sink): bandRenderP50Ms=100.8, bandRenderP99Ms=601.8, bandRenders=16, cacheTrimRequests=2.0, heapReclaims=1, singleRenders=0

### `irs-page30`

_#1469: ~650-710 MB after opening a 4 MB text-only document with zero image XObjects. Paging 30 pages is the same interaction the 09-11..09-14 hand-driven readings used (1104 -> 838 -> 720 MB footprint), so this scenario is what makes those comparable._

3 run(s), 0 step failure(s), 0 boundary(ies) with no outer sample.

| step | op | wall ms | footprint MB | rss MB | committed MB | live MB | frag MB | tiles MB | sp cache MB | gen2 | ok |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| baseline | baseline | 0.0 | 198.0 | 242.0 | 8.6 | 21.6 | 2.4 | 0.0 | 0.0 | 0 | yes |
| pre-open | mark | 0.1 | 199.0 | 242.0 | 8.6 | 21.6 | 2.4 | 0.0 | 0.0 | 0 | yes |
| open-irs | open | 110.4 | 399.0 | 360.0 | 47.7 | 47.4 | 10.3 | 0.0 | 0.0 | 3 | yes |
| view-single | viewmode | 2.0 | 462.0 | 407.0 | 49.4 | 61.7 | 10.9 | 20.6 | 0.0 | 5 | yes |
| idle-after-open | waitidle | 2663.3 | 424.0 | 443.0 | 80.5 | 102.4 | 3.9 | 20.6 | 20.6 | 10 | yes |
| page-30 | pageby | 3655.5 | 544.0 | 490.0 | 76.9 | 92.8 | 17.3 | 20.6 | 61.8 | 24 | yes |
| idle-after-paging | waitidle | 485.2 | 544.0 | 490.0 | 76.9 | 92.9 | 17.3 | 20.6 | 61.8 | 24 | yes |
| idle-20s | idle | 20001.0 | 466.0 | 490.0 | 76.9 | 93.2 | 17.3 | 20.6 | 61.8 | 24 | yes |

Footprint: baseline 198.0 MB -> peak 544.0 MB -> final 466.0 MB (**268.0 MB above baseline at the end**).

Render/trim metrics (from the #1491 sink): bandRenders=1, cacheTrimRequests=1.0, heapReclaims=0, singleRenders=1

### `irs-search-redact`

_The redaction path's live memory cost, which no headless harness measures with a real compositor attached. Deliberately does NOT close afterwards: a redacted document is dirty, and closing it would wait on the unsaved-changes dialog._

3 run(s), 0 step failure(s), 0 boundary(ies) with no outer sample.

| step | op | wall ms | footprint MB | rss MB | committed MB | live MB | frag MB | tiles MB | sp cache MB | gen2 | ok |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| baseline | baseline | 0.0 | 198.0 | 242.0 | 8.6 | 21.5 | 2.5 | 0.0 | 0.0 | 0 | yes |
| pre-open | mark | 0.1 | 199.0 | 242.0 | 8.6 | 21.6 | 2.5 | 0.0 | 0.0 | 0 | yes |
| open-irs | open | 112.0 | 412.0 | 360.0 | 47.7 | 47.2 | 10.3 | 0.0 | 0.0 | 3 | yes |
| view-continuous | viewmode | 0.0 | 413.0 | 360.0 | 49.4 | 60.0 | 11.0 | 20.6 | 0.0 | 5 | yes |
| idle-after-open | waitidle | 2299.4 | 459.0 | 404.0 | 77.4 | 101.1 | 9.4 | 20.6 | 0.0 | 10 | yes |
| search-income | search | 122.2 | 481.0 | 426.0 | 78.1 | 104.9 | 9.4 | 30.9 | 0.0 | 10 | yes |
| idle-after-search | waitidle | 726.7 | 412.0 | 429.0 | 78.1 | 105.0 | 9.4 | 30.9 | 0.0 | 10 | yes |
| redact-income | redacttext | 1.8 | 483.0 | 429.0 | 78.1 | 105.1 | 9.4 | 30.9 | 0.0 | 10 | yes |
| idle-after-redact | waitidle | 969.9 | 411.0 | 429.0 | 78.1 | 105.2 | 9.4 | 30.9 | 0.0 | 10 | yes |
| idle-20s | idle | 20001.5 | 405.0 | 429.0 | 78.1 | 105.5 | 9.4 | 30.9 | 0.0 | 10 | yes |

Footprint: baseline 198.0 MB -> peak 483.0 MB -> final 405.0 MB (**207.0 MB above baseline at the end**).

Render/trim metrics (from the #1491 sink): bandRenders=1, cacheTrimRequests=2.0, heapReclaims=0, singleRenders=0

### `irs-zoom`

_Zoom changes the render DPI, so it is the axis that multiplies tile bytes without touching page count. 25/100/500% brackets the range a reader actually uses._

3 run(s), 0 step failure(s), 0 boundary(ies) with no outer sample.

| step | op | wall ms | footprint MB | rss MB | committed MB | live MB | frag MB | tiles MB | sp cache MB | gen2 | ok |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| baseline | baseline | 0.0 | 199.0 | 242.0 | 8.6 | 21.6 | 2.4 | 0.0 | 0.0 | 0 | yes |
| pre-open | mark | 0.1 | 199.0 | 242.0 | 8.6 | 21.6 | 2.4 | 0.0 | 0.0 | 0 | yes |
| open-irs | open | 112.7 | 398.0 | 360.0 | 47.4 | 47.2 | 10.3 | 0.0 | 0.0 | 3 | yes |
| view-continuous | viewmode | 0.0 | 398.0 | 360.0 | 48.7 | 59.1 | 11.3 | 20.6 | 0.0 | 5 | yes |
| idle-after-open | waitidle | 2300.5 | 446.0 | 406.0 | 78.7 | 105.5 | 9.4 | 20.6 | 0.0 | 10 | yes |
| zoom-25 | zoom | 0.4 | 440.0 | 410.0 | 78.7 | 105.6 | 9.4 | 20.6 | 0.0 | 10 | yes |
| idle-zoom-25 | waitidle | 726.1 | 441.0 | 411.0 | 79.7 | 101.6 | 7.9 | 23.9 | 0.0 | 10 | yes |
| zoom-100 | zoom | 0.3 | 504.0 | 461.0 | 79.7 | 101.6 | 7.9 | 23.9 | 0.0 | 10 | yes |
| idle-zoom-100 | waitidle | 484.8 | 505.0 | 462.0 | 79.7 | 94.4 | 15.0 | 49.4 | 0.0 | 11 | yes |
| zoom-500 | zoom | 0.3 | 565.0 | 534.0 | 79.7 | 94.5 | 15.0 | 49.4 | 0.0 | 11 | yes |
| idle-zoom-500 | waitidle | 484.9 | 565.0 | 534.0 | 79.2 | 93.7 | 9.7 | 57.4 | 0.0 | 11 | yes |
| idle-10s | idle | 10001.3 | 487.0 | 534.0 | 79.2 | 93.9 | 9.7 | 57.4 | 0.0 | 11 | yes |

Footprint: baseline 199.0 MB -> peak 565.0 MB -> final 487.0 MB (**288.0 MB above baseline at the end**).

Render/trim metrics (from the #1491 sink): bandRenderP50Ms=15.7, bandRenderP99Ms=24.5, bandRenders=6, cacheTrimRequests=1.0, heapReclaims=0, singleRenders=0

### `null`

_CALIBRATION BASELINE. Launch, settle, idle, quit, with no document ever opened. Subtracted from every other scenario, and compared against a plain launch with the runner switched off to price the runner itself._

3 run(s), 0 step failure(s), 0 boundary(ies) with no outer sample.

| step | op | wall ms | footprint MB | rss MB | committed MB | live MB | frag MB | tiles MB | sp cache MB | gen2 | ok |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| baseline | baseline | 0.0 | 198.0 | 242.0 | 8.6 | 21.5 | 2.4 | 0.0 | 0.0 | 0 | yes |
| launched | mark | 0.2 | 199.0 | 242.0 | 8.6 | 21.6 | 2.4 | 0.0 | 0.0 | 0 | yes |
| idle-after-launch | waitidle | 729.3 | 197.0 | 242.0 | 8.6 | 21.7 | 2.4 | 0.0 | 0.0 | 0 | yes |
| idle-10s | idle | 10001.5 | 197.0 | 247.0 | 8.6 | 21.9 | 2.4 | 0.0 | 0.0 | 0 | yes |

Footprint: baseline 198.0 MB -> peak 199.0 MB -> final 197.0 MB (**-1.0 MB above baseline at the end**).

Render/trim metrics (from the #1491 sink): bandRenders=0, cacheTrimRequests=1.0, heapReclaims=0, singleRenders=0

### `soft-trim-cycle`

_#1478 and #1496 together: trim at Warn then Critical on a scrolled Altona, then scroll back. The trim is requested IN PROCESS through ViewerCacheTrimCoordinator, which is what removes `sudo memory_pressure` (and therefore the human) from the loop. The scroll-back step is there because #1496's own risk list names re-inflation churn: freed samples decode again._

3 run(s), 0 step failure(s), 0 boundary(ies) with no outer sample.

| step | op | wall ms | footprint MB | rss MB | committed MB | live MB | frag MB | tiles MB | sp cache MB | gen2 | ok |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| baseline | baseline | 0.0 | 198.0 | 242.0 | 8.6 | 21.5 | 2.4 | 0.0 | 0.0 | 0 | yes |
| pre-open | mark | 0.1 | 199.0 | 242.0 | 8.6 | 21.6 | 2.4 | 0.0 | 0.0 | 0 | yes |
| open-altona | open | 30.4 | 449.0 | 433.0 | 16.8 | 45.0 | 5.8 | 0.0 | 0.0 | 0 | yes |
| view-continuous | viewmode | 0.0 | 496.0 | 479.0 | 171.1 | 195.4 | 23.4 | 2.0 | 0.0 | 4 | yes |
| idle-after-open | waitidle | 2180.6 | 557.0 | 603.0 | 334.3 | 262.9 | 98.4 | 2.5 | 0.0 | 6 | yes |
| scroll-17 | scrollpages | 747.2 | 699.0 | 666.0 | 344.2 | 322.2 | 48.4 | 10.9 | 0.0 | 9 | yes |
| idle-after-scroll | waitidle | 1089.5 | 628.0 | 671.0 | 366.2 | 344.9 | 45.1 | 11.4 | 0.0 | 9 | yes |
| trim-background | trim | 0.5 | 352.0 | 394.0 | 366.2 | 345.0 | 45.1 | 4.4 | 0.0 | 9 | yes |
| idle-after-background-trim | waitidle | 484.6 | 352.0 | 394.0 | 90.5 | 94.3 | 2.9 | 4.4 | 0.0 | 10 | yes |
| trim-warn | trim | 0.2 | 352.0 | 394.0 | 90.5 | 94.4 | 2.9 | 4.4 | 0.0 | 10 | yes |
| idle-after-warn-trim | waitidle | 484.9 | 352.0 | 395.0 | 90.5 | 94.4 | 2.9 | 4.4 | 0.0 | 11 | yes |
| trim-critical | trim | 0.2 | 352.0 | 395.0 | 90.5 | 94.4 | 2.9 | 0.0 | 0.0 | 11 | yes |
| idle-after-critical-trim | waitidle | 484.6 | 352.0 | 395.0 | 90.5 | 94.3 | 3.0 | 0.0 | 0.0 | 12 | yes |
| scroll-back-17 | scrollpages | 750.1 | 794.0 | 745.0 | 437.0 | 426.7 | 66.9 | 7.5 | 0.0 | 16 | yes |
| idle-after-scroll-back | waitidle | 605.3 | 796.0 | 746.0 | 437.0 | 426.8 | 66.9 | 7.5 | 0.0 | 16 | yes |
| idle-20s | idle | 20001.3 | 715.0 | 746.0 | 437.0 | 419.1 | 35.6 | 7.5 | 0.0 | 16 | yes |

Footprint: baseline 198.0 MB -> peak 796.0 MB -> final 715.0 MB (**517.0 MB above baseline at the end**).

Render/trim metrics (from the #1491 sink): bandRenderP50Ms=118.1, bandRenderP99Ms=666.2, bandRenders=31, cacheTrimRequests=4.0, heapReclaimP50Ms=7.0, heapReclaims=3, singleRenders=0

### `w9-launch-idle`

_The fixed floor: a one-page form with nothing heavy in it. #1496 measured 336 MB footprint / 120 MB committed heap here, and every other number in the family is read against it._

3 run(s), 0 step failure(s), 0 boundary(ies) with no outer sample.

| step | op | wall ms | footprint MB | rss MB | committed MB | live MB | frag MB | tiles MB | sp cache MB | gen2 | ok |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| baseline | baseline | 0.0 | 199.0 | 242.0 | 8.6 | 21.5 | 2.4 | 0.0 | 0.0 | 0 | yes |
| pre-open | mark | 0.1 | 199.0 | 242.0 | 8.6 | 21.6 | 2.4 | 0.0 | 0.0 | 0 | yes |
| open-w9 | open | 53.3 | 377.0 | 339.0 | 25.9 | 31.6 | 5.4 | 0.0 | 0.0 | 0 | yes |
| view-continuous | viewmode | 0.0 | 378.0 | 341.0 | 35.4 | 44.4 | 8.7 | 20.6 | 0.0 | 2 | yes |
| idle-after-open | waitidle | 1696.9 | 322.0 | 357.0 | 35.6 | 46.7 | 1.5 | 20.6 | 0.0 | 2 | yes |
| idle-20s | idle | 20000.9 | 318.0 | 358.0 | 35.6 | 47.0 | 1.5 | 20.6 | 0.0 | 2 | yes |

Footprint: baseline 199.0 MB -> peak 378.0 MB -> final 318.0 MB (**119.0 MB above baseline at the end**).

Render/trim metrics (from the #1491 sink): bandRenders=1, cacheTrimRequests=1.0, heapReclaims=0, singleRenders=0

