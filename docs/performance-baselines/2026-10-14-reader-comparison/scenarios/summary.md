# Live GUI performance scenarios (#1497)

```
sha=dcd0a2e9
tree=dirty
load1=1.92
sample=boundary
vmmap=0
repeats=3
set=default
scenarios=null w9-launch-idle irs-page30 irs-zoom altona-scroll altona-close altona-replace-w9 soft-trim-cycle irs-search-redact
systemTabbing=fullscreen
appHome=/private/tmp/excise-gui-perf/run
exe=/Users/marc/Documents/GitHub/pdfe/logs/reader-bench-bundle/excise.app/Contents/MacOS/Excise.App
started=2026-10-14T17:11:48Z
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
| `altona-close` | footprintMB | 3 | 735.0 | 726.0 | 739.0 | 13.0 |
| `altona-close` | rssMB | 3 | 693.0 | 684.0 | 693.0 | 9.0 |
| `altona-close` | committedMB | 3 | 359.6 | 349.9 | 359.7 | 9.8 |
| `altona-close` | liveHeapMB | 3 | 352.5 | 352.1 | 352.5 | 0.4 |
| `altona-close` | cpuTotalMs | 3 | 7486.0 | 7405.2 | 7495.4 | 90.1 |
| `altona-replace-w9` | footprintMB | 3 | 721.0 | 705.0 | 724.0 | 19.0 |
| `altona-replace-w9` | rssMB | 3 | 680.0 | 678.0 | 682.0 | 4.0 |
| `altona-replace-w9` | committedMB | 3 | 347.2 | 345.4 | 350.1 | 4.7 |
| `altona-replace-w9` | liveHeapMB | 3 | 351.9 | 335.1 | 353.8 | 18.8 |
| `altona-replace-w9` | cpuTotalMs | 3 | 8138.9 | 8094.8 | 8291.3 | 196.5 |
| `altona-scroll` | footprintMB | 3 | 737.0 | 729.0 | 746.0 | 17.0 |
| `altona-scroll` | rssMB | 3 | 704.0 | 703.0 | 710.0 | 7.0 |
| `altona-scroll` | committedMB | 3 | 370.1 | 368.8 | 375.0 | 6.2 |
| `altona-scroll` | liveHeapMB | 3 | 352.0 | 351.8 | 352.3 | 0.5 |
| `altona-scroll` | cpuTotalMs | 3 | 7191.7 | 7190.0 | 7246.6 | 56.6 |
| `irs-page30` | footprintMB | 3 | 552.0 | 536.0 | 554.0 | 18.0 |
| `irs-page30` | rssMB | 3 | 499.0 | 497.0 | 500.0 | 3.0 |
| `irs-page30` | committedMB | 3 | 78.3 | 77.4 | 80.2 | 2.8 |
| `irs-page30` | liveHeapMB | 3 | 109.5 | 97.7 | 110.6 | 13.0 |
| `irs-page30` | cpuTotalMs | 3 | 4842.8 | 4787.3 | 4857.5 | 70.2 |
| `irs-search-redact` | footprintMB | 3 | 469.0 | 468.0 | 484.0 | 16.0 |
| `irs-search-redact` | rssMB | 3 | 429.0 | 428.0 | 430.0 | 2.0 |
| `irs-search-redact` | committedMB | 3 | 80.1 | 78.8 | 80.1 | 1.3 |
| `irs-search-redact` | liveHeapMB | 3 | 108.1 | 108.1 | 110.6 | 2.5 |
| `irs-search-redact` | cpuTotalMs | 3 | 3322.2 | 3294.4 | 3329.9 | 35.4 |
| `irs-zoom` | footprintMB | 3 | 563.0 | 561.0 | 565.0 | 4.0 |
| `irs-zoom` | rssMB | 3 | 532.0 | 530.0 | 534.0 | 4.0 |
| `irs-zoom` | committedMB | 3 | 77.9 | 77.0 | 80.6 | 3.6 |
| `irs-zoom` | liveHeapMB | 3 | 107.7 | 105.1 | 112.6 | 7.6 |
| `irs-zoom` | cpuTotalMs | 3 | 3528.0 | 3506.5 | 3540.0 | 33.5 |
| `null` | footprintMB | 3 | 199.0 | 199.0 | 217.0 | 18.0 |
| `null` | rssMB | 3 | 247.0 | 247.0 | 248.0 | 1.0 |
| `null` | committedMB | 3 | 8.6 | 8.6 | 16.8 | 8.2 |
| `null` | liveHeapMB | 3 | 21.9 | 21.8 | 26.5 | 4.7 |
| `null` | cpuTotalMs | 3 | 828.0 | 764.3 | 940.4 | 176.1 |
| `soft-trim-cycle` | footprintMB | 3 | 812.0 | 789.0 | 832.0 | 43.0 |
| `soft-trim-cycle` | rssMB | 3 | 766.0 | 743.0 | 789.0 | 46.0 |
| `soft-trim-cycle` | committedMB | 3 | 424.2 | 423.8 | 468.8 | 45.0 |
| `soft-trim-cycle` | liveHeapMB | 3 | 432.8 | 426.1 | 434.4 | 8.3 |
| `soft-trim-cycle` | cpuTotalMs | 3 | 9317.9 | 9167.0 | 9535.7 | 368.7 |
| `w9-launch-idle` | footprintMB | 3 | 383.0 | 378.0 | 384.0 | 6.0 |
| `w9-launch-idle` | rssMB | 3 | 357.0 | 357.0 | 360.0 | 3.0 |
| `w9-launch-idle` | committedMB | 3 | 34.6 | 34.6 | 34.6 | 0.0 |
| `w9-launch-idle` | liveHeapMB | 3 | 51.4 | 51.3 | 52.0 | 0.7 |
| `w9-launch-idle` | cpuTotalMs | 3 | 1876.9 | 1862.0 | 2160.2 | 298.2 |

## The floor every delta is judged against

`floor = max(noise spread, runner overhead, sampler overhead)`

| metric | floor | unit | from |
|---|---:|---|---|
| footprintMB — physical footprint (outer; the #1461/#1496 number) | 43.0 | MB | noiseSpread |
| rssMB — resident set (outer) | 46.0 | MB | noiseSpread |
| committedMB — committed managed heap (in-app) | 45.0 | MB | noiseSpread |
| liveHeapMB — live managed heap (in-app) | 18.8 | MB | noiseSpread |
| cpuTotalMs — cumulative process CPU (in-app) | 368.7 | ms | noiseSpread |

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
| baseline | baseline | 0.0 | 199.0 | 242.0 | 8.6 | 21.5 | 2.4 | 0.0 | 0.0 | 0 | yes |
| pre-open | mark | 0.1 | 199.0 | 242.0 | 8.6 | 21.6 | 2.4 | 0.0 | 0.0 | 0 | yes |
| open-altona | open | 30.9 | 333.0 | 327.0 | 16.8 | 46.7 | 5.8 | 0.0 | 0.0 | 0 | yes |
| view-continuous | viewmode | 0.0 | 469.0 | 443.0 | 171.5 | 193.5 | 24.9 | 1.0 | 0.0 | 4 | yes |
| idle-after-open | waitidle | 2548.4 | 591.0 | 613.0 | 336.3 | 261.4 | 100.3 | 2.5 | 0.0 | 6 | yes |
| scroll-17 | scrollpages | 743.0 | 726.0 | 679.0 | 349.9 | 323.3 | 53.8 | 10.9 | 0.0 | 9 | yes |
| idle-after-scroll | waitidle | 1089.8 | 690.0 | 684.0 | 349.9 | 352.1 | 53.8 | 11.4 | 0.0 | 9 | yes |
| close-altona | close | 16.3 | 386.0 | 346.0 | 349.9 | 352.5 | 53.8 | 0.0 | 0.0 | 9 | yes |
| idle-after-close | waitidle | 726.8 | 361.0 | 347.0 | 33.2 | 23.5 | 10.6 | 0.0 | 0.0 | 10 | yes |
| idle-45s | idle | 45001.1 | 314.0 | 347.0 | 33.1 | 23.0 | 10.9 | 0.0 | 0.0 | 11 | yes |

Footprint: baseline 199.0 MB -> peak 726.0 MB -> final 314.0 MB (**115.0 MB above baseline at the end**).

Render/trim metrics (from the #1491 sink): bandRenderP50Ms=207.6, bandRenderP99Ms=658.4, bandRenders=16, cacheTrimRequests=2.0, heapReclaimP50Ms=13.0, heapReclaims=2, singleRenders=0

### `altona-replace-w9`

_#1481: replacing a document runs ReleasedMemoryReclaimer (aggressive, blocking, compacting) and dropped footprint to 421 MB in 18 ms by hand. This is that path, repeatable._

3 run(s), 0 step failure(s), 0 boundary(ies) with no outer sample.

| step | op | wall ms | footprint MB | rss MB | committed MB | live MB | frag MB | tiles MB | sp cache MB | gen2 | ok |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| baseline | baseline | 0.0 | 199.0 | 242.0 | 8.6 | 21.5 | 2.4 | 0.0 | 0.0 | 0 | yes |
| pre-open | mark | 0.1 | 199.0 | 242.0 | 8.6 | 21.6 | 2.4 | 0.0 | 0.0 | 0 | yes |
| open-altona | open | 32.4 | 325.0 | 298.0 | 16.8 | 46.7 | 5.8 | 0.0 | 0.0 | 0 | yes |
| view-continuous | viewmode | 0.0 | 540.0 | 517.0 | 83.3 | 77.4 | 18.4 | 0.5 | 0.0 | 3 | yes |
| idle-after-open | waitidle | 2428.1 | 477.0 | 524.0 | 248.6 | 235.1 | 31.1 | 2.5 | 0.0 | 6 | yes |
| scroll-17 | scrollpages | 743.5 | 721.0 | 675.0 | 345.4 | 353.7 | 49.4 | 11.4 | 0.0 | 9 | yes |
| idle-after-scroll | waitidle | 1090.3 | 659.0 | 680.0 | 345.4 | 353.8 | 49.4 | 11.4 | 0.0 | 9 | yes |
| replace-with-w9 | open | 64.2 | 452.0 | 401.0 | 37.6 | 36.4 | 5.0 | 0.0 | 0.0 | 10 | yes |
| idle-after-replace | waitidle | 1452.9 | 454.0 | 402.0 | 33.1 | 51.0 | 0.0 | 20.6 | 0.0 | 11 | yes |
| idle-45s | idle | 45001.1 | 374.0 | 401.0 | 31.9 | 37.0 | 4.0 | 10.3 | 0.0 | 12 | yes |

Footprint: baseline 199.0 MB -> peak 721.0 MB -> final 374.0 MB (**175.0 MB above baseline at the end**).

Render/trim metrics (from the #1491 sink): bandRenderP50Ms=135.8, bandRenderP99Ms=616.6, bandRenders=17, cacheTrimRequests=2.0, heapReclaimP50Ms=14.0, heapReclaims=2, singleRenders=0

### `altona-scroll`

_excise's worst measured metric against a native viewer: #1461 recorded 1440-2432 MB RSS after a full scroll against Preview's 446 MB on the same file. 122 MB, 17 pages, image-heavy._

3 run(s), 0 step failure(s), 0 boundary(ies) with no outer sample.

| step | op | wall ms | footprint MB | rss MB | committed MB | live MB | frag MB | tiles MB | sp cache MB | gen2 | ok |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| baseline | baseline | 0.0 | 199.0 | 242.0 | 8.6 | 21.6 | 2.4 | 0.0 | 0.0 | 0 | yes |
| pre-open | mark | 0.1 | 199.0 | 242.0 | 8.6 | 21.6 | 2.4 | 0.0 | 0.0 | 0 | yes |
| open-altona | open | 43.2 | 325.0 | 299.0 | 16.8 | 46.8 | 5.8 | 0.0 | 0.0 | 0 | yes |
| view-continuous | viewmode | 0.0 | 447.0 | 424.0 | 168.9 | 190.6 | 21.2 | 1.0 | 0.0 | 4 | yes |
| idle-after-open | waitidle | 2547.4 | 577.0 | 608.0 | 333.1 | 236.0 | 113.7 | 2.5 | 0.0 | 6 | yes |
| scroll-17 | scrollpages | 742.2 | 729.0 | 698.0 | 368.8 | 350.9 | 73.8 | 10.9 | 0.0 | 9 | yes |
| idle-after-scroll | waitidle | 1091.2 | 689.0 | 703.0 | 368.8 | 352.0 | 73.8 | 11.4 | 0.0 | 9 | yes |
| idle-45s | idle | 45001.4 | 418.0 | 466.0 | 153.4 | 154.7 | 4.7 | 4.4 | 0.0 | 10 | yes |

Footprint: baseline 199.0 MB -> peak 729.0 MB -> final 418.0 MB (**219.0 MB above baseline at the end**).

Render/trim metrics (from the #1491 sink): bandRenderP50Ms=212.7, bandRenderP99Ms=622.9, bandRenders=16, cacheTrimRequests=2.0, heapReclaims=1, singleRenders=0

### `irs-page30`

_#1469: ~650-710 MB after opening a 4 MB text-only document with zero image XObjects. Paging 30 pages is the same interaction the 09-11..09-14 hand-driven readings used (1104 -> 838 -> 720 MB footprint), so this scenario is what makes those comparable._

3 run(s), 0 step failure(s), 0 boundary(ies) with no outer sample.

| step | op | wall ms | footprint MB | rss MB | committed MB | live MB | frag MB | tiles MB | sp cache MB | gen2 | ok |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| baseline | baseline | 0.0 | 199.0 | 242.0 | 8.6 | 21.6 | 2.4 | 0.0 | 0.0 | 0 | yes |
| pre-open | mark | 0.1 | 199.0 | 242.0 | 8.6 | 21.6 | 2.4 | 0.0 | 0.0 | 0 | yes |
| open-irs | open | 102.6 | 400.0 | 363.0 | 47.3 | 55.8 | 5.9 | 0.0 | 0.0 | 2 | yes |
| view-single | viewmode | 2.0 | 449.0 | 398.0 | 52.1 | 61.4 | 9.2 | 20.6 | 0.0 | 5 | yes |
| idle-after-open | waitidle | 2787.2 | 407.0 | 441.0 | 78.3 | 97.7 | 10.5 | 20.6 | 20.6 | 10 | yes |
| page-30 | pageby | 3653.1 | 536.0 | 497.0 | 74.5 | 91.2 | 19.1 | 20.6 | 61.8 | 23 | yes |
| idle-after-paging | waitidle | 485.1 | 536.0 | 497.0 | 74.5 | 91.3 | 19.1 | 20.6 | 61.8 | 23 | yes |
| idle-20s | idle | 20001.3 | 458.0 | 497.0 | 74.5 | 91.6 | 19.1 | 20.6 | 61.8 | 23 | yes |

Footprint: baseline 199.0 MB -> peak 536.0 MB -> final 458.0 MB (**259.0 MB above baseline at the end**).

Render/trim metrics (from the #1491 sink): bandRenders=1, cacheTrimRequests=1.0, heapReclaims=0, singleRenders=1

### `irs-search-redact`

_The redaction path's live memory cost, which no headless harness measures with a real compositor attached. Deliberately does NOT close afterwards: a redacted document is dirty, and closing it would wait on the unsaved-changes dialog._

3 run(s), 0 step failure(s), 0 boundary(ies) with no outer sample.

| step | op | wall ms | footprint MB | rss MB | committed MB | live MB | frag MB | tiles MB | sp cache MB | gen2 | ok |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| baseline | baseline | 0.0 | 199.0 | 242.0 | 8.6 | 21.6 | 2.4 | 0.0 | 0.0 | 0 | yes |
| pre-open | mark | 0.1 | 199.0 | 242.0 | 8.6 | 21.6 | 2.4 | 0.0 | 0.0 | 0 | yes |
| open-irs | open | 100.9 | 404.0 | 354.0 | 47.1 | 55.7 | 5.9 | 0.0 | 0.0 | 2 | yes |
| view-continuous | viewmode | 0.0 | 414.0 | 361.0 | 52.1 | 61.9 | 9.1 | 20.6 | 0.0 | 5 | yes |
| idle-after-open | waitidle | 2544.9 | 399.0 | 405.0 | 80.1 | 104.3 | 5.9 | 20.6 | 0.0 | 9 | yes |
| search-income | search | 121.2 | 481.0 | 427.0 | 80.1 | 107.5 | 5.9 | 30.9 | 0.0 | 9 | yes |
| idle-after-search | waitidle | 727.0 | 484.0 | 430.0 | 80.1 | 107.6 | 5.9 | 30.9 | 0.0 | 9 | yes |
| redact-income | redacttext | 1.4 | 484.0 | 429.0 | 80.1 | 107.7 | 5.9 | 30.9 | 0.0 | 9 | yes |
| idle-after-redact | waitidle | 484.7 | 484.0 | 429.0 | 80.1 | 107.8 | 5.9 | 30.9 | 0.0 | 9 | yes |
| idle-20s | idle | 20000.5 | 411.0 | 429.0 | 80.1 | 108.1 | 5.9 | 30.9 | 0.0 | 9 | yes |

Footprint: baseline 199.0 MB -> peak 484.0 MB -> final 411.0 MB (**212.0 MB above baseline at the end**).

Render/trim metrics (from the #1491 sink): bandRenders=1, cacheTrimRequests=2.0, heapReclaims=0, singleRenders=0

### `irs-zoom`

_Zoom changes the render DPI, so it is the axis that multiplies tile bytes without touching page count. 25/100/500% brackets the range a reader actually uses._

3 run(s), 0 step failure(s), 0 boundary(ies) with no outer sample.

| step | op | wall ms | footprint MB | rss MB | committed MB | live MB | frag MB | tiles MB | sp cache MB | gen2 | ok |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| baseline | baseline | 0.0 | 199.0 | 242.0 | 8.6 | 21.5 | 2.4 | 0.0 | 0.0 | 0 | yes |
| pre-open | mark | 0.2 | 199.0 | 242.0 | 8.6 | 21.6 | 2.4 | 0.0 | 0.0 | 0 | yes |
| open-irs | open | 97.5 | 404.0 | 367.0 | 47.4 | 55.7 | 5.9 | 0.0 | 0.0 | 2 | yes |
| view-continuous | viewmode | 0.0 | 400.0 | 362.0 | 51.9 | 58.9 | 9.2 | 20.6 | 0.0 | 5 | yes |
| idle-after-open | waitidle | 1696.2 | 442.0 | 402.0 | 77.9 | 96.7 | 9.5 | 20.6 | 0.0 | 10 | yes |
| zoom-25 | zoom | 0.3 | 435.0 | 405.0 | 77.9 | 96.8 | 9.5 | 20.6 | 0.0 | 10 | yes |
| idle-zoom-25 | waitidle | 1090.2 | 372.0 | 408.0 | 77.9 | 104.9 | 9.5 | 23.9 | 0.0 | 10 | yes |
| zoom-100 | zoom | 0.3 | 501.0 | 458.0 | 77.9 | 105.1 | 9.5 | 23.9 | 0.0 | 10 | yes |
| idle-zoom-100 | waitidle | 725.3 | 502.0 | 459.0 | 77.9 | 93.8 | 17.5 | 49.4 | 0.0 | 11 | yes |
| zoom-500 | zoom | 0.3 | 563.0 | 532.0 | 77.9 | 93.9 | 17.5 | 49.4 | 0.0 | 11 | yes |
| idle-zoom-500 | waitidle | 483.9 | 563.0 | 532.0 | 77.9 | 96.3 | 9.9 | 57.4 | 0.0 | 11 | yes |
| idle-10s | idle | 10000.8 | 491.0 | 532.0 | 77.9 | 96.5 | 9.9 | 57.4 | 0.0 | 11 | yes |

Footprint: baseline 199.0 MB -> peak 563.0 MB -> final 491.0 MB (**292.0 MB above baseline at the end**).

Render/trim metrics (from the #1491 sink): bandRenderP50Ms=15.8, bandRenderP99Ms=25.8, bandRenders=6, cacheTrimRequests=1.0, heapReclaims=0, singleRenders=0

### `null`

_CALIBRATION BASELINE. Launch, settle, idle, quit, with no document ever opened. Subtracted from every other scenario, and compared against a plain launch with the runner switched off to price the runner itself._

3 run(s), 0 step failure(s), 0 boundary(ies) with no outer sample.

| step | op | wall ms | footprint MB | rss MB | committed MB | live MB | frag MB | tiles MB | sp cache MB | gen2 | ok |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| baseline | baseline | 0.0 | 198.0 | 241.0 | 8.6 | 21.5 | 2.5 | 0.0 | 0.0 | 0 | yes |
| launched | mark | 0.3 | 199.0 | 242.0 | 8.6 | 21.5 | 2.5 | 0.0 | 0.0 | 0 | yes |
| idle-after-launch | waitidle | 727.7 | 197.0 | 243.0 | 8.6 | 21.6 | 2.5 | 0.0 | 0.0 | 0 | yes |
| idle-10s | idle | 10001.2 | 197.0 | 247.0 | 8.6 | 21.8 | 2.5 | 0.0 | 0.0 | 0 | yes |

Footprint: baseline 198.0 MB -> peak 199.0 MB -> final 197.0 MB (**-1.0 MB above baseline at the end**).

Render/trim metrics (from the #1491 sink): bandRenders=0, cacheTrimRequests=1.0, heapReclaims=0, singleRenders=0

### `soft-trim-cycle`

_#1478 and #1496 together: trim at Warn then Critical on a scrolled Altona, then scroll back. The trim is requested IN PROCESS through ViewerCacheTrimCoordinator, which is what removes `sudo memory_pressure` (and therefore the human) from the loop. The scroll-back step is there because #1496's own risk list names re-inflation churn: freed samples decode again._

3 run(s), 0 step failure(s), 0 boundary(ies) with no outer sample.

| step | op | wall ms | footprint MB | rss MB | committed MB | live MB | frag MB | tiles MB | sp cache MB | gen2 | ok |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| baseline | baseline | 0.0 | 199.0 | 242.0 | 8.6 | 21.5 | 2.4 | 0.0 | 0.0 | 0 | yes |
| pre-open | mark | 0.1 | 199.0 | 242.0 | 8.6 | 21.6 | 2.4 | 0.0 | 0.0 | 0 | yes |
| open-altona | open | 31.9 | 326.0 | 310.0 | 16.8 | 46.7 | 5.8 | 0.0 | 0.0 | 0 | yes |
| view-continuous | viewmode | 0.0 | 545.0 | 522.0 | 107.2 | 194.2 | 14.3 | 1.0 | 0.0 | 4 | yes |
| idle-after-open | waitidle | 2299.2 | 581.0 | 614.0 | 337.6 | 234.8 | 120.8 | 2.5 | 0.0 | 7 | yes |
| scroll-17 | scrollpages | 745.1 | 725.0 | 679.0 | 349.7 | 351.6 | 54.5 | 10.9 | 0.0 | 10 | yes |
| idle-after-scroll | waitidle | 969.4 | 685.0 | 683.0 | 349.7 | 352.7 | 54.5 | 11.4 | 0.0 | 10 | yes |
| trim-background | trim | 0.5 | 467.0 | 468.0 | 349.7 | 352.7 | 54.5 | 4.4 | 0.0 | 10 | yes |
| idle-after-background-trim | waitidle | 485.8 | 465.0 | 468.0 | 154.9 | 156.1 | 4.5 | 4.4 | 0.0 | 11 | yes |
| trim-warn | trim | 0.3 | 465.0 | 468.0 | 154.9 | 156.2 | 4.5 | 4.4 | 0.0 | 11 | yes |
| idle-after-warn-trim | waitidle | 483.7 | 461.0 | 468.0 | 154.9 | 156.1 | 4.5 | 4.4 | 0.0 | 12 | yes |
| trim-critical | trim | 0.2 | 461.0 | 468.0 | 154.9 | 156.2 | 4.5 | 0.0 | 0.0 | 12 | yes |
| idle-after-critical-trim | waitidle | 484.6 | 457.0 | 468.0 | 154.9 | 156.1 | 4.5 | 0.0 | 0.0 | 13 | yes |
| scroll-back-17 | scrollpages | 728.5 | 832.0 | 787.0 | 468.8 | 425.7 | 71.7 | 7.5 | 0.0 | 16 | yes |
| idle-after-scroll-back | waitidle | 605.8 | 762.0 | 789.0 | 468.8 | 425.8 | 71.7 | 7.5 | 0.0 | 16 | yes |
| idle-20s | idle | 20001.4 | 755.0 | 789.0 | 468.8 | 426.1 | 71.7 | 7.5 | 0.0 | 16 | yes |

Footprint: baseline 199.0 MB -> peak 832.0 MB -> final 755.0 MB (**556.0 MB above baseline at the end**).

Render/trim metrics (from the #1491 sink): bandRenderP50Ms=132.2, bandRenderP99Ms=637.0, bandRenders=31, cacheTrimRequests=4.0, heapReclaimP50Ms=7.0, heapReclaims=3, singleRenders=0

### `w9-launch-idle`

_The fixed floor: a one-page form with nothing heavy in it. #1496 measured 336 MB footprint / 120 MB committed heap here, and every other number in the family is read against it._

3 run(s), 0 step failure(s), 0 boundary(ies) with no outer sample.

| step | op | wall ms | footprint MB | rss MB | committed MB | live MB | frag MB | tiles MB | sp cache MB | gen2 | ok |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| baseline | baseline | 0.0 | 199.0 | 242.0 | 8.6 | 21.5 | 2.5 | 0.0 | 0.0 | 0 | yes |
| pre-open | mark | 0.2 | 200.0 | 242.0 | 8.6 | 21.6 | 2.5 | 0.0 | 0.0 | 0 | yes |
| open-w9 | open | 99.5 | 378.0 | 335.0 | 27.8 | 33.1 | 7.0 | 0.0 | 0.0 | 1 | yes |
| view-continuous | viewmode | 0.0 | 375.0 | 337.0 | 28.1 | 43.9 | 4.9 | 20.6 | 0.0 | 2 | yes |
| idle-after-open | waitidle | 2665.2 | 324.0 | 360.0 | 34.6 | 51.7 | 1.5 | 20.6 | 0.0 | 2 | yes |
| idle-20s | idle | 20002.0 | 320.0 | 360.0 | 34.6 | 52.0 | 1.5 | 10.3 | 0.0 | 2 | yes |

Footprint: baseline 199.0 MB -> peak 378.0 MB -> final 320.0 MB (**121.0 MB above baseline at the end**).

Render/trim metrics (from the #1491 sink): bandRenders=1, cacheTrimRequests=2.0, heapReclaims=0, singleRenders=0

