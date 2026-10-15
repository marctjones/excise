# Live GUI performance scenarios (#1497)

```
sha=86aa2150
tree=clean
load1=1.72
sample=boundary
vmmap=0
repeats=3
set=default
scenarios=null w9-launch-idle irs-page30 irs-zoom altona-scroll altona-close altona-replace-w9 soft-trim-cycle irs-search-redact
systemTabbing=fullscreen
appHome=/private/tmp/excise-gui-perf/aot
exe=/Users/marc/Documents/GitHub/pdfe/logs/reader-bench-bundle-aot/excise.app/Contents/MacOS/Excise.App
started=2026-10-15T22:33:03Z
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
| `altona-close` | footprintMB | 3 | 578.0 | 543.0 | 608.0 | 65.0 |
| `altona-close` | rssMB | 3 | 547.0 | 516.0 | 563.0 | 47.0 |
| `altona-close` | committedMB | 3 | 347.3 | 338.5 | 361.2 | 22.7 |
| `altona-close` | liveHeapMB | 3 | 325.4 | 322.4 | 327.3 | 4.9 |
| `altona-close` | cpuTotalMs | 3 | 2496.4 | 2464.6 | 2524.5 | 59.9 |
| `altona-replace-w9` | footprintMB | 3 | 573.0 | 539.0 | 606.0 | 67.0 |
| `altona-replace-w9` | rssMB | 3 | 542.0 | 512.0 | 575.0 | 63.0 |
| `altona-replace-w9` | committedMB | 3 | 341.8 | 334.7 | 374.7 | 40.0 |
| `altona-replace-w9` | liveHeapMB | 3 | 322.5 | 322.4 | 325.3 | 2.9 |
| `altona-replace-w9` | cpuTotalMs | 3 | 2716.3 | 2709.5 | 2738.4 | 28.9 |
| `altona-scroll` | footprintMB | 3 | 574.0 | 554.0 | 611.0 | 57.0 |
| `altona-scroll` | rssMB | 3 | 528.0 | 523.0 | 580.0 | 57.0 |
| `altona-scroll` | committedMB | 3 | 346.5 | 334.8 | 379.2 | 44.4 |
| `altona-scroll` | liveHeapMB | 3 | 325.4 | 322.4 | 327.5 | 5.1 |
| `altona-scroll` | cpuTotalMs | 3 | 2489.8 | 2485.6 | 2510.9 | 25.3 |
| `irs-page30` | footprintMB | 3 | 449.0 | 449.0 | 452.0 | 3.0 |
| `irs-page30` | rssMB | 3 | 397.0 | 396.0 | 399.0 | 3.0 |
| `irs-page30` | committedMB | 3 | 83.0 | 81.3 | 83.3 | 2.0 |
| `irs-page30` | liveHeapMB | 3 | 103.1 | 99.5 | 111.2 | 11.7 |
| `irs-page30` | cpuTotalMs | 3 | 2181.4 | 2175.4 | 2203.2 | 27.8 |
| `irs-search-redact` | footprintMB | 3 | 387.0 | 375.0 | 388.0 | 13.0 |
| `irs-search-redact` | rssMB | 3 | 335.0 | 335.0 | 337.0 | 2.0 |
| `irs-search-redact` | committedMB | 3 | 80.2 | 79.9 | 80.2 | 0.3 |
| `irs-search-redact` | liveHeapMB | 3 | 112.8 | 110.8 | 116.6 | 5.8 |
| `irs-search-redact` | cpuTotalMs | 3 | 1107.6 | 1090.4 | 1112.8 | 22.4 |
| `irs-zoom` | footprintMB | 3 | 482.0 | 481.0 | 482.0 | 1.0 |
| `irs-zoom` | rssMB | 3 | 438.0 | 436.0 | 438.0 | 2.0 |
| `irs-zoom` | committedMB | 3 | 82.0 | 80.3 | 82.7 | 2.3 |
| `irs-zoom` | liveHeapMB | 3 | 117.5 | 112.9 | 117.8 | 4.9 |
| `irs-zoom` | cpuTotalMs | 3 | 1200.7 | 1185.6 | 1231.0 | 45.4 |
| `null` | footprintMB | 3 | 122.0 | 122.0 | 122.0 | 0.0 |
| `null` | rssMB | 3 | 166.0 | 166.0 | 166.0 | 0.0 |
| `null` | committedMB | 3 | 11.6 | 11.6 | 11.6 | 0.0 |
| `null` | liveHeapMB | 3 | 24.4 | 24.3 | 24.4 | 0.1 |
| `null` | cpuTotalMs | 3 | 189.4 | 187.0 | 190.4 | 3.4 |
| `soft-trim-cycle` | footprintMB | 3 | 717.0 | 637.0 | 719.0 | 82.0 |
| `soft-trim-cycle` | rssMB | 3 | 687.0 | 606.0 | 688.0 | 82.0 |
| `soft-trim-cycle` | committedMB | 3 | 486.5 | 400.8 | 486.5 | 85.8 |
| `soft-trim-cycle` | liveHeapMB | 3 | 348.4 | 348.0 | 353.4 | 5.3 |
| `soft-trim-cycle` | cpuTotalMs | 3 | 3687.5 | 3664.8 | 3731.5 | 66.8 |
| `w9-launch-idle` | footprintMB | 3 | 302.0 | 300.0 | 316.0 | 16.0 |
| `w9-launch-idle` | rssMB | 3 | 263.0 | 262.0 | 264.0 | 2.0 |
| `w9-launch-idle` | committedMB | 3 | 37.4 | 37.3 | 37.6 | 0.2 |
| `w9-launch-idle` | liveHeapMB | 3 | 54.6 | 50.1 | 54.6 | 4.5 |
| `w9-launch-idle` | cpuTotalMs | 3 | 453.2 | 445.4 | 460.3 | 14.8 |

## The floor every delta is judged against

`floor = max(noise spread, runner overhead, sampler overhead)`

| metric | floor | unit | from |
|---|---:|---|---|
| footprintMB — physical footprint (outer; the #1461/#1496 number) | 82.0 | MB | noiseSpread |
| rssMB — resident set (outer) | 82.0 | MB | noiseSpread |
| committedMB — committed managed heap (in-app) | 85.8 | MB | noiseSpread |
| liveHeapMB — live managed heap (in-app) | 11.7 | MB | noiseSpread |
| cpuTotalMs — cumulative process CPU (in-app) | 66.8 | ms | noiseSpread |

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
| baseline | baseline | 0.0 | 122.0 | 166.0 | 11.6 | 24.1 | 3.8 | 0.0 | 0.0 | 0 | yes |
| pre-open | mark | 0.0 | 122.0 | 166.0 | 11.6 | 24.1 | 3.8 | 0.0 | 0.0 | 0 | yes |
| open-altona | open | 13.8 | 389.0 | 407.0 | 20.7 | 42.2 | 2.6 | 0.0 | 0.0 | 0 | yes |
| view-continuous | viewmode | 0.0 | 432.0 | 409.0 | 203.6 | 327.3 | 14.0 | 2.0 | 0.0 | 4 | yes |
| idle-after-open | waitidle | 867.9 | 450.0 | 424.0 | 248.2 | 261.8 | 10.2 | 2.5 | 0.0 | 6 | yes |
| scroll-17 | scrollpages | 751.1 | 608.0 | 563.0 | 336.7 | 294.9 | 115.8 | 10.9 | 0.0 | 10 | yes |
| idle-after-scroll | waitidle | 500.5 | 608.0 | 563.0 | 336.7 | 295.0 | 115.8 | 10.9 | 0.0 | 10 | yes |
| close-altona | close | 5.8 | 253.0 | 240.0 | 361.2 | 288.4 | 88.8 | 0.0 | 0.0 | 10 | yes |
| idle-after-close | waitidle | 498.3 | 253.0 | 240.0 | 38.1 | 25.2 | 4.3 | 0.0 | 0.0 | 11 | yes |
| idle-45s | idle | 45002.5 | 200.0 | 233.0 | 31.7 | 24.2 | 4.5 | 0.0 | 0.0 | 12 | yes |

Footprint: baseline 122.0 MB -> peak 608.0 MB -> final 200.0 MB (**78.0 MB above baseline at the end**).

Render/trim metrics (from the #1491 sink): bandRenderP50Ms=51.6, bandRenderP99Ms=195.2, bandRenders=15, cacheTrimRequests=2.0, heapReclaimP50Ms=10.0, heapReclaims=2, singleRenders=0

### `altona-replace-w9`

_#1481: replacing a document runs ReleasedMemoryReclaimer (aggressive, blocking, compacting) and dropped footprint to 421 MB in 18 ms by hand. This is that path, repeatable._

3 run(s), 0 step failure(s), 0 boundary(ies) with no outer sample.

| step | op | wall ms | footprint MB | rss MB | committed MB | live MB | frag MB | tiles MB | sp cache MB | gen2 | ok |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| baseline | baseline | 0.0 | 122.0 | 166.0 | 11.6 | 24.6 | 3.9 | 0.0 | 0.0 | 0 | yes |
| pre-open | mark | 0.0 | 123.0 | 166.0 | 11.6 | 24.6 | 3.9 | 0.0 | 0.0 | 0 | yes |
| open-altona | open | 13.6 | 433.0 | 411.0 | 20.1 | 42.6 | 2.7 | 0.0 | 0.0 | 0 | yes |
| view-continuous | viewmode | 0.0 | 439.0 | 430.0 | 204.9 | 325.3 | 16.6 | 2.0 | 0.0 | 4 | yes |
| idle-after-open | waitidle | 874.3 | 462.0 | 435.0 | 259.0 | 256.2 | 15.2 | 2.5 | 0.0 | 6 | yes |
| scroll-17 | scrollpages | 745.5 | 606.0 | 575.0 | 374.7 | 267.6 | 134.1 | 10.9 | 0.0 | 10 | yes |
| idle-after-scroll | waitidle | 488.8 | 606.0 | 575.0 | 374.7 | 267.7 | 134.1 | 10.9 | 0.0 | 10 | yes |
| replace-with-w9 | open | 26.6 | 319.0 | 282.0 | 38.0 | 29.9 | 4.5 | 0.0 | 0.0 | 11 | yes |
| idle-after-replace | waitidle | 497.1 | 324.0 | 287.0 | 29.9 | 39.3 | 3.8 | 20.6 | 0.0 | 12 | yes |
| idle-45s | idle | 45005.3 | 242.0 | 284.0 | 32.5 | 37.8 | 0.0 | 10.3 | 0.0 | 13 | yes |

Footprint: baseline 122.0 MB -> peak 606.0 MB -> final 242.0 MB (**120.0 MB above baseline at the end**).

Render/trim metrics (from the #1491 sink): bandRenderP50Ms=52.1, bandRenderP99Ms=184.9, bandRenders=16, cacheTrimRequests=2.0, heapReclaimP50Ms=14.0, heapReclaims=2, singleRenders=0

### `altona-scroll`

_excise's worst measured metric against a native viewer: #1461 recorded 1440-2432 MB RSS after a full scroll against Preview's 446 MB on the same file. 122 MB, 17 pages, image-heavy._

3 run(s), 0 step failure(s), 0 boundary(ies) with no outer sample.

| step | op | wall ms | footprint MB | rss MB | committed MB | live MB | frag MB | tiles MB | sp cache MB | gen2 | ok |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| baseline | baseline | 0.0 | 122.0 | 166.0 | 11.6 | 24.1 | 3.8 | 0.0 | 0.0 | 0 | yes |
| pre-open | mark | 0.0 | 122.0 | 166.0 | 11.6 | 24.2 | 3.8 | 0.0 | 0.0 | 0 | yes |
| open-altona | open | 13.0 | 435.0 | 413.0 | 20.9 | 42.2 | 2.7 | 0.0 | 0.0 | 0 | yes |
| view-continuous | viewmode | 0.0 | 445.0 | 430.0 | 202.8 | 325.4 | 14.1 | 2.0 | 0.0 | 5 | yes |
| idle-after-open | waitidle | 865.8 | 550.0 | 523.0 | 346.5 | 255.5 | 102.9 | 2.5 | 0.0 | 7 | yes |
| scroll-17 | scrollpages | 745.7 | 554.0 | 523.0 | 318.2 | 276.9 | 79.6 | 10.9 | 0.0 | 11 | yes |
| idle-after-scroll | waitidle | 493.8 | 480.0 | 523.0 | 318.2 | 276.9 | 79.6 | 10.9 | 0.0 | 11 | yes |
| idle-45s | idle | 45004.4 | 243.0 | 290.0 | 87.6 | 94.7 | 0.1 | 4.4 | 0.0 | 12 | yes |

Footprint: baseline 122.0 MB -> peak 554.0 MB -> final 243.0 MB (**121.0 MB above baseline at the end**).

Render/trim metrics (from the #1491 sink): bandRenderP50Ms=52.0, bandRenderP99Ms=185.2, bandRenders=15, cacheTrimRequests=2.0, heapReclaims=1, singleRenders=0

### `irs-page30`

_#1469: ~650-710 MB after opening a 4 MB text-only document with zero image XObjects. Paging 30 pages is the same interaction the 09-11..09-14 hand-driven readings used (1104 -> 838 -> 720 MB footprint), so this scenario is what makes those comparable._

3 run(s), 0 step failure(s), 0 boundary(ies) with no outer sample.

| step | op | wall ms | footprint MB | rss MB | committed MB | live MB | frag MB | tiles MB | sp cache MB | gen2 | ok |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| baseline | baseline | 0.0 | 122.0 | 165.0 | 11.8 | 24.0 | 3.8 | 0.0 | 0.0 | 0 | yes |
| pre-open | mark | 0.0 | 122.0 | 166.0 | 11.8 | 24.1 | 3.8 | 0.0 | 0.0 | 0 | yes |
| open-irs | open | 59.9 | 316.0 | 280.0 | 53.0 | 60.6 | 14.8 | 0.0 | 0.0 | 2 | yes |
| view-single | viewmode | 1.1 | 381.0 | 318.0 | 54.0 | 62.3 | 17.6 | 20.6 | 0.0 | 5 | yes |
| idle-after-open | waitidle | 1244.2 | 410.0 | 357.0 | 83.0 | 111.2 | 9.0 | 20.6 | 20.6 | 9 | yes |
| page-30 | pageby | 3742.2 | 449.0 | 396.0 | 79.1 | 99.2 | 15.6 | 20.6 | 61.8 | 22 | yes |
| idle-after-paging | waitidle | 495.6 | 449.0 | 396.0 | 79.1 | 99.3 | 15.6 | 20.6 | 61.8 | 22 | yes |
| idle-20s | idle | 20002.3 | 370.0 | 396.0 | 79.1 | 99.6 | 15.6 | 20.6 | 61.8 | 22 | yes |

Footprint: baseline 122.0 MB -> peak 449.0 MB -> final 370.0 MB (**248.0 MB above baseline at the end**).

Render/trim metrics (from the #1491 sink): bandRenders=1, cacheTrimRequests=1.0, heapReclaims=0, singleRenders=1

### `irs-search-redact`

_The redaction path's live memory cost, which no headless harness measures with a real compositor attached. Deliberately does NOT close afterwards: a redacted document is dirty, and closing it would wait on the unsaved-changes dialog._

3 run(s), 0 step failure(s), 0 boundary(ies) with no outer sample.

| step | op | wall ms | footprint MB | rss MB | committed MB | live MB | frag MB | tiles MB | sp cache MB | gen2 | ok |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| baseline | baseline | 0.0 | 122.0 | 166.0 | 11.6 | 24.5 | 4.1 | 0.0 | 0.0 | 0 | yes |
| pre-open | mark | 0.0 | 122.0 | 166.0 | 11.6 | 24.5 | 4.1 | 0.0 | 0.0 | 0 | yes |
| open-irs | open | 64.8 | 330.0 | 278.0 | 53.1 | 55.8 | 19.3 | 0.0 | 0.0 | 3 | yes |
| view-continuous | viewmode | 0.0 | 330.0 | 278.0 | 53.1 | 60.9 | 18.6 | 20.6 | 0.0 | 5 | yes |
| idle-after-open | waitidle | 1117.0 | 368.0 | 315.0 | 79.9 | 112.8 | 3.1 | 20.6 | 0.0 | 9 | yes |
| search-income | search | 120.5 | 388.0 | 335.0 | 80.2 | 109.9 | 3.0 | 30.9 | 0.0 | 9 | yes |
| idle-after-search | waitidle | 500.1 | 388.0 | 335.0 | 80.2 | 109.9 | 3.0 | 30.9 | 0.0 | 9 | yes |
| redact-income | redacttext | 0.9 | 387.0 | 334.0 | 80.2 | 110.0 | 3.0 | 30.9 | 0.0 | 9 | yes |
| idle-after-redact | waitidle | 497.6 | 387.0 | 334.0 | 80.2 | 110.2 | 3.0 | 30.9 | 0.0 | 9 | yes |
| idle-20s | idle | 20005.1 | 309.0 | 334.0 | 80.2 | 110.5 | 3.0 | 30.9 | 0.0 | 9 | yes |

Footprint: baseline 122.0 MB -> peak 388.0 MB -> final 309.0 MB (**187.0 MB above baseline at the end**).

Render/trim metrics (from the #1491 sink): bandRenders=1, cacheTrimRequests=2.0, heapReclaims=0, singleRenders=0

### `irs-zoom`

_Zoom changes the render DPI, so it is the axis that multiplies tile bytes without touching page count. 25/100/500% brackets the range a reader actually uses._

3 run(s), 0 step failure(s), 0 boundary(ies) with no outer sample.

| step | op | wall ms | footprint MB | rss MB | committed MB | live MB | frag MB | tiles MB | sp cache MB | gen2 | ok |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| baseline | baseline | 0.0 | 122.0 | 166.0 | 11.6 | 24.4 | 3.9 | 0.0 | 0.0 | 0 | yes |
| pre-open | mark | 0.0 | 122.0 | 166.0 | 11.6 | 24.4 | 3.9 | 0.0 | 0.0 | 0 | yes |
| open-irs | open | 67.6 | 342.0 | 290.0 | 51.7 | 49.7 | 12.9 | 0.0 | 0.0 | 3 | yes |
| view-continuous | viewmode | 0.0 | 330.0 | 279.0 | 52.7 | 61.0 | 12.4 | 20.6 | 0.0 | 5 | yes |
| idle-after-open | waitidle | 1116.3 | 368.0 | 315.0 | 79.3 | 110.8 | 5.6 | 20.6 | 0.0 | 9 | yes |
| zoom-25 | zoom | 0.2 | 359.0 | 315.0 | 79.3 | 110.9 | 5.6 | 20.6 | 0.0 | 9 | yes |
| idle-zoom-25 | waitidle | 498.2 | 359.0 | 315.0 | 80.3 | 112.8 | 5.5 | 23.9 | 0.0 | 9 | yes |
| zoom-100 | zoom | 0.2 | 421.0 | 365.0 | 80.3 | 112.9 | 5.5 | 23.9 | 0.0 | 9 | yes |
| idle-zoom-100 | waitidle | 496.4 | 421.0 | 365.0 | 80.3 | 97.2 | 18.0 | 49.4 | 0.0 | 10 | yes |
| zoom-500 | zoom | 0.2 | 481.0 | 436.0 | 80.3 | 97.3 | 18.0 | 49.4 | 0.0 | 10 | yes |
| idle-zoom-500 | waitidle | 495.1 | 481.0 | 436.0 | 80.3 | 97.4 | 12.1 | 57.4 | 0.0 | 10 | yes |
| idle-10s | idle | 10005.0 | 402.0 | 436.0 | 80.3 | 97.6 | 12.1 | 57.4 | 0.0 | 10 | yes |

Footprint: baseline 122.0 MB -> peak 481.0 MB -> final 402.0 MB (**280.0 MB above baseline at the end**).

Render/trim metrics (from the #1491 sink): bandRenderP50Ms=16.7, bandRenderP99Ms=23.2, bandRenders=6, cacheTrimRequests=1.0, heapReclaims=0, singleRenders=0

### `null`

_CALIBRATION BASELINE. Launch, settle, idle, quit, with no document ever opened. Subtracted from every other scenario, and compared against a plain launch with the runner switched off to price the runner itself._

3 run(s), 0 step failure(s), 0 boundary(ies) with no outer sample.

| step | op | wall ms | footprint MB | rss MB | committed MB | live MB | frag MB | tiles MB | sp cache MB | gen2 | ok |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| baseline | baseline | 0.0 | 122.0 | 166.0 | 11.6 | 24.1 | 3.8 | 0.0 | 0.0 | 0 | yes |
| launched | mark | 0.0 | 122.0 | 166.0 | 11.6 | 24.1 | 3.8 | 0.0 | 0.0 | 0 | yes |
| idle-after-launch | waitidle | 497.6 | 120.0 | 166.0 | 11.6 | 24.2 | 3.8 | 0.0 | 0.0 | 0 | yes |
| idle-10s | idle | 10005.4 | 116.0 | 166.0 | 11.6 | 24.4 | 3.8 | 0.0 | 0.0 | 0 | yes |

Footprint: baseline 122.0 MB -> peak 122.0 MB -> final 116.0 MB (**-6.0 MB above baseline at the end**).

Render/trim metrics (from the #1491 sink): bandRenders=0, cacheTrimRequests=1.0, heapReclaims=0, singleRenders=0

### `soft-trim-cycle`

_#1478 and #1496 together: trim at Warn then Critical on a scrolled Altona, then scroll back. The trim is requested IN PROCESS through ViewerCacheTrimCoordinator, which is what removes `sudo memory_pressure` (and therefore the human) from the loop. The scroll-back step is there because #1496's own risk list names re-inflation churn: freed samples decode again._

3 run(s), 0 step failure(s), 0 boundary(ies) with no outer sample.

| step | op | wall ms | footprint MB | rss MB | committed MB | live MB | frag MB | tiles MB | sp cache MB | gen2 | ok |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| baseline | baseline | 0.0 | 122.0 | 166.0 | 11.6 | 24.0 | 3.8 | 0.0 | 0.0 | 0 | yes |
| pre-open | mark | 0.0 | 122.0 | 166.0 | 11.6 | 24.1 | 3.8 | 0.0 | 0.0 | 0 | yes |
| open-altona | open | 14.6 | 384.0 | 364.0 | 20.6 | 42.1 | 7.6 | 0.0 | 0.0 | 0 | yes |
| view-continuous | viewmode | 0.0 | 518.0 | 407.0 | 201.9 | 322.4 | 12.5 | 2.0 | 0.0 | 5 | yes |
| idle-after-open | waitidle | 876.6 | 543.0 | 516.0 | 338.6 | 262.9 | 100.5 | 2.5 | 0.0 | 7 | yes |
| scroll-17 | scrollpages | 747.5 | 559.0 | 529.0 | 329.8 | 295.0 | 60.1 | 10.9 | 0.0 | 11 | yes |
| idle-after-scroll | waitidle | 498.9 | 559.0 | 529.0 | 329.8 | 295.1 | 60.1 | 10.9 | 0.0 | 11 | yes |
| trim-background | trim | 0.1 | 277.0 | 320.0 | 329.8 | 295.2 | 60.1 | 4.4 | 0.0 | 11 | yes |
| idle-after-background-trim | waitidle | 495.8 | 277.0 | 320.0 | 118.0 | 121.1 | 0.3 | 4.4 | 0.0 | 12 | yes |
| trim-warn | trim | 0.1 | 277.0 | 320.0 | 118.0 | 121.1 | 0.3 | 4.4 | 0.0 | 12 | yes |
| idle-after-warn-trim | waitidle | 498.7 | 277.0 | 320.0 | 118.0 | 121.0 | 0.3 | 4.4 | 0.0 | 13 | yes |
| trim-critical | trim | 0.1 | 277.0 | 320.0 | 118.0 | 121.1 | 0.3 | 0.0 | 0.0 | 13 | yes |
| idle-after-critical-trim | waitidle | 496.8 | 277.0 | 320.0 | 118.0 | 121.0 | 0.3 | 0.0 | 0.0 | 14 | yes |
| scroll-back-17 | scrollpages | 739.3 | 637.0 | 606.0 | 400.8 | 348.0 | 105.4 | 7.5 | 0.0 | 19 | yes |
| idle-after-scroll-back | waitidle | 495.2 | 637.0 | 606.0 | 400.8 | 348.1 | 105.4 | 7.5 | 0.0 | 19 | yes |
| idle-20s | idle | 20004.8 | 558.0 | 606.0 | 400.8 | 348.4 | 105.4 | 7.5 | 0.0 | 19 | yes |

Footprint: baseline 122.0 MB -> peak 637.0 MB -> final 558.0 MB (**436.0 MB above baseline at the end**).

Render/trim metrics (from the #1491 sink): bandRenderP50Ms=51.8, bandRenderP99Ms=311.9, bandRenders=30, cacheTrimRequests=4.0, heapReclaimP50Ms=7.0, heapReclaims=3, singleRenders=0

### `w9-launch-idle`

_The fixed floor: a one-page form with nothing heavy in it. #1496 measured 336 MB footprint / 120 MB committed heap here, and every other number in the family is read against it._

3 run(s), 0 step failure(s), 0 boundary(ies) with no outer sample.

| step | op | wall ms | footprint MB | rss MB | committed MB | live MB | frag MB | tiles MB | sp cache MB | gen2 | ok |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| baseline | baseline | 0.0 | 122.0 | 166.0 | 11.8 | 24.4 | 4.1 | 0.0 | 0.0 | 0 | yes |
| pre-open | mark | 0.0 | 122.0 | 166.0 | 11.8 | 24.4 | 4.1 | 0.0 | 0.0 | 0 | yes |
| open-w9 | open | 16.7 | 291.0 | 253.0 | 20.1 | 33.7 | 2.6 | 0.0 | 0.0 | 0 | yes |
| view-continuous | viewmode | 0.0 | 279.0 | 242.0 | 27.4 | 37.3 | 7.7 | 20.6 | 0.0 | 2 | yes |
| idle-after-open | waitidle | 750.8 | 300.0 | 262.0 | 37.4 | 49.8 | 9.5 | 20.6 | 0.0 | 3 | yes |
| idle-20s | idle | 20004.7 | 222.0 | 262.0 | 37.4 | 50.1 | 9.5 | 20.6 | 0.0 | 3 | yes |

Footprint: baseline 122.0 MB -> peak 300.0 MB -> final 222.0 MB (**100.0 MB above baseline at the end**).

Render/trim metrics (from the #1491 sink): bandRenders=1, cacheTrimRequests=1.0, heapReclaims=0, singleRenders=0

