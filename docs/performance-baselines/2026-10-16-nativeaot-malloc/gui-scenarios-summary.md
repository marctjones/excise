# Live GUI performance scenarios (#1497)

```
sha=c7b08576
tree=dirty
load1=1.88
sample=boundary
vmmap=0
repeats=3
set=default
scenarios=null w9-launch-idle irs-page30 irs-zoom altona-scroll altona-close altona-replace-w9 soft-trim-cycle irs-search-redact irs-redact-save business-open business-search plans-open ds11-open scan-scroll-close altona-zoom altona-zoom-after-trim
systemTabbing=fullscreen
appHome=/private/tmp/excise-gui-perf/perf-aot-baseline
exe=/Users/marc/Documents/GitHub/pdfe/logs/reader-bench-bundle-aot-env/excise.app/Contents/MacOS/Excise.App
buildShape=NativeAOT
started=2026-10-16T02:20:59Z
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
| `altona-close` | footprintMB | 3 | 536.0 | 528.0 | 559.0 | 31.0 |
| `altona-close` | rssMB | 3 | 515.0 | 507.0 | 537.0 | 30.0 |
| `altona-close` | committedMB | 3 | 330.0 | 317.5 | 349.3 | 31.9 |
| `altona-close` | liveHeapMB | 3 | 322.7 | 322.7 | 325.6 | 2.9 |
| `altona-close` | cpuTotalMs | 3 | 2856.8 | 2843.0 | 2862.5 | 19.5 |
| `altona-replace-w9` | footprintMB | 3 | 543.0 | 519.0 | 553.0 | 34.0 |
| `altona-replace-w9` | rssMB | 3 | 521.0 | 488.0 | 533.0 | 45.0 |
| `altona-replace-w9` | committedMB | 3 | 339.2 | 299.8 | 344.5 | 44.7 |
| `altona-replace-w9` | liveHeapMB | 3 | 322.7 | 322.6 | 325.5 | 2.9 |
| `altona-replace-w9` | cpuTotalMs | 3 | 3121.8 | 3104.5 | 3154.5 | 50.0 |
| `altona-scroll` | footprintMB | 3 | 528.0 | 527.0 | 561.0 | 34.0 |
| `altona-scroll` | rssMB | 3 | 508.0 | 506.0 | 540.0 | 34.0 |
| `altona-scroll` | committedMB | 3 | 321.0 | 316.7 | 352.7 | 35.9 |
| `altona-scroll` | liveHeapMB | 3 | 292.5 | 287.9 | 322.9 | 35.0 |
| `altona-scroll` | cpuTotalMs | 3 | 2827.0 | 2810.2 | 2843.8 | 33.6 |
| `altona-zoom` | footprintMB | 3 | 891.0 | 888.0 | 899.0 | 11.0 |
| `altona-zoom` | rssMB | 3 | 872.0 | 868.0 | 883.0 | 15.0 |
| `altona-zoom` | committedMB | 3 | 670.6 | 670.5 | 670.7 | 0.1 |
| `altona-zoom` | liveHeapMB | 3 | 681.1 | 681.0 | 682.9 | 1.9 |
| `altona-zoom` | cpuTotalMs | 3 | 3292.1 | 3268.0 | 3299.2 | 31.2 |
| `altona-zoom-after-trim` | footprintMB | 3 | 649.0 | 635.0 | 658.0 | 23.0 |
| `altona-zoom-after-trim` | rssMB | 3 | 640.0 | 626.0 | 650.0 | 24.0 |
| `altona-zoom-after-trim` | committedMB | 3 | 379.8 | 323.6 | 405.5 | 81.8 |
| `altona-zoom-after-trim` | liveHeapMB | 3 | 325.6 | 325.4 | 328.3 | 2.9 |
| `altona-zoom-after-trim` | cpuTotalMs | 3 | 4625.0 | 4600.3 | 4631.8 | 31.5 |
| `business-open` | footprintMB | 3 | 385.0 | 385.0 | 387.0 | 2.0 |
| `business-open` | rssMB | 3 | 480.0 | 479.0 | 481.0 | 2.0 |
| `business-open` | committedMB | 3 | 86.6 | 86.6 | 88.2 | 1.7 |
| `business-open` | liveHeapMB | 3 | 123.6 | 123.3 | 123.7 | 0.4 |
| `business-open` | cpuTotalMs | 3 | 3846.5 | 3837.2 | 3859.6 | 22.4 |
| `business-search` | footprintMB | 3 | 335.0 | 333.0 | 335.0 | 2.0 |
| `business-search` | rssMB | 3 | 300.0 | 300.0 | 301.0 | 1.0 |
| `business-search` | committedMB | 3 | 86.6 | 86.6 | 88.4 | 1.8 |
| `business-search` | liveHeapMB | 3 | 123.6 | 123.4 | 123.7 | 0.3 |
| `business-search` | cpuTotalMs | 3 | 1716.4 | 1705.1 | 1717.3 | 12.2 |
| `ds11-open` | footprintMB | 3 | 465.0 | 463.0 | 476.0 | 13.0 |
| `ds11-open` | rssMB | 3 | 447.0 | 445.0 | 459.0 | 14.0 |
| `ds11-open` | committedMB | 3 | 188.2 | 186.0 | 199.1 | 13.1 |
| `ds11-open` | liveHeapMB | 3 | 290.2 | 249.7 | 292.9 | 43.2 |
| `ds11-open` | cpuTotalMs | 3 | 1603.6 | 1575.2 | 1609.1 | 33.9 |
| `irs-page30` | footprintMB | 3 | 375.0 | 375.0 | 376.0 | 1.0 |
| `irs-page30` | rssMB | 3 | 353.0 | 352.0 | 354.0 | 2.0 |
| `irs-page30` | committedMB | 3 | 81.0 | 80.8 | 81.2 | 0.4 |
| `irs-page30` | liveHeapMB | 3 | 104.8 | 103.2 | 106.5 | 3.3 |
| `irs-page30` | cpuTotalMs | 3 | 2825.9 | 2809.8 | 2838.0 | 28.1 |
| `irs-redact-save` | footprintMB | 3 | 571.0 | 513.0 | 573.0 | 60.0 |
| `irs-redact-save` | rssMB | 3 | 547.0 | 490.0 | 549.0 | 59.0 |
| `irs-redact-save` | committedMB | 3 | 321.9 | 268.9 | 323.2 | 54.3 |
| `irs-redact-save` | liveHeapMB | 3 | 340.3 | 340.3 | 382.2 | 42.0 |
| `irs-redact-save` | cpuTotalMs | 3 | 20575.2 | 20550.8 | 20790.1 | 239.3 |
| `irs-search-redact` | footprintMB | 3 | 327.0 | 326.0 | 329.0 | 3.0 |
| `irs-search-redact` | rssMB | 3 | 301.0 | 300.0 | 302.0 | 2.0 |
| `irs-search-redact` | committedMB | 3 | 80.2 | 79.6 | 80.2 | 0.6 |
| `irs-search-redact` | liveHeapMB | 3 | 111.6 | 110.8 | 115.1 | 4.3 |
| `irs-search-redact` | cpuTotalMs | 3 | 1316.9 | 1312.5 | 1319.7 | 7.2 |
| `irs-zoom` | footprintMB | 3 | 347.0 | 346.0 | 351.0 | 5.0 |
| `irs-zoom` | rssMB | 3 | 326.0 | 326.0 | 326.0 | 0.0 |
| `irs-zoom` | committedMB | 3 | 80.2 | 80.0 | 80.8 | 0.8 |
| `irs-zoom` | liveHeapMB | 3 | 113.0 | 112.8 | 113.1 | 0.3 |
| `irs-zoom` | cpuTotalMs | 3 | 1453.8 | 1450.6 | 1460.4 | 9.8 |
| `null` | footprintMB | 3 | 112.0 | 112.0 | 112.0 | 0.0 |
| `null` | rssMB | 3 | 159.0 | 159.0 | 159.0 | 0.0 |
| `null` | committedMB | 3 | 11.6 | 11.6 | 11.8 | 0.2 |
| `null` | liveHeapMB | 3 | 24.6 | 24.6 | 24.6 | 0.0 |
| `null` | cpuTotalMs | 3 | 240.1 | 236.9 | 246.2 | 9.4 |
| `plans-open` | footprintMB | 3 | 454.0 | 421.0 | 455.0 | 34.0 |
| `plans-open` | rssMB | 3 | 460.0 | 440.0 | 461.0 | 21.0 |
| `plans-open` | committedMB | 3 | 53.5 | 50.9 | 53.6 | 2.7 |
| `plans-open` | liveHeapMB | 3 | 58.5 | 55.2 | 71.3 | 16.0 |
| `plans-open` | cpuTotalMs | 3 | 4238.6 | 4221.3 | 4249.0 | 27.7 |
| `scan-scroll-close` | footprintMB | 3 | 428.0 | 422.0 | 429.0 | 7.0 |
| `scan-scroll-close` | rssMB | 3 | 413.0 | 413.0 | 414.0 | 1.0 |
| `scan-scroll-close` | committedMB | 3 | 33.3 | 31.7 | 34.0 | 2.3 |
| `scan-scroll-close` | liveHeapMB | 3 | 38.3 | 35.8 | 38.4 | 2.6 |
| `scan-scroll-close` | cpuTotalMs | 3 | 2617.4 | 2571.6 | 2651.4 | 79.8 |
| `soft-trim-cycle` | footprintMB | 3 | 689.0 | 660.0 | 690.0 | 30.0 |
| `soft-trim-cycle` | rssMB | 3 | 673.0 | 643.0 | 674.0 | 31.0 |
| `soft-trim-cycle` | committedMB | 3 | 488.5 | 458.0 | 488.5 | 30.5 |
| `soft-trim-cycle` | liveHeapMB | 3 | 348.5 | 348.4 | 348.6 | 0.3 |
| `soft-trim-cycle` | cpuTotalMs | 3 | 4116.7 | 4084.7 | 4127.4 | 42.7 |
| `w9-launch-idle` | footprintMB | 3 | 273.0 | 273.0 | 276.0 | 3.0 |
| `w9-launch-idle` | rssMB | 3 | 240.0 | 240.0 | 246.0 | 6.0 |
| `w9-launch-idle` | committedMB | 3 | 33.1 | 32.9 | 38.6 | 5.7 |
| `w9-launch-idle` | liveHeapMB | 3 | 49.5 | 49.4 | 51.3 | 1.9 |
| `w9-launch-idle` | cpuTotalMs | 3 | 608.8 | 608.8 | 610.6 | 1.8 |

## The floor every delta is judged against

`floor = max(noise spread, runner overhead, sampler overhead)`

| metric | floor | unit | from |
|---|---:|---|---|
| footprintMB — physical footprint (outer; the #1461/#1496 number) | 60.0 | MB | noiseSpread |
| rssMB — resident set (outer) | 59.0 | MB | noiseSpread |
| committedMB — committed managed heap (in-app) | 81.8 | MB | noiseSpread |
| liveHeapMB — live managed heap (in-app) | 43.2 | MB | noiseSpread |
| cpuTotalMs — cumulative process CPU (in-app) | 239.3 | ms | noiseSpread |

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

| step | op | wall ms | footprint MB | rss MB | committed MB | live MB | frag MB | tiles MB | sp cache MB | gen2 | ui stall ms | ok |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| baseline | baseline | 0.0 | 112.0 | 159.0 | 11.6 | 24.3 | 3.8 | 0.0 | 0.0 | 0 | 0 | yes |
| pre-open | mark | 0.0 | 112.0 | 159.0 | 11.6 | 24.3 | 3.8 | 0.0 | 0.0 | 0 | 2 | yes |
| open-altona | open | 38.3 | 376.0 | 353.0 | 20.7 | 42.4 | 2.5 | 0.0 | 0.0 | 0 | 2 | yes |
| view-continuous | viewmode | 0.0 | 519.0 | 398.0 | 205.2 | 325.6 | 16.6 | 2.0 | 0.0 | 4 | 2 | yes |
| idle-after-open | waitidle | 894.7 | 440.0 | 412.0 | 248.4 | 262.0 | 10.0 | 2.5 | 0.0 | 6 | 3 | yes |
| scroll-17 | scrollpages | 775.1 | 536.0 | 515.0 | 330.0 | 287.5 | 76.9 | 11.4 | 0.0 | 10 | 6 | yes |
| idle-after-scroll | waitidle | 512.0 | 464.0 | 515.0 | 330.0 | 287.6 | 76.9 | 11.4 | 0.0 | 10 | 2 | yes |
| close-altona | close | 14.3 | 200.0 | 214.0 | 330.0 | 288.9 | 76.9 | 0.0 | 0.0 | 10 | 2 | yes |
| idle-after-close | waitidle | 503.9 | 200.0 | 214.0 | 32.5 | 25.7 | 4.3 | 0.0 | 0.0 | 11 | 2 | yes |
| idle-45s | idle | 45002.2 | 153.0 | 214.0 | 32.1 | 24.4 | 4.5 | 0.0 | 0.0 | 12 | 3 | yes |

UI stall (the in-app heartbeat's worst gap past its 50 ms interval, per boundary): worst per run, median over 3 run(s) 7 ms (min 6, max 16); boundaries with a stall of 100 ms or more, median 0 of 10; the first run's worst is at `scroll-17`. No floor is known for this metric yet (FLOOR-UNKNOWN), so a difference is a lead, not a verdict.

Footprint: baseline 112.0 MB -> peak 536.0 MB -> final 153.0 MB (**41.0 MB above baseline at the end**).

Render/trim metrics (from the #1491 sink): bandRenderP50Ms=56.1, bandRenderP99Ms=203.8, bandRenders=16, cacheTrimRequests=1.0, heapReclaimP50Ms=27.0, heapReclaims=2, singleRenders=0

### `altona-replace-w9`

_#1481: replacing a document runs ReleasedMemoryReclaimer (aggressive, blocking, compacting) and dropped footprint to 421 MB in 18 ms by hand. This is that path, repeatable._

3 run(s), 0 step failure(s), 0 boundary(ies) with no outer sample.

| step | op | wall ms | footprint MB | rss MB | committed MB | live MB | frag MB | tiles MB | sp cache MB | gen2 | ui stall ms | ok |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| baseline | baseline | 0.0 | 112.0 | 159.0 | 11.6 | 24.1 | 3.9 | 0.0 | 0.0 | 0 | 0 | yes |
| pre-open | mark | 0.0 | 112.0 | 159.0 | 11.6 | 24.2 | 3.9 | 0.0 | 0.0 | 0 | 2 | yes |
| open-altona | open | 37.4 | 389.0 | 399.0 | 20.6 | 42.5 | 2.6 | 0.0 | 0.0 | 1 | 2 | yes |
| view-continuous | viewmode | 0.0 | 440.0 | 418.0 | 201.7 | 322.7 | 12.2 | 2.0 | 0.0 | 5 | 2 | yes |
| idle-after-open | waitidle | 906.0 | 530.0 | 502.0 | 339.2 | 262.8 | 101.4 | 2.5 | 0.0 | 7 | 2 | yes |
| scroll-17 | scrollpages | 774.2 | 543.0 | 521.0 | 332.8 | 287.3 | 79.3 | 11.4 | 0.0 | 11 | 2 | yes |
| idle-after-scroll | waitidle | 485.3 | 543.0 | 521.0 | 332.8 | 287.4 | 79.3 | 11.4 | 0.0 | 11 | 2 | yes |
| replace-with-w9 | open | 59.1 | 266.0 | 253.0 | 41.2 | 30.6 | 1.9 | 0.0 | 0.0 | 12 | 2 | yes |
| idle-after-replace | waitidle | 508.8 | 266.0 | 254.0 | 30.5 | 42.6 | 3.2 | 20.6 | 0.0 | 13 | 2 | yes |
| idle-45s | idle | 45001.4 | 179.0 | 254.0 | 32.3 | 38.2 | 0.2 | 10.3 | 0.0 | 14 | 22 | yes |

UI stall (the in-app heartbeat's worst gap past its 50 ms interval, per boundary): worst per run, median over 3 run(s) 26 ms (min 22, max 43); boundaries with a stall of 100 ms or more, median 0 of 10; the first run's worst is at `idle-45s`. No floor is known for this metric yet (FLOOR-UNKNOWN), so a difference is a lead, not a verdict.

Footprint: baseline 112.0 MB -> peak 543.0 MB -> final 179.0 MB (**67.0 MB above baseline at the end**).

Render/trim metrics (from the #1491 sink): bandRenderP50Ms=50.9, bandRenderP99Ms=199.7, bandRenders=17, cacheTrimRequests=1.0, heapReclaimP50Ms=17.0, heapReclaims=2, singleRenders=0

### `altona-scroll`

_excise's worst measured metric against a native viewer: #1461 recorded 1440-2432 MB RSS after a full scroll against Preview's 446 MB on the same file. 122 MB, 17 pages, image-heavy._

3 run(s), 0 step failure(s), 0 boundary(ies) with no outer sample.

| step | op | wall ms | footprint MB | rss MB | committed MB | live MB | frag MB | tiles MB | sp cache MB | gen2 | ui stall ms | ok |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| baseline | baseline | 0.0 | 112.0 | 159.0 | 11.6 | 24.3 | 3.8 | 0.0 | 0.0 | 0 | 0 | yes |
| pre-open | mark | 0.0 | 112.0 | 159.0 | 11.6 | 24.3 | 3.8 | 0.0 | 0.0 | 0 | 2 | yes |
| open-altona | open | 46.5 | 359.0 | 337.0 | 20.7 | 42.5 | 2.6 | 0.0 | 0.0 | 0 | 25 | yes |
| view-continuous | viewmode | 0.0 | 499.0 | 476.0 | 205.0 | 187.8 | 30.1 | 1.5 | 0.0 | 5 | 2 | yes |
| idle-after-open | waitidle | 906.2 | 437.0 | 410.0 | 246.0 | 258.2 | 2.5 | 2.5 | 0.0 | 7 | 2 | yes |
| scroll-17 | scrollpages | 774.4 | 528.0 | 508.0 | 321.0 | 292.5 | 67.6 | 11.4 | 0.0 | 11 | 15 | yes |
| idle-after-scroll | waitidle | 504.2 | 528.0 | 508.0 | 321.0 | 292.5 | 67.6 | 11.4 | 0.0 | 11 | 2 | yes |
| idle-45s | idle | 45000.7 | 219.0 | 278.0 | 91.3 | 95.0 | 0.3 | 4.4 | 0.0 | 12 | 25 | yes |

UI stall (the in-app heartbeat's worst gap past its 50 ms interval, per boundary): worst per run, median over 3 run(s) 50 ms (min 25, max 54); boundaries with a stall of 100 ms or more, median 0 of 8; the first run's worst is at `open-altona`. No floor is known for this metric yet (FLOOR-UNKNOWN), so a difference is a lead, not a verdict.

Footprint: baseline 112.0 MB -> peak 528.0 MB -> final 219.0 MB (**107.0 MB above baseline at the end**).

Render/trim metrics (from the #1491 sink): bandRenderP50Ms=54.9, bandRenderP99Ms=205.2, bandRenders=16, cacheTrimRequests=1.0, heapReclaims=1, singleRenders=0

### `altona-zoom`

_Zoom on an image-heavy document: 25/100/300% on the 17-page, 122 MB Altona file, where zoom multiplies decode and tile cost (irs-zoom is a text document). Each waitIdle's wall ms is the time until the page is sharp again; the ui stall column shows whether the window froze meanwhile._

3 run(s), 0 step failure(s), 0 boundary(ies) with no outer sample.

| step | op | wall ms | footprint MB | rss MB | committed MB | live MB | frag MB | tiles MB | sp cache MB | gen2 | ui stall ms | ok |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| baseline | baseline | 0.0 | 112.0 | 159.0 | 11.6 | 24.3 | 3.8 | 0.0 | 0.0 | 0 | 0 | yes |
| pre-open | mark | 0.0 | 112.0 | 159.0 | 11.6 | 24.3 | 3.8 | 0.0 | 0.0 | 0 | 2 | yes |
| open-altona | open | 38.8 | 379.0 | 356.0 | 20.7 | 42.4 | 2.6 | 0.0 | 0.0 | 0 | 12 | yes |
| view-continuous | viewmode | 0.0 | 432.0 | 408.0 | 201.8 | 322.6 | 12.2 | 2.0 | 0.0 | 5 | 2 | yes |
| idle-after-open | waitidle | 908.6 | 525.0 | 497.0 | 332.7 | 256.2 | 89.0 | 2.5 | 0.0 | 7 | 3 | yes |
| zoom-25 | zoom | 1.2 | 737.0 | 718.0 | 332.7 | 256.3 | 89.0 | 2.5 | 0.0 | 7 | 2 | yes |
| idle-zoom-25 | waitidle | 505.9 | 814.0 | 788.0 | 595.7 | 630.0 | 23.3 | 3.2 | 0.0 | 9 | 2 | yes |
| zoom-100 | zoom | 0.9 | 832.0 | 809.0 | 595.7 | 630.1 | 23.3 | 3.2 | 0.0 | 9 | 2 | yes |
| idle-zoom-100 | waitidle | 511.0 | 832.0 | 809.0 | 617.7 | 641.0 | 3.7 | 7.7 | 0.0 | 9 | 2 | yes |
| zoom-300 | zoom | 0.5 | 872.0 | 861.0 | 617.7 | 641.1 | 3.7 | 7.7 | 0.0 | 9 | 2 | yes |
| idle-zoom-300 | waitidle | 516.4 | 891.0 | 868.0 | 670.5 | 680.8 | 1.0 | 21.6 | 0.0 | 9 | 2 | yes |
| idle-10s | idle | 10007.7 | 813.0 | 868.0 | 670.5 | 681.0 | 1.0 | 21.6 | 0.0 | 9 | 2 | yes |

UI stall (the in-app heartbeat's worst gap past its 50 ms interval, per boundary): worst per run, median over 3 run(s) 12 ms (min 9, max 12); boundaries with a stall of 100 ms or more, median 0 of 12; the first run's worst is at `open-altona`. No floor is known for this metric yet (FLOOR-UNKNOWN), so a difference is a lead, not a verdict.

Footprint: baseline 112.0 MB -> peak 891.0 MB -> final 813.0 MB (**701.0 MB above baseline at the end**).

Render/trim metrics (from the #1491 sink): bandRenderP50Ms=72.1, bandRenderP99Ms=330.0, bandRenders=17, cacheTrimRequests=0, heapReclaims=0, singleRenders=0

### `altona-zoom-after-trim`

_What #2071 costs: a trim now releases a settled band page's decoded samples, so the first zoom after a Background trim decodes the images again (the investigator saw Altona page 1 band renders go from 34/51 to 132/157 ms, ~190 ms CPU). The same 2.0x zoom is taken first with the cache warm and then after a Background trim, back to back; compare the wall ms of idle-zoom-warm and idle-zoom-after-trim._

3 run(s), 0 step failure(s), 0 boundary(ies) with no outer sample.

| step | op | wall ms | footprint MB | rss MB | committed MB | live MB | frag MB | tiles MB | sp cache MB | gen2 | ui stall ms | ok |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| baseline | baseline | 0.0 | 112.0 | 159.0 | 11.6 | 24.2 | 3.8 | 0.0 | 0.0 | 0 | 0 | yes |
| pre-open | mark | 0.0 | 112.0 | 159.0 | 11.6 | 24.2 | 3.8 | 0.0 | 0.0 | 0 | 2 | yes |
| open-altona | open | 38.5 | 387.0 | 399.0 | 20.6 | 42.3 | 2.5 | 0.0 | 0.0 | 0 | 5 | yes |
| view-continuous | viewmode | 0.0 | 441.0 | 415.0 | 203.6 | 328.3 | 14.0 | 2.0 | 0.0 | 4 | 2 | yes |
| idle-after-open | waitidle | 890.8 | 437.0 | 409.0 | 246.6 | 256.9 | 2.6 | 2.5 | 0.0 | 6 | 3 | yes |
| scroll-17 | scrollpages | 775.0 | 531.0 | 509.0 | 298.2 | 313.4 | 39.7 | 11.4 | 0.0 | 10 | 6 | yes |
| idle-after-scroll | waitidle | 518.1 | 459.0 | 509.0 | 298.2 | 313.4 | 39.7 | 11.4 | 0.0 | 10 | 2 | yes |
| zoom-200-warm | zoom | 0.8 | 649.0 | 640.0 | 298.2 | 313.5 | 39.7 | 11.4 | 0.0 | 10 | 1 | yes |
| idle-zoom-warm | waitidle | 766.3 | 559.0 | 563.0 | 323.6 | 159.8 | 188.7 | 46.0 | 0.0 | 13 | 2 | yes |
| zoom-100 | zoom | 0.9 | 390.0 | 397.0 | 323.6 | 159.8 | 188.7 | 46.0 | 0.0 | 13 | 2 | yes |
| idle-zoom-100 | waitidle | 504.7 | 390.0 | 397.0 | 121.8 | 159.2 | 0.0 | 56.6 | 0.0 | 14 | 2 | yes |
| trim-background | trim | 3.2 | 196.0 | 300.0 | 121.8 | 159.2 | 0.0 | 9.5 | 0.0 | 14 | 2 | yes |
| idle-after-trim | waitidle | 502.7 | 193.0 | 300.0 | 51.6 | 54.9 | 0.0 | 9.5 | 0.0 | 15 | 2 | yes |
| zoom-200-after-trim | zoom | 1.3 | 344.0 | 392.0 | 51.6 | 55.0 | 0.0 | 9.5 | 0.0 | 15 | 2 | yes |
| idle-zoom-after-trim | waitidle | 639.3 | 382.0 | 419.0 | 175.7 | 160.1 | 39.9 | 44.1 | 0.0 | 21 | 2 | yes |
| idle-10s | idle | 10007.6 | 304.0 | 419.0 | 175.7 | 160.3 | 39.9 | 44.1 | 0.0 | 21 | 2 | yes |

UI stall (the in-app heartbeat's worst gap past its 50 ms interval, per boundary): worst per run, median over 3 run(s) 9 ms (min 6, max 11); boundaries with a stall of 100 ms or more, median 0 of 16; the first run's worst is at `scroll-17`. No floor is known for this metric yet (FLOOR-UNKNOWN), so a difference is a lead, not a verdict.

Footprint: baseline 112.0 MB -> peak 649.0 MB -> final 304.0 MB (**192.0 MB above baseline at the end**).

Render/trim metrics (from the #1491 sink): bandRenderP50Ms=56.2, bandRenderP99Ms=328.4, bandRenders=29, cacheTrimRequests=1.0, heapReclaims=1, singleRenders=0

### `business-open`

_Open to settled for the long-document regime: a 455-page, 7 MB book (page index, thumbnail placeholders, text index), which the 126-page irs scenarios do not reach. The open step's wall ms is document load, idle-after-open is time until the viewer is quiescent (first page and the render-ahead band drawn); then 30 page heights of scrolling._

3 run(s), 0 step failure(s), 0 boundary(ies) with no outer sample.

| step | op | wall ms | footprint MB | rss MB | committed MB | live MB | frag MB | tiles MB | sp cache MB | gen2 | ui stall ms | ok |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| baseline | baseline | 0.0 | 112.0 | 159.0 | 11.6 | 24.7 | 4.0 | 0.0 | 0.0 | 0 | 0 | yes |
| pre-open | mark | 0.0 | 112.0 | 159.0 | 11.6 | 24.7 | 4.0 | 0.0 | 0.0 | 0 | 2 | yes |
| open-business | open | 310.1 | 288.0 | 255.0 | 52.8 | 70.0 | 5.5 | 0.0 | 0.0 | 6 | 177 | yes |
| view-continuous | viewmode | 0.0 | 276.0 | 245.0 | 53.5 | 65.1 | 15.4 | 19.2 | 0.0 | 7 | 2 | yes |
| idle-after-open | waitidle | 1281.7 | 322.0 | 290.0 | 88.2 | 123.3 | 25.2 | 19.2 | 0.0 | 11 | 2 | yes |
| scroll-30 | scrollpages | 5354.2 | 385.0 | 479.0 | 76.9 | 107.0 | 18.3 | 199.2 | 0.0 | 38 | 11 | yes |
| idle-after-scroll | waitidle | 508.4 | 385.0 | 479.0 | 76.9 | 107.1 | 18.3 | 199.2 | 0.0 | 38 | 2 | yes |
| idle-10s | idle | 10009.5 | 306.0 | 479.0 | 76.9 | 107.3 | 18.3 | 199.2 | 0.0 | 38 | 3 | yes |

UI stall (the in-app heartbeat's worst gap past its 50 ms interval, per boundary): worst per run, median over 3 run(s) 216 ms (min 177, max 221); boundaries with a stall of 100 ms or more, median 1 of 8; the first run's worst is at `open-business`. No floor is known for this metric yet (FLOOR-UNKNOWN), so a difference is a lead, not a verdict.

Footprint: baseline 112.0 MB -> peak 385.0 MB -> final 306.0 MB (**194.0 MB above baseline at the end**).

Render/trim metrics (from the #1491 sink): bandRenderP50Ms=29.1, bandRenderP99Ms=44.2, bandRenders=23, cacheTrimRequests=0, heapReclaims=0, singleRenders=0

### `business-search`

_Search latency and its memory on the 455-page book: the search step's wall ms is the time to settle on a common term (many hits) and then on a rare one (a whole-document scan with few), after the text index is built. The matching rules changed in 2026-10 (case folding, accents, hyphen/space); nothing in the GUI timed them._

3 run(s), 0 step failure(s), 0 boundary(ies) with no outer sample.

| step | op | wall ms | footprint MB | rss MB | committed MB | live MB | frag MB | tiles MB | sp cache MB | gen2 | ui stall ms | ok |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| baseline | baseline | 0.0 | 112.0 | 159.0 | 11.6 | 24.6 | 4.0 | 0.0 | 0.0 | 0 | 0 | yes |
| pre-open | mark | 0.0 | 112.0 | 159.0 | 11.6 | 24.6 | 4.0 | 0.0 | 0.0 | 0 | 2 | yes |
| open-business | open | 298.0 | 278.0 | 245.0 | 52.9 | 70.0 | 5.5 | 0.0 | 0.0 | 6 | 221 | yes |
| view-continuous | viewmode | 0.0 | 278.0 | 245.0 | 53.7 | 64.8 | 15.5 | 19.2 | 0.0 | 7 | 2 | yes |
| idle-after-open | waitidle | 1274.7 | 324.0 | 290.0 | 88.4 | 123.4 | 25.2 | 19.2 | 0.0 | 11 | 3 | yes |
| search-common | search | 127.0 | 333.0 | 300.0 | 81.0 | 107.1 | 19.3 | 38.3 | 0.0 | 12 | 2 | yes |
| idle-after-common | waitidle | 507.8 | 333.0 | 300.0 | 81.0 | 107.1 | 19.3 | 38.3 | 0.0 | 12 | 2 | yes |
| search-rare | search | 130.5 | 333.0 | 300.0 | 81.0 | 109.3 | 18.9 | 38.3 | 0.0 | 12 | 2 | yes |
| idle-after-rare | waitidle | 508.2 | 333.0 | 300.0 | 81.0 | 109.3 | 18.9 | 38.3 | 0.0 | 12 | 2 | yes |
| idle-10s | idle | 10001.1 | 255.0 | 300.0 | 81.0 | 109.6 | 18.9 | 38.3 | 0.0 | 12 | 3 | yes |

UI stall (the in-app heartbeat's worst gap past its 50 ms interval, per boundary): worst per run, median over 3 run(s) 215 ms (min 207, max 221); boundaries with a stall of 100 ms or more, median 1 of 10; the first run's worst is at `open-business`. No floor is known for this metric yet (FLOOR-UNKNOWN), so a difference is a lead, not a verdict.

Footprint: baseline 112.0 MB -> peak 333.0 MB -> final 255.0 MB (**143.0 MB above baseline at the end**).

Render/trim metrics (from the #1491 sink): bandRenderP50Ms=11.2, bandRenderP99Ms=21.4, bandRenders=2, cacheTrimRequests=0, heapReclaims=0, singleRenders=0

### `ds11-open`

_Open to settled for a real AcroForm (the state DS-11 passport application, 6 pages): widget and appearance-stream rendering, which the text and image scenarios never exercise._

3 run(s), 0 step failure(s), 0 boundary(ies) with no outer sample.

| step | op | wall ms | footprint MB | rss MB | committed MB | live MB | frag MB | tiles MB | sp cache MB | gen2 | ui stall ms | ok |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| baseline | baseline | 0.0 | 112.0 | 159.0 | 11.6 | 24.5 | 3.9 | 0.0 | 0.0 | 0 | 0 | yes |
| pre-open | mark | 0.0 | 113.0 | 159.0 | 11.6 | 24.6 | 3.9 | 0.0 | 0.0 | 0 | 2 | yes |
| open-ds11 | open | 82.9 | 298.0 | 265.0 | 31.8 | 38.4 | 2.8 | 0.0 | 0.0 | 2 | 14 | yes |
| view-continuous | viewmode | 0.0 | 374.0 | 347.0 | 113.2 | 109.1 | 54.5 | 10.3 | 0.0 | 8 | 2 | yes |
| idle-after-open | waitidle | 894.0 | 394.0 | 367.0 | 155.3 | 252.9 | 0.4 | 20.6 | 0.0 | 13 | 2 | yes |
| scroll-5 | scrollpages | 1051.4 | 463.0 | 445.0 | 186.0 | 287.7 | 26.6 | 61.7 | 0.0 | 22 | 29 | yes |
| idle-after-scroll | waitidle | 521.9 | 463.0 | 445.0 | 186.0 | 287.8 | 26.6 | 61.7 | 0.0 | 22 | 2 | yes |
| idle-10s | idle | 10009.7 | 385.0 | 445.0 | 186.0 | 292.9 | 29.7 | 61.7 | 0.0 | 22 | 3 | yes |

UI stall (the in-app heartbeat's worst gap past its 50 ms interval, per boundary): worst per run, median over 3 run(s) 29 ms (min 23, max 33); boundaries with a stall of 100 ms or more, median 0 of 8; the first run's worst is at `scroll-5`. No floor is known for this metric yet (FLOOR-UNKNOWN), so a difference is a lead, not a verdict.

Footprint: baseline 112.0 MB -> peak 463.0 MB -> final 385.0 MB (**273.0 MB above baseline at the end**).

Render/trim metrics (from the #1491 sink): bandRenderP50Ms=120.6, bandRenderP99Ms=136.7, bandRenders=4, cacheTrimRequests=0, heapReclaims=0, singleRenders=0

### `irs-page30`

_#1469: ~650-710 MB after opening a 4 MB text-only document with zero image XObjects. Paging 30 pages is the same interaction the 09-11..09-14 hand-driven readings used (1104 -> 838 -> 720 MB footprint), so this scenario is what makes those comparable._

3 run(s), 0 step failure(s), 0 boundary(ies) with no outer sample.

| step | op | wall ms | footprint MB | rss MB | committed MB | live MB | frag MB | tiles MB | sp cache MB | gen2 | ui stall ms | ok |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| baseline | baseline | 0.0 | 112.0 | 159.0 | 11.6 | 24.3 | 3.8 | 0.0 | 0.0 | 0 | 0 | yes |
| pre-open | mark | 0.0 | 112.0 | 159.0 | 11.6 | 24.3 | 3.8 | 0.0 | 0.0 | 0 | 2 | yes |
| open-irs | open | 114.2 | 279.0 | 251.0 | 51.9 | 49.6 | 12.9 | 0.0 | 0.0 | 3 | 23 | yes |
| view-single | viewmode | 1.7 | 311.0 | 284.0 | 51.9 | 60.5 | 12.5 | 20.6 | 0.0 | 6 | 2 | yes |
| idle-after-open | waitidle | 1152.8 | 339.0 | 313.0 | 81.2 | 103.2 | 8.0 | 20.6 | 20.6 | 10 | 2 | yes |
| page-30 | pageby | 3871.9 | 375.0 | 353.0 | 78.4 | 99.2 | 14.7 | 20.6 | 61.8 | 23 | 2 | yes |
| idle-after-paging | waitidle | 501.5 | 375.0 | 353.0 | 78.4 | 99.3 | 14.7 | 20.6 | 61.8 | 23 | 2 | yes |
| idle-20s | idle | 20010.4 | 297.0 | 352.0 | 78.4 | 99.6 | 14.7 | 20.6 | 61.8 | 23 | 3 | yes |

UI stall (the in-app heartbeat's worst gap past its 50 ms interval, per boundary): worst per run, median over 3 run(s) 20 ms (min 19, max 23); boundaries with a stall of 100 ms or more, median 0 of 8; the first run's worst is at `open-irs`. No floor is known for this metric yet (FLOOR-UNKNOWN), so a difference is a lead, not a verdict.

Footprint: baseline 112.0 MB -> peak 375.0 MB -> final 297.0 MB (**185.0 MB above baseline at the end**).

Render/trim metrics (from the #1491 sink): bandRenders=1, cacheTrimRequests=0, heapReclaims=0, singleRenders=1

### `irs-redact-save`

_The redaction workflow end to end in the GUI process: the apply-and-save time (the saveAs step's wall ms), the memory it peaks at, and whether the UI thread froze while it ran (the ui stall column). irs-search-redact stops at marking because closing a dirty document would wait on a dialog; saveAs applies the pending redactions through the same engine as `excise redact`, writes a scratch copy (deleted afterwards) and leaves the document clean, so this scenario can close it and show what a redacted document gives back._

3 run(s), 0 step failure(s), 0 boundary(ies) with no outer sample.

| step | op | wall ms | footprint MB | rss MB | committed MB | live MB | frag MB | tiles MB | sp cache MB | gen2 | ui stall ms | ok |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| baseline | baseline | 0.0 | 112.0 | 159.0 | 11.6 | 24.3 | 3.8 | 0.0 | 0.0 | 0 | 0 | yes |
| pre-open | mark | 0.0 | 112.0 | 159.0 | 11.6 | 24.3 | 3.8 | 0.0 | 0.0 | 0 | 2 | yes |
| open-irs | open | 115.4 | 289.0 | 259.0 | 51.9 | 49.6 | 12.9 | 0.0 | 0.0 | 3 | 30 | yes |
| view-continuous | viewmode | 0.0 | 278.0 | 254.0 | 51.9 | 63.3 | 13.3 | 20.6 | 0.0 | 5 | 2 | yes |
| idle-after-open | waitidle | 1273.9 | 315.0 | 291.0 | 79.4 | 111.0 | 5.4 | 20.6 | 0.0 | 9 | 3 | yes |
| search-income | search | 130.5 | 325.0 | 301.0 | 80.2 | 108.3 | 5.3 | 30.9 | 0.0 | 9 | 2 | yes |
| idle-after-search | waitidle | 513.2 | 325.0 | 301.0 | 80.2 | 108.4 | 5.3 | 30.9 | 0.0 | 9 | 2 | yes |
| redact-income | redacttext | 3.4 | 324.0 | 300.0 | 80.2 | 108.4 | 5.3 | 30.9 | 0.0 | 9 | 2 | yes |
| save-redacted | saveas | 19478.1 | 513.0 | 490.0 | 268.9 | 380.7 | 38.7 | 30.9 | 0.0 | 66 | 19433 | yes |
| idle-after-save | waitidle | 511.2 | 513.0 | 490.0 | 268.9 | 380.9 | 38.7 | 30.9 | 0.0 | 66 | 2 | yes |
| close-saved | close | 17.0 | 254.0 | 244.0 | 268.9 | 382.2 | 38.7 | 0.0 | 0.0 | 66 | 15 | yes |
| idle-after-close | waitidle | 519.3 | 254.0 | 244.0 | 41.5 | 30.7 | 14.4 | 0.0 | 0.0 | 67 | 2 | yes |
| idle-20s | idle | 20002.7 | 176.0 | 244.0 | 41.5 | 31.0 | 14.4 | 0.0 | 0.0 | 67 | 3 | yes |

UI stall (the in-app heartbeat's worst gap past its 50 ms interval, per boundary): worst per run, median over 3 run(s) 19310 ms (min 19102, max 19433); boundaries with a stall of 100 ms or more, median 1 of 13; the first run's worst is at `save-redacted`. No floor is known for this metric yet (FLOOR-UNKNOWN), so a difference is a lead, not a verdict.

Footprint: baseline 112.0 MB -> peak 513.0 MB -> final 176.0 MB (**64.0 MB above baseline at the end**).

Render/trim metrics (from the #1491 sink): bandRenders=1, cacheTrimRequests=0, heapReclaims=1, singleRenders=0

### `irs-search-redact`

_The redaction path's live memory cost, which no headless harness measures with a real compositor attached. Deliberately does NOT close afterwards: a redacted document is dirty, and closing it would wait on the unsaved-changes dialog._

3 run(s), 0 step failure(s), 0 boundary(ies) with no outer sample.

| step | op | wall ms | footprint MB | rss MB | committed MB | live MB | frag MB | tiles MB | sp cache MB | gen2 | ui stall ms | ok |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| baseline | baseline | 0.0 | 112.0 | 159.0 | 11.8 | 24.7 | 4.0 | 0.0 | 0.0 | 0 | 0 | yes |
| pre-open | mark | 0.0 | 112.0 | 159.0 | 11.8 | 24.7 | 4.0 | 0.0 | 0.0 | 0 | 2 | yes |
| open-irs | open | 115.3 | 292.0 | 259.0 | 51.7 | 49.3 | 12.7 | 0.0 | 0.0 | 3 | 20 | yes |
| view-continuous | viewmode | 0.0 | 281.0 | 254.0 | 51.7 | 60.1 | 12.2 | 20.6 | 0.0 | 5 | 3 | yes |
| idle-after-open | waitidle | 1157.8 | 318.0 | 291.0 | 80.2 | 107.3 | 5.4 | 20.6 | 0.0 | 9 | 2 | yes |
| search-income | search | 130.5 | 327.0 | 300.0 | 80.2 | 110.9 | 5.4 | 30.9 | 0.0 | 9 | 2 | yes |
| idle-after-search | waitidle | 508.7 | 327.0 | 300.0 | 80.2 | 111.0 | 5.4 | 30.9 | 0.0 | 9 | 2 | yes |
| redact-income | redacttext | 3.4 | 327.0 | 300.0 | 80.2 | 111.1 | 5.4 | 30.9 | 0.0 | 9 | 2 | yes |
| idle-after-redact | waitidle | 516.9 | 327.0 | 300.0 | 80.2 | 111.2 | 5.4 | 30.9 | 0.0 | 9 | 2 | yes |
| idle-20s | idle | 20010.2 | 248.0 | 300.0 | 80.2 | 111.6 | 5.4 | 30.9 | 0.0 | 9 | 3 | yes |

UI stall (the in-app heartbeat's worst gap past its 50 ms interval, per boundary): worst per run, median over 3 run(s) 27 ms (min 20, max 28); boundaries with a stall of 100 ms or more, median 0 of 10; the first run's worst is at `open-irs`. No floor is known for this metric yet (FLOOR-UNKNOWN), so a difference is a lead, not a verdict.

Footprint: baseline 112.0 MB -> peak 327.0 MB -> final 248.0 MB (**136.0 MB above baseline at the end**).

Render/trim metrics (from the #1491 sink): bandRenders=1, cacheTrimRequests=0, heapReclaims=0, singleRenders=0

### `irs-zoom`

_Zoom changes the render DPI, so it is the axis that multiplies tile bytes without touching page count. 25/100/500% brackets the range a reader actually uses._

3 run(s), 0 step failure(s), 0 boundary(ies) with no outer sample.

| step | op | wall ms | footprint MB | rss MB | committed MB | live MB | frag MB | tiles MB | sp cache MB | gen2 | ui stall ms | ok |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| baseline | baseline | 0.0 | 112.0 | 159.0 | 11.6 | 24.6 | 4.0 | 0.0 | 0.0 | 0 | 0 | yes |
| pre-open | mark | 0.0 | 112.0 | 159.0 | 11.6 | 24.6 | 4.0 | 0.0 | 0.0 | 0 | 2 | yes |
| open-irs | open | 112.8 | 292.0 | 264.0 | 51.7 | 49.3 | 12.6 | 0.0 | 0.0 | 3 | 25 | yes |
| view-continuous | viewmode | 0.0 | 280.0 | 254.0 | 51.7 | 60.3 | 12.0 | 20.6 | 0.0 | 5 | 1 | yes |
| idle-after-open | waitidle | 1150.6 | 318.0 | 290.0 | 79.8 | 112.0 | 5.5 | 20.6 | 0.0 | 9 | 3 | yes |
| zoom-25 | zoom | 0.9 | 294.0 | 280.0 | 79.8 | 112.1 | 5.5 | 20.6 | 0.0 | 9 | 2 | yes |
| idle-zoom-25 | waitidle | 497.6 | 294.0 | 280.0 | 80.8 | 112.7 | 5.4 | 23.9 | 0.0 | 9 | 2 | yes |
| zoom-100 | zoom | 0.9 | 351.0 | 326.0 | 80.8 | 112.8 | 5.4 | 23.9 | 0.0 | 9 | 2 | yes |
| idle-zoom-100 | waitidle | 504.0 | 351.0 | 326.0 | 80.8 | 96.7 | 17.9 | 49.4 | 0.0 | 10 | 2 | yes |
| zoom-500 | zoom | 0.7 | 325.0 | 314.0 | 80.8 | 96.7 | 17.9 | 49.4 | 0.0 | 10 | 2 | yes |
| idle-zoom-500 | waitidle | 513.8 | 325.0 | 314.0 | 80.8 | 97.3 | 11.9 | 57.4 | 0.0 | 10 | 2 | yes |
| idle-10s | idle | 10002.1 | 247.0 | 314.0 | 80.8 | 97.5 | 11.9 | 57.4 | 0.0 | 10 | 3 | yes |

UI stall (the in-app heartbeat's worst gap past its 50 ms interval, per boundary): worst per run, median over 3 run(s) 27 ms (min 25, max 28); boundaries with a stall of 100 ms or more, median 0 of 12; the first run's worst is at `open-irs`. No floor is known for this metric yet (FLOOR-UNKNOWN), so a difference is a lead, not a verdict.

Footprint: baseline 112.0 MB -> peak 351.0 MB -> final 247.0 MB (**135.0 MB above baseline at the end**).

Render/trim metrics (from the #1491 sink): bandRenderP50Ms=21.6, bandRenderP99Ms=53.6, bandRenders=6, cacheTrimRequests=0, heapReclaims=0, singleRenders=0

### `null`

_CALIBRATION BASELINE. Launch, settle, idle, quit, with no document ever opened. Subtracted from every other scenario, and compared against a plain launch with the runner switched off to price the runner itself._

3 run(s), 0 step failure(s), 0 boundary(ies) with no outer sample.

| step | op | wall ms | footprint MB | rss MB | committed MB | live MB | frag MB | tiles MB | sp cache MB | gen2 | ui stall ms | ok |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| baseline | baseline | 0.0 | 112.0 | 159.0 | 11.6 | 24.3 | 3.8 | 0.0 | 0.0 | 0 | 0 | yes |
| launched | mark | 0.0 | 112.0 | 159.0 | 11.6 | 24.3 | 3.8 | 0.0 | 0.0 | 0 | 2 | yes |
| idle-after-launch | waitidle | 500.3 | 110.0 | 159.0 | 11.6 | 24.4 | 3.8 | 0.0 | 0.0 | 0 | 2 | yes |
| idle-10s | idle | 10005.4 | 106.0 | 153.0 | 11.6 | 24.6 | 3.8 | 0.0 | 0.0 | 0 | 2 | yes |

UI stall (the in-app heartbeat's worst gap past its 50 ms interval, per boundary): worst per run, median over 3 run(s) 3 ms (min 2, max 3); boundaries with a stall of 100 ms or more, median 0 of 4; the first run's worst is at `idle-10s`. No floor is known for this metric yet (FLOOR-UNKNOWN), so a difference is a lead, not a verdict.

Footprint: baseline 112.0 MB -> peak 112.0 MB -> final 106.0 MB (**-6.0 MB above baseline at the end**).

Render/trim metrics (from the #1491 sink): bandRenders=0, cacheTrimRequests=0, heapReclaims=0, singleRenders=0

### `plans-open`

_Open to settled for a vector-heavy drawing (20 copies of a dense CAD page: thousands of paths per page), the axis where the path rasteriser, not image decode, is the cost; then scroll and a 3x zoom into the dense linework. Caveat: the 20 pages share ONE content stream, so a cache keyed by object may render pages 2-20 cheaper than a real 20-page drawing._

3 run(s), 0 step failure(s), 0 boundary(ies) with no outer sample.

| step | op | wall ms | footprint MB | rss MB | committed MB | live MB | frag MB | tiles MB | sp cache MB | gen2 | ui stall ms | ok |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| baseline | baseline | 0.0 | 112.0 | 159.0 | 11.6 | 24.3 | 3.8 | 0.0 | 0.0 | 0 | 0 | yes |
| pre-open | mark | 0.0 | 112.0 | 159.0 | 11.6 | 24.3 | 3.8 | 0.0 | 0.0 | 0 | 2 | yes |
| open-plans | open | 16.3 | 304.0 | 287.0 | 11.6 | 29.7 | 3.8 | 0.0 | 0.0 | 0 | 2 | yes |
| view-continuous | viewmode | 0.0 | 292.0 | 272.0 | 49.2 | 51.7 | 11.1 | 22.2 | 0.0 | 5 | 2 | yes |
| idle-after-open | waitidle | 642.2 | 283.0 | 265.0 | 53.5 | 58.5 | 7.1 | 22.2 | 0.0 | 8 | 2 | yes |
| scroll-10 | scrollpages | 2173.1 | 338.0 | 357.0 | 33.7 | 49.5 | 11.2 | 127.9 | 0.0 | 28 | 5 | yes |
| idle-after-scroll | waitidle | 502.0 | 338.0 | 357.0 | 37.5 | 47.4 | 7.6 | 133.4 | 0.0 | 28 | 2 | yes |
| zoom-300 | zoom | 1.3 | 419.0 | 460.0 | 37.5 | 47.5 | 7.6 | 133.4 | 0.0 | 28 | 2 | yes |
| idle-zoom-300 | waitidle | 1024.0 | 421.0 | 425.0 | 38.0 | 38.0 | 7.2 | 188.8 | 0.0 | 32 | 2 | yes |
| idle-10s | idle | 10010.1 | 343.0 | 425.0 | 38.0 | 38.2 | 7.2 | 188.8 | 0.0 | 32 | 2 | yes |

UI stall (the in-app heartbeat's worst gap past its 50 ms interval, per boundary): worst per run, median over 3 run(s) 5 ms (min 3, max 8); boundaries with a stall of 100 ms or more, median 0 of 10; the first run's worst is at `scroll-10`. No floor is known for this metric yet (FLOOR-UNKNOWN), so a difference is a lead, not a verdict.

Footprint: baseline 112.0 MB -> peak 421.0 MB -> final 343.0 MB (**231.0 MB above baseline at the end**).

Render/trim metrics (from the #1491 sink): bandRenderP50Ms=123.2, bandRenderP99Ms=435.7, bandRenders=8, cacheTrimRequests=0, heapReclaims=0, singleRenders=0

### `scan-scroll-close`

_The common real-world scanned document (20 pages, one 200 dpi JPEG each, no text): open, scroll it all, wait through the app's 30 s idle trim, close, wait again. Written for #2072, where the NativeAOT build kept ~425 MB after the idle trim and after close while ReadyToRun returned to ~250 MB; compare idle-after-trim and idle-after-close between build shapes._

3 run(s), 0 step failure(s), 0 boundary(ies) with no outer sample.

| step | op | wall ms | footprint MB | rss MB | committed MB | live MB | frag MB | tiles MB | sp cache MB | gen2 | ui stall ms | ok |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| baseline | baseline | 0.0 | 112.0 | 159.0 | 11.6 | 24.3 | 3.8 | 0.0 | 0.0 | 0 | 0 | yes |
| pre-open | mark | 0.0 | 112.0 | 159.0 | 11.6 | 24.3 | 3.8 | 0.0 | 0.0 | 0 | 2 | yes |
| open-scan | open | 13.3 | 246.0 | 223.0 | 11.6 | 25.8 | 3.8 | 0.0 | 0.0 | 0 | 2 | yes |
| view-continuous | viewmode | 0.0 | 246.0 | 223.0 | 25.9 | 26.9 | 10.2 | 20.6 | 0.0 | 2 | 2 | yes |
| idle-after-open | waitidle | 625.7 | 263.0 | 239.0 | 31.7 | 34.9 | 8.0 | 20.6 | 0.0 | 3 | 2 | yes |
| scroll-20 | scrollpages | 3834.6 | 422.0 | 413.0 | 31.7 | 34.6 | 8.4 | 199.2 | 0.0 | 31 | 7 | yes |
| idle-after-scroll | waitidle | 509.9 | 350.0 | 413.0 | 31.7 | 34.7 | 8.4 | 199.2 | 0.0 | 31 | 2 | yes |
| idle-after-trim-45s | idle | 45002.7 | 172.0 | 411.0 | 30.0 | 34.5 | 6.5 | 10.3 | 0.0 | 32 | 34 | yes |
| close-scan | close | 18.4 | 216.0 | 381.0 | 30.0 | 35.8 | 6.5 | 0.0 | 0.0 | 32 | 2 | yes |
| idle-after-close | waitidle | 487.5 | 216.0 | 381.0 | 20.1 | 23.8 | 6.9 | 0.0 | 0.0 | 33 | 2 | yes |
| idle-after-close-45s | idle | 45002.3 | 135.0 | 381.0 | 20.1 | 23.6 | 6.9 | 0.0 | 0.0 | 34 | 3 | yes |

UI stall (the in-app heartbeat's worst gap past its 50 ms interval, per boundary): worst per run, median over 3 run(s) 30 ms (min 24, max 34); boundaries with a stall of 100 ms or more, median 0 of 11; the first run's worst is at `idle-after-trim-45s`. No floor is known for this metric yet (FLOOR-UNKNOWN), so a difference is a lead, not a verdict.

Footprint: baseline 112.0 MB -> peak 422.0 MB -> final 135.0 MB (**23.0 MB above baseline at the end**).

Render/trim metrics (from the #1491 sink): bandRenderP50Ms=18.8, bandRenderP99Ms=22.3, bandRenders=10, cacheTrimRequests=2.0, heapReclaimP50Ms=29.0, heapReclaims=3, singleRenders=0

### `soft-trim-cycle`

_#1478 and #1496 together: trim at Warn then Critical on a scrolled Altona, then scroll back. The trim is requested IN PROCESS through ViewerCacheTrimCoordinator, which is what removes `sudo memory_pressure` (and therefore the human) from the loop. The scroll-back step is there because #1496's own risk list names re-inflation churn: freed samples decode again._

3 run(s), 0 step failure(s), 0 boundary(ies) with no outer sample.

| step | op | wall ms | footprint MB | rss MB | committed MB | live MB | frag MB | tiles MB | sp cache MB | gen2 | ui stall ms | ok |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| baseline | baseline | 0.0 | 112.0 | 159.0 | 11.6 | 24.6 | 4.0 | 0.0 | 0.0 | 0 | 0 | yes |
| pre-open | mark | 0.0 | 113.0 | 160.0 | 11.6 | 24.7 | 4.0 | 0.0 | 0.0 | 0 | 2 | yes |
| open-altona | open | 38.9 | 384.0 | 388.0 | 20.1 | 42.6 | 2.6 | 0.0 | 0.0 | 0 | 2 | yes |
| view-continuous | viewmode | 0.0 | 432.0 | 402.0 | 202.5 | 323.1 | 12.8 | 2.0 | 0.0 | 5 | 2 | yes |
| idle-after-open | waitidle | 911.9 | 524.0 | 499.0 | 334.5 | 261.0 | 89.6 | 2.5 | 0.0 | 7 | 2 | yes |
| scroll-17 | scrollpages | 758.8 | 557.0 | 536.0 | 347.5 | 288.3 | 92.4 | 11.4 | 0.0 | 11 | 4 | yes |
| idle-after-scroll | waitidle | 514.4 | 557.0 | 536.0 | 347.5 | 288.4 | 92.4 | 11.4 | 0.0 | 11 | 2 | yes |
| trim-background | trim | 0.4 | 228.0 | 280.0 | 347.5 | 288.4 | 92.4 | 4.4 | 0.0 | 11 | 2 | yes |
| idle-after-background-trim | waitidle | 502.3 | 225.0 | 280.0 | 94.1 | 97.2 | 0.2 | 4.4 | 0.0 | 12 | 2 | yes |
| trim-warn | trim | 0.3 | 225.0 | 280.0 | 94.1 | 97.2 | 0.2 | 4.4 | 0.0 | 12 | 2 | yes |
| idle-after-warn-trim | waitidle | 498.8 | 225.0 | 280.0 | 94.1 | 97.2 | 0.2 | 4.4 | 0.0 | 13 | 2 | yes |
| trim-critical | trim | 0.6 | 223.0 | 280.0 | 94.1 | 97.3 | 0.2 | 0.0 | 0.0 | 13 | 2 | yes |
| idle-after-critical-trim | waitidle | 506.0 | 223.0 | 280.0 | 94.0 | 97.1 | 0.1 | 0.0 | 0.0 | 14 | 3 | yes |
| scroll-back-17 | scrollpages | 761.2 | 660.0 | 643.0 | 458.0 | 348.0 | 158.1 | 7.5 | 0.0 | 20 | 3 | yes |
| idle-after-scroll-back | waitidle | 506.2 | 660.0 | 643.0 | 458.0 | 348.1 | 158.1 | 7.5 | 0.0 | 20 | 2 | yes |
| idle-20s | idle | 20009.9 | 581.0 | 643.0 | 458.0 | 348.5 | 158.1 | 7.5 | 0.0 | 20 | 3 | yes |

UI stall (the in-app heartbeat's worst gap past its 50 ms interval, per boundary): worst per run, median over 3 run(s) 9 ms (min 4, max 9); boundaries with a stall of 100 ms or more, median 0 of 16; the first run's worst is at `scroll-17`. No floor is known for this metric yet (FLOOR-UNKNOWN), so a difference is a lead, not a verdict.

Footprint: baseline 112.0 MB -> peak 660.0 MB -> final 581.0 MB (**469.0 MB above baseline at the end**).

Render/trim metrics (from the #1491 sink): bandRenderP50Ms=73.9, bandRenderP99Ms=291.3, bandRenders=31, cacheTrimRequests=3.0, heapReclaimP50Ms=24.0, heapReclaims=3, singleRenders=0

### `w9-launch-idle`

_The fixed floor: a one-page form with nothing heavy in it. #1496 measured 336 MB footprint / 120 MB committed heap here, and every other number in the family is read against it._

3 run(s), 0 step failure(s), 0 boundary(ies) with no outer sample.

| step | op | wall ms | footprint MB | rss MB | committed MB | live MB | frag MB | tiles MB | sp cache MB | gen2 | ui stall ms | ok |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| baseline | baseline | 0.0 | 112.0 | 159.0 | 11.8 | 24.2 | 3.8 | 0.0 | 0.0 | 0 | 0 | yes |
| pre-open | mark | 0.0 | 112.0 | 159.0 | 11.8 | 24.2 | 3.8 | 0.0 | 0.0 | 0 | 2 | yes |
| open-w9 | open | 45.3 | 273.0 | 238.0 | 20.9 | 33.8 | 2.6 | 0.0 | 0.0 | 0 | 9 | yes |
| view-continuous | viewmode | 0.0 | 261.0 | 228.0 | 24.4 | 47.7 | 4.0 | 20.6 | 0.0 | 1 | 2 | yes |
| idle-after-open | waitidle | 768.5 | 270.0 | 240.0 | 32.9 | 49.0 | 0.5 | 20.6 | 0.0 | 1 | 10 | yes |
| idle-20s | idle | 20006.2 | 191.0 | 233.0 | 32.9 | 49.4 | 0.5 | 20.6 | 0.0 | 1 | 3 | yes |

UI stall (the in-app heartbeat's worst gap past its 50 ms interval, per boundary): worst per run, median over 3 run(s) 10 ms (min 8, max 12); boundaries with a stall of 100 ms or more, median 0 of 6; the first run's worst is at `idle-after-open`. No floor is known for this metric yet (FLOOR-UNKNOWN), so a difference is a lead, not a verdict.

Footprint: baseline 112.0 MB -> peak 273.0 MB -> final 191.0 MB (**79.0 MB above baseline at the end**).

Render/trim metrics (from the #1491 sink): bandRenders=1, cacheTrimRequests=0, heapReclaims=0, singleRenders=0

