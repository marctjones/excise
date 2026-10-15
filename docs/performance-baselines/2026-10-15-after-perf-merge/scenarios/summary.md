# Live GUI performance scenarios (#1497)

```
sha=415ae973
tree=clean
load1=1.59
sample=boundary
vmmap=0
repeats=3
set=default
scenarios=null w9-launch-idle irs-page30 irs-zoom altona-scroll altona-close altona-replace-w9 soft-trim-cycle irs-search-redact
systemTabbing=fullscreen
appHome=/private/tmp/excise-gui-perf/run
exe=/Users/marc/Documents/GitHub/pdfe/logs/reader-bench-bundle-after/excise.app/Contents/MacOS/Excise.App
started=2026-10-15T04:52:28Z
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
| `altona-close` | footprintMB | 3 | 715.0 | 702.0 | 717.0 | 15.0 |
| `altona-close` | rssMB | 3 | 673.0 | 659.0 | 674.0 | 15.0 |
| `altona-close` | committedMB | 3 | 369.2 | 355.9 | 372.7 | 16.8 |
| `altona-close` | liveHeapMB | 3 | 348.0 | 347.4 | 353.4 | 6.0 |
| `altona-close` | cpuTotalMs | 3 | 5794.0 | 5713.5 | 5798.8 | 85.3 |
| `altona-replace-w9` | footprintMB | 3 | 705.0 | 704.0 | 716.0 | 12.0 |
| `altona-replace-w9` | rssMB | 3 | 662.0 | 662.0 | 672.0 | 10.0 |
| `altona-replace-w9` | committedMB | 3 | 363.2 | 358.0 | 366.5 | 8.5 |
| `altona-replace-w9` | liveHeapMB | 3 | 346.8 | 346.2 | 348.6 | 2.4 |
| `altona-replace-w9` | cpuTotalMs | 3 | 6507.5 | 6392.3 | 6592.7 | 200.4 |
| `altona-scroll` | footprintMB | 3 | 697.0 | 695.0 | 711.0 | 16.0 |
| `altona-scroll` | rssMB | 3 | 666.0 | 654.0 | 668.0 | 14.0 |
| `altona-scroll` | committedMB | 3 | 363.0 | 352.9 | 365.9 | 13.0 |
| `altona-scroll` | liveHeapMB | 3 | 346.7 | 346.4 | 346.7 | 0.3 |
| `altona-scroll` | cpuTotalMs | 3 | 5490.0 | 5468.8 | 5533.1 | 64.3 |
| `irs-page30` | footprintMB | 3 | 548.0 | 544.0 | 548.0 | 4.0 |
| `irs-page30` | rssMB | 3 | 493.0 | 489.0 | 493.0 | 4.0 |
| `irs-page30` | committedMB | 3 | 80.2 | 78.5 | 80.9 | 2.4 |
| `irs-page30` | liveHeapMB | 3 | 102.7 | 102.5 | 102.7 | 0.2 |
| `irs-page30` | cpuTotalMs | 3 | 4813.7 | 4800.7 | 4851.7 | 51.0 |
| `irs-search-redact` | footprintMB | 3 | 470.0 | 469.0 | 470.0 | 1.0 |
| `irs-search-redact` | rssMB | 3 | 431.0 | 430.0 | 431.0 | 1.0 |
| `irs-search-redact` | committedMB | 3 | 79.8 | 79.7 | 79.8 | 0.0 |
| `irs-search-redact` | liveHeapMB | 3 | 105.4 | 105.3 | 105.6 | 0.2 |
| `irs-search-redact` | cpuTotalMs | 3 | 3290.3 | 3273.0 | 3305.6 | 32.6 |
| `irs-zoom` | footprintMB | 3 | 564.0 | 562.0 | 579.0 | 17.0 |
| `irs-zoom` | rssMB | 3 | 533.0 | 531.0 | 533.0 | 2.0 |
| `irs-zoom` | committedMB | 3 | 79.8 | 78.0 | 79.9 | 1.8 |
| `irs-zoom` | liveHeapMB | 3 | 105.8 | 105.6 | 109.5 | 3.9 |
| `irs-zoom` | cpuTotalMs | 3 | 3543.7 | 3540.4 | 3589.9 | 49.6 |
| `null` | footprintMB | 3 | 199.0 | 199.0 | 199.0 | 0.0 |
| `null` | rssMB | 3 | 247.0 | 247.0 | 247.0 | 0.0 |
| `null` | committedMB | 3 | 8.6 | 8.6 | 8.6 | 0.0 |
| `null` | liveHeapMB | 3 | 21.9 | 21.8 | 21.9 | 0.0 |
| `null` | cpuTotalMs | 3 | 753.3 | 747.1 | 753.4 | 6.4 |
| `soft-trim-cycle` | footprintMB | 3 | 867.0 | 845.0 | 912.0 | 67.0 |
| `soft-trim-cycle` | rssMB | 3 | 835.0 | 799.0 | 865.0 | 66.0 |
| `soft-trim-cycle` | committedMB | 3 | 526.0 | 490.0 | 556.7 | 66.8 |
| `soft-trim-cycle` | liveHeapMB | 3 | 421.8 | 419.7 | 425.4 | 5.8 |
| `soft-trim-cycle` | cpuTotalMs | 3 | 7621.1 | 7617.3 | 7633.8 | 16.5 |
| `w9-launch-idle` | footprintMB | 3 | 378.0 | 377.0 | 388.0 | 11.0 |
| `w9-launch-idle` | rssMB | 3 | 357.0 | 356.0 | 357.0 | 1.0 |
| `w9-launch-idle` | committedMB | 3 | 35.6 | 35.6 | 35.9 | 0.2 |
| `w9-launch-idle` | liveHeapMB | 3 | 47.0 | 47.0 | 47.1 | 0.0 |
| `w9-launch-idle` | cpuTotalMs | 3 | 1768.3 | 1733.3 | 1785.1 | 51.9 |

## The floor every delta is judged against

`floor = max(noise spread, runner overhead, sampler overhead)`

| metric | floor | unit | from |
|---|---:|---|---|
| footprintMB — physical footprint (outer; the #1461/#1496 number) | 67.0 | MB | noiseSpread |
| rssMB — resident set (outer) | 66.0 | MB | noiseSpread |
| committedMB — committed managed heap (in-app) | 66.8 | MB | noiseSpread |
| liveHeapMB — live managed heap (in-app) | 6.0 | MB | noiseSpread |
| cpuTotalMs — cumulative process CPU (in-app) | 200.4 | ms | noiseSpread |

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
| baseline | baseline | 0.0 | 198.0 | 242.0 | 8.6 | 21.6 | 2.4 | 0.0 | 0.0 | 0 | yes |
| pre-open | mark | 0.2 | 199.0 | 242.0 | 8.6 | 21.6 | 2.4 | 0.0 | 0.0 | 0 | yes |
| open-altona | open | 27.5 | 482.0 | 483.0 | 16.8 | 45.0 | 5.8 | 0.0 | 0.0 | 0 | yes |
| view-continuous | viewmode | 0.0 | 527.0 | 487.0 | 198.3 | 326.0 | 15.8 | 2.0 | 0.0 | 4 | yes |
| idle-after-open | waitidle | 2426.6 | 590.0 | 607.0 | 339.8 | 262.4 | 104.4 | 2.5 | 0.0 | 6 | yes |
| scroll-17 | scrollpages | 735.8 | 702.0 | 655.0 | 334.5 | 353.4 | 37.7 | 10.9 | 0.0 | 9 | yes |
| idle-after-scroll | waitidle | 1210.3 | 654.0 | 659.0 | 355.9 | 346.5 | 34.3 | 11.4 | 0.0 | 9 | yes |
| close-altona | close | 15.4 | 373.0 | 336.0 | 355.9 | 346.9 | 34.3 | 0.0 | 0.0 | 9 | yes |
| idle-after-close | waitidle | 484.5 | 351.0 | 337.0 | 31.9 | 24.0 | 8.9 | 0.0 | 0.0 | 10 | yes |
| idle-45s | idle | 45001.0 | 305.0 | 336.0 | 31.9 | 23.0 | 9.1 | 0.0 | 0.0 | 11 | yes |

Footprint: baseline 198.0 MB -> peak 702.0 MB -> final 305.0 MB (**107.0 MB above baseline at the end**).

Render/trim metrics (from the #1491 sink): bandRenderP50Ms=103.9, bandRenderP99Ms=642.7, bandRenders=16, cacheTrimRequests=2.0, heapReclaimP50Ms=11.0, heapReclaims=2, singleRenders=0

### `altona-replace-w9`

_#1481: replacing a document runs ReleasedMemoryReclaimer (aggressive, blocking, compacting) and dropped footprint to 421 MB in 18 ms by hand. This is that path, repeatable._

3 run(s), 0 step failure(s), 0 boundary(ies) with no outer sample.

| step | op | wall ms | footprint MB | rss MB | committed MB | live MB | frag MB | tiles MB | sp cache MB | gen2 | ok |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| baseline | baseline | 0.0 | 199.0 | 242.0 | 8.6 | 21.6 | 2.4 | 0.0 | 0.0 | 0 | yes |
| pre-open | mark | 0.1 | 199.0 | 242.0 | 8.6 | 21.6 | 2.4 | 0.0 | 0.0 | 0 | yes |
| open-altona | open | 29.8 | 453.0 | 435.0 | 16.8 | 45.0 | 5.8 | 0.0 | 0.0 | 0 | yes |
| view-continuous | viewmode | 0.0 | 490.0 | 478.0 | 234.0 | 231.4 | 13.2 | 2.0 | 0.0 | 5 | yes |
| idle-after-open | waitidle | 2180.3 | 553.0 | 596.0 | 327.5 | 248.2 | 105.1 | 2.5 | 0.0 | 7 | yes |
| scroll-17 | scrollpages | 742.9 | 705.0 | 659.0 | 363.2 | 345.8 | 37.1 | 10.9 | 0.0 | 10 | yes |
| idle-after-scroll | waitidle | 1090.1 | 634.0 | 662.0 | 363.2 | 346.8 | 37.1 | 11.4 | 0.0 | 10 | yes |
| replace-with-w9 | open | 43.3 | 446.0 | 395.0 | 35.9 | 28.6 | 8.6 | 0.0 | 0.0 | 11 | yes |
| idle-after-replace | waitidle | 1210.5 | 450.0 | 398.0 | 30.5 | 44.9 | 1.1 | 20.6 | 0.0 | 12 | yes |
| idle-45s | idle | 45000.6 | 371.0 | 398.0 | 30.5 | 37.0 | 2.3 | 10.3 | 0.0 | 13 | yes |

Footprint: baseline 199.0 MB -> peak 705.0 MB -> final 371.0 MB (**172.0 MB above baseline at the end**).

Render/trim metrics (from the #1491 sink): bandRenderP50Ms=80.6, bandRenderP99Ms=622.9, bandRenders=17, cacheTrimRequests=2.0, heapReclaimP50Ms=13.0, heapReclaims=2, singleRenders=0

### `altona-scroll`

_excise's worst measured metric against a native viewer: #1461 recorded 1440-2432 MB RSS after a full scroll against Preview's 446 MB on the same file. 122 MB, 17 pages, image-heavy._

3 run(s), 0 step failure(s), 0 boundary(ies) with no outer sample.

| step | op | wall ms | footprint MB | rss MB | committed MB | live MB | frag MB | tiles MB | sp cache MB | gen2 | ok |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| baseline | baseline | 0.0 | 198.0 | 242.0 | 8.6 | 21.5 | 2.4 | 0.0 | 0.0 | 0 | yes |
| pre-open | mark | 0.1 | 199.0 | 242.0 | 8.6 | 21.6 | 2.4 | 0.0 | 0.0 | 0 | yes |
| open-altona | open | 30.7 | 456.0 | 438.0 | 16.8 | 45.0 | 5.8 | 0.0 | 0.0 | 0 | yes |
| view-continuous | viewmode | 0.0 | 494.0 | 482.0 | 239.6 | 231.4 | 18.8 | 2.0 | 0.0 | 5 | yes |
| idle-after-open | waitidle | 2180.9 | 561.0 | 601.0 | 334.0 | 262.6 | 98.7 | 2.5 | 0.0 | 6 | yes |
| scroll-17 | scrollpages | 732.5 | 711.0 | 664.0 | 365.9 | 346.7 | 45.4 | 11.4 | 0.0 | 9 | yes |
| idle-after-scroll | waitidle | 968.9 | 643.0 | 668.0 | 365.9 | 346.7 | 45.4 | 11.4 | 0.0 | 9 | yes |
| idle-45s | idle | 45000.7 | 425.0 | 456.0 | 154.5 | 154.8 | 2.9 | 4.4 | 0.0 | 10 | yes |

Footprint: baseline 198.0 MB -> peak 711.0 MB -> final 425.0 MB (**227.0 MB above baseline at the end**).

Render/trim metrics (from the #1491 sink): bandRenderP50Ms=101.3, bandRenderP99Ms=601.0, bandRenders=16, cacheTrimRequests=2.0, heapReclaims=1, singleRenders=0

### `irs-page30`

_#1469: ~650-710 MB after opening a 4 MB text-only document with zero image XObjects. Paging 30 pages is the same interaction the 09-11..09-14 hand-driven readings used (1104 -> 838 -> 720 MB footprint), so this scenario is what makes those comparable._

3 run(s), 0 step failure(s), 0 boundary(ies) with no outer sample.

| step | op | wall ms | footprint MB | rss MB | committed MB | live MB | frag MB | tiles MB | sp cache MB | gen2 | ok |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| baseline | baseline | 0.0 | 199.0 | 242.0 | 8.6 | 21.6 | 2.4 | 0.0 | 0.0 | 0 | yes |
| pre-open | mark | 0.1 | 199.0 | 242.0 | 8.6 | 21.6 | 2.4 | 0.0 | 0.0 | 0 | yes |
| open-irs | open | 107.5 | 401.0 | 362.0 | 47.4 | 47.2 | 10.3 | 0.0 | 0.0 | 3 | yes |
| view-single | viewmode | 1.9 | 451.0 | 397.0 | 49.4 | 61.2 | 11.3 | 20.6 | 0.0 | 5 | yes |
| idle-after-open | waitidle | 2665.1 | 414.0 | 434.0 | 80.9 | 102.7 | 4.2 | 20.6 | 20.6 | 10 | yes |
| page-30 | pageby | 3653.2 | 548.0 | 493.0 | 77.7 | 93.0 | 17.2 | 20.6 | 61.8 | 24 | yes |
| idle-after-paging | waitidle | 484.6 | 548.0 | 493.0 | 77.7 | 93.0 | 17.2 | 20.6 | 61.8 | 24 | yes |
| idle-20s | idle | 20001.4 | 475.0 | 493.0 | 77.7 | 93.3 | 17.2 | 20.6 | 61.8 | 24 | yes |

Footprint: baseline 199.0 MB -> peak 548.0 MB -> final 475.0 MB (**276.0 MB above baseline at the end**).

Render/trim metrics (from the #1491 sink): bandRenders=1, cacheTrimRequests=1.0, heapReclaims=0, singleRenders=1

### `irs-search-redact`

_The redaction path's live memory cost, which no headless harness measures with a real compositor attached. Deliberately does NOT close afterwards: a redacted document is dirty, and closing it would wait on the unsaved-changes dialog._

3 run(s), 0 step failure(s), 0 boundary(ies) with no outer sample.

| step | op | wall ms | footprint MB | rss MB | committed MB | live MB | frag MB | tiles MB | sp cache MB | gen2 | ok |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| baseline | baseline | 0.0 | 198.0 | 242.0 | 8.6 | 21.6 | 2.4 | 0.0 | 0.0 | 0 | yes |
| pre-open | mark | 0.1 | 199.0 | 242.0 | 8.6 | 21.6 | 2.4 | 0.0 | 0.0 | 0 | yes |
| open-irs | open | 108.2 | 398.0 | 360.0 | 47.7 | 47.2 | 10.3 | 0.0 | 0.0 | 3 | yes |
| view-continuous | viewmode | 0.0 | 397.0 | 359.0 | 49.4 | 62.9 | 11.0 | 20.6 | 0.0 | 5 | yes |
| idle-after-open | waitidle | 2301.1 | 444.0 | 404.0 | 78.7 | 105.3 | 9.4 | 20.6 | 0.0 | 10 | yes |
| search-income | search | 121.3 | 466.0 | 427.0 | 79.7 | 97.1 | 9.3 | 30.9 | 0.0 | 10 | yes |
| idle-after-search | waitidle | 727.4 | 411.0 | 430.0 | 79.7 | 97.2 | 9.3 | 30.9 | 0.0 | 10 | yes |
| redact-income | redacttext | 1.7 | 469.0 | 430.0 | 79.7 | 97.3 | 9.3 | 30.9 | 0.0 | 10 | yes |
| idle-after-redact | waitidle | 482.7 | 469.0 | 430.0 | 79.7 | 97.4 | 9.3 | 30.9 | 0.0 | 10 | yes |
| idle-20s | idle | 20001.2 | 391.0 | 430.0 | 79.7 | 97.7 | 9.3 | 30.9 | 0.0 | 10 | yes |

Footprint: baseline 198.0 MB -> peak 469.0 MB -> final 391.0 MB (**193.0 MB above baseline at the end**).

Render/trim metrics (from the #1491 sink): bandRenders=1, cacheTrimRequests=2.0, heapReclaims=0, singleRenders=0

### `irs-zoom`

_Zoom changes the render DPI, so it is the axis that multiplies tile bytes without touching page count. 25/100/500% brackets the range a reader actually uses._

3 run(s), 0 step failure(s), 0 boundary(ies) with no outer sample.

| step | op | wall ms | footprint MB | rss MB | committed MB | live MB | frag MB | tiles MB | sp cache MB | gen2 | ok |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| baseline | baseline | 0.0 | 198.0 | 241.0 | 8.6 | 21.6 | 2.4 | 0.0 | 0.0 | 0 | yes |
| pre-open | mark | 0.1 | 199.0 | 242.0 | 8.6 | 21.6 | 2.4 | 0.0 | 0.0 | 0 | yes |
| open-irs | open | 107.7 | 401.0 | 362.0 | 47.7 | 47.7 | 10.3 | 0.0 | 0.0 | 3 | yes |
| view-continuous | viewmode | 0.0 | 396.0 | 358.0 | 48.7 | 62.7 | 11.1 | 20.6 | 0.0 | 5 | yes |
| idle-after-open | waitidle | 2421.4 | 400.0 | 403.0 | 77.3 | 105.5 | 8.4 | 20.6 | 0.0 | 10 | yes |
| zoom-25 | zoom | 0.3 | 437.0 | 407.0 | 77.3 | 105.6 | 8.4 | 20.6 | 0.0 | 10 | yes |
| idle-zoom-25 | waitidle | 605.6 | 438.0 | 407.0 | 78.0 | 101.5 | 6.9 | 23.9 | 0.0 | 10 | yes |
| zoom-100 | zoom | 0.3 | 500.0 | 457.0 | 78.0 | 101.6 | 6.9 | 23.9 | 0.0 | 10 | yes |
| idle-zoom-100 | waitidle | 484.8 | 501.0 | 458.0 | 78.0 | 94.4 | 13.9 | 49.4 | 0.0 | 11 | yes |
| zoom-500 | zoom | 0.3 | 561.0 | 530.0 | 78.0 | 94.5 | 13.9 | 49.4 | 0.0 | 11 | yes |
| idle-zoom-500 | waitidle | 484.3 | 562.0 | 530.0 | 77.5 | 93.7 | 8.7 | 57.4 | 0.0 | 11 | yes |
| idle-10s | idle | 10001.3 | 483.0 | 531.0 | 77.5 | 93.9 | 8.7 | 57.4 | 0.0 | 11 | yes |

Footprint: baseline 198.0 MB -> peak 562.0 MB -> final 483.0 MB (**285.0 MB above baseline at the end**).

Render/trim metrics (from the #1491 sink): bandRenderP50Ms=15.1, bandRenderP99Ms=24.4, bandRenders=6, cacheTrimRequests=1.0, heapReclaims=0, singleRenders=0

### `null`

_CALIBRATION BASELINE. Launch, settle, idle, quit, with no document ever opened. Subtracted from every other scenario, and compared against a plain launch with the runner switched off to price the runner itself._

3 run(s), 0 step failure(s), 0 boundary(ies) with no outer sample.

| step | op | wall ms | footprint MB | rss MB | committed MB | live MB | frag MB | tiles MB | sp cache MB | gen2 | ok |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| baseline | baseline | 0.0 | 199.0 | 242.0 | 8.6 | 21.5 | 2.4 | 0.0 | 0.0 | 0 | yes |
| launched | mark | 0.1 | 199.0 | 242.0 | 8.6 | 21.6 | 2.4 | 0.0 | 0.0 | 0 | yes |
| idle-after-launch | waitidle | 729.5 | 197.0 | 242.0 | 8.6 | 21.7 | 2.4 | 0.0 | 0.0 | 0 | yes |
| idle-10s | idle | 10001.6 | 197.0 | 247.0 | 8.6 | 21.8 | 2.4 | 0.0 | 0.0 | 0 | yes |

Footprint: baseline 199.0 MB -> peak 199.0 MB -> final 197.0 MB (**-2.0 MB above baseline at the end**).

Render/trim metrics (from the #1491 sink): bandRenders=0, cacheTrimRequests=1.0, heapReclaims=0, singleRenders=0

### `soft-trim-cycle`

_#1478 and #1496 together: trim at Warn then Critical on a scrolled Altona, then scroll back. The trim is requested IN PROCESS through ViewerCacheTrimCoordinator, which is what removes `sudo memory_pressure` (and therefore the human) from the loop. The scroll-back step is there because #1496's own risk list names re-inflation churn: freed samples decode again._

3 run(s), 0 step failure(s), 0 boundary(ies) with no outer sample.

| step | op | wall ms | footprint MB | rss MB | committed MB | live MB | frag MB | tiles MB | sp cache MB | gen2 | ok |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| baseline | baseline | 0.0 | 198.0 | 242.0 | 8.6 | 21.5 | 2.4 | 0.0 | 0.0 | 0 | yes |
| pre-open | mark | 0.1 | 199.0 | 242.0 | 8.6 | 21.6 | 2.4 | 0.0 | 0.0 | 0 | yes |
| open-altona | open | 28.7 | 479.0 | 465.0 | 16.8 | 45.0 | 5.8 | 0.0 | 0.0 | 0 | yes |
| view-continuous | viewmode | 0.0 | 527.0 | 481.0 | 234.0 | 231.3 | 13.2 | 2.0 | 0.0 | 5 | yes |
| idle-after-open | waitidle | 2313.4 | 567.0 | 596.0 | 326.3 | 248.1 | 104.4 | 2.5 | 0.0 | 7 | yes |
| scroll-17 | scrollpages | 730.0 | 704.0 | 658.0 | 336.1 | 326.4 | 40.8 | 10.9 | 0.0 | 10 | yes |
| idle-after-scroll | waitidle | 1089.9 | 643.0 | 661.0 | 358.0 | 346.8 | 37.4 | 11.4 | 0.0 | 10 | yes |
| trim-background | trim | 0.5 | 439.0 | 457.0 | 358.0 | 346.9 | 37.4 | 4.4 | 0.0 | 10 | yes |
| idle-after-background-trim | waitidle | 484.1 | 439.0 | 457.0 | 151.5 | 155.7 | 2.8 | 4.4 | 0.0 | 11 | yes |
| trim-warn | trim | 0.3 | 439.0 | 457.0 | 151.5 | 155.8 | 2.8 | 4.4 | 0.0 | 11 | yes |
| idle-after-warn-trim | waitidle | 484.6 | 439.0 | 457.0 | 151.5 | 155.7 | 2.8 | 4.4 | 0.0 | 12 | yes |
| trim-critical | trim | 0.2 | 440.0 | 457.0 | 151.5 | 155.8 | 2.8 | 0.0 | 0.0 | 12 | yes |
| idle-after-critical-trim | waitidle | 483.8 | 440.0 | 457.0 | 151.5 | 155.7 | 2.8 | 0.0 | 0.0 | 13 | yes |
| scroll-back-17 | scrollpages | 733.0 | 844.0 | 798.0 | 490.0 | 421.4 | 115.2 | 7.5 | 0.0 | 16 | yes |
| idle-after-scroll-back | waitidle | 484.6 | 845.0 | 799.0 | 490.0 | 421.5 | 115.2 | 7.5 | 0.0 | 16 | yes |
| idle-20s | idle | 20001.9 | 779.0 | 798.0 | 490.0 | 421.8 | 115.2 | 7.5 | 0.0 | 16 | yes |

Footprint: baseline 198.0 MB -> peak 845.0 MB -> final 779.0 MB (**581.0 MB above baseline at the end**).

Render/trim metrics (from the #1491 sink): bandRenderP50Ms=103.2, bandRenderP99Ms=644.6, bandRenders=31, cacheTrimRequests=4.0, heapReclaimP50Ms=6.0, heapReclaims=3, singleRenders=0

### `w9-launch-idle`

_The fixed floor: a one-page form with nothing heavy in it. #1496 measured 336 MB footprint / 120 MB committed heap here, and every other number in the family is read against it._

3 run(s), 0 step failure(s), 0 boundary(ies) with no outer sample.

| step | op | wall ms | footprint MB | rss MB | committed MB | live MB | frag MB | tiles MB | sp cache MB | gen2 | ok |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| baseline | baseline | 0.0 | 198.0 | 242.0 | 8.6 | 21.5 | 2.5 | 0.0 | 0.0 | 0 | yes |
| pre-open | mark | 0.1 | 199.0 | 242.0 | 8.6 | 21.5 | 2.5 | 0.0 | 0.0 | 0 | yes |
| open-w9 | open | 55.6 | 376.0 | 338.0 | 25.9 | 31.6 | 5.5 | 0.0 | 0.0 | 0 | yes |
| view-continuous | viewmode | 0.0 | 377.0 | 340.0 | 35.6 | 44.5 | 8.8 | 20.6 | 0.0 | 2 | yes |
| idle-after-open | waitidle | 1697.1 | 334.0 | 356.0 | 35.9 | 46.8 | 1.5 | 20.6 | 0.0 | 2 | yes |
| idle-20s | idle | 20001.2 | 323.0 | 356.0 | 35.9 | 47.1 | 1.5 | 20.6 | 0.0 | 2 | yes |

Footprint: baseline 198.0 MB -> peak 377.0 MB -> final 323.0 MB (**125.0 MB above baseline at the end**).

Render/trim metrics (from the #1491 sink): bandRenders=1, cacheTrimRequests=1.0, heapReclaims=0, singleRenders=0

