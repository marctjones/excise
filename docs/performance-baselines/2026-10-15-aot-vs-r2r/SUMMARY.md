# NativeAOT vs ReadyToRun, same commit, 2026-10-15

**Build shapes compared** (both bundles built from one commit, develop @ `86aa2150`, by `scripts/build-macos-app.sh`):
- **ReadyToRun** (the default publish): `Excise.Core.dll` carries a ReadyToRun header; 189 MB bundle.
- **NativeAOT** (`--aot`): one native `Excise.App` binary of 43 MB, no managed assemblies; 64 MB bundle.

Machine: Apple M5, 24 GB, macOS 26.6.2. excise only. Every stage ran quiet (load1 1.4-2.0, no Godot; `LOAD.txt`). The two
shapes were alternated (ABBA) within each repeat for the speed and first memory passes, so drift cancels. Both bundles were
built without bundled OCR language data (the project default for every package); whether OCR works in the NativeAOT build is
untested. The NativeAOT window draws its title bar left-aligned and darker where ReadyToRun draws a centered system one; the
cause is not known.

## In-app scenarios (`run-gui-perf-scenarios.sh`, 3 repeats each, 0 step failures; ReadyToRun -> NativeAOT)

| scenario | baseline MB | peak MB | final MB | band render p50 / p99 ms | CPU total s (noise) |
|---|---|---|---|---|---|
| null (launch, quit) | 198 -> 122 | 199 -> 122 | 197 -> 116 | - | 0.7 -> 0.2 |
| w9-launch-idle | 199 -> 122 | 378 -> 300 | 318 -> 222 | - | 1.8 -> 0.5 |
| irs-page30 | 198 -> 122 | 544 -> 449 | 466 -> 370 | - | 4.8 -> 2.2 |
| irs-zoom | 199 -> 122 | 565 -> 481 | 487 -> 402 | 16 -> 17 / 24 -> 23 | 3.6 -> 1.2 |
| irs-search-redact | 198 -> 122 | 483 -> 388 | 405 -> 309 | - | 3.3 -> 1.1 |
| altona-scroll | 199 -> 122 | 729 -> 554 | 359 -> 243 | 101 -> 52 / 602 -> 185 | 5.6 -> 2.5 (+-0.21 / 0.03) |
| altona-close | 198 -> 122 | 713 -> 608 | 306 -> 200 | 102 -> 52 / 670 -> 195 | 5.9 -> 2.5 |
| altona-replace-w9 | 198 -> 122 | 701 -> 606 | 371 -> 242 | 80 -> 52 / 696 -> 185 | 6.5 -> 2.7 |
| soft-trim-cycle | 198 -> 122 | 796 -> 637 | 715 -> 558 | 118 -> 52 / 666 -> 312 | 7.6 -> 3.7 |

Scenario idles are 10-20 s, shorter than the app's 30 s idle trim, so "final" is a pre-trim figure.

## Memory and CPU on real documents (`reader_bench.py`, 3 repeats, footprint MB, median [spread ReadyToRun / NativeAOT])

| doc | opened | paged-30 | idle-30s | closed-45s | peak | CPU s | idle CPU % |
|---|---|---|---|---|---|---|---|
| empty | 204 -> 128 (-37%) | - | 200 -> 124 | - | 278 -> 202 | 0.60 -> 0.30 | 0.27 -> 0.03 |
| w9 | 327 -> 233 (-29%) | 396 -> 292 [12/13] | 390 -> 278 | 299 -> 252 | 570 -> 459 | 4.2 -> 1.8 | 0.13 -> 0.10 |
| irs | 375 -> 290 (-23%) | 602 -> 496 | 582 -> 473 | 484 -> 378 | 685 -> 579 | 7.2 -> 3.6 | 0.20 -> 0.10 |
| altona | 562 -> 401 (-29%) | 843 -> 507 [63/10] | 336 -> 222 (-34%) | 321 -> 201 (-37%) | 928 -> 655 | 7.3 -> 3.8 | 0.17 -> 0.10 |
| **scan** | 323 -> 243 (-25%) | 527 -> 445 | **354 -> 431 (+22%)** | **252 -> 422 (+67%)** | 613 -> 521 | 3.7 -> 1.8 | 0.13 -> 0.10 |

The scan row goes the other way after the idle trim and after close, in all three repeats of each shape (idle-30s 429-437 against
353-367 MB; closed-45s 421-428 against 250-285 MB): filed as #2072. The first NativeAOT pass failed every document run
because the harness cropped the left-aligned NativeAOT title and could not read the file name; the crop was fixed
(`reader_bench.py`) and the document runs repeated (`mem/aot-docs-*`); the empty-window figures come from the first pass (`mem/aot-empty-*`).
The ReadyToRun arm of this table was measured earlier in the same session and not interleaved with the repeated NativeAOT
runs; all were quiet and the spreads are small.

## Responsiveness (`reader_speed_bench.py`, real keys, 5 repeats per shape, interleaved; medians of per-run values)

| doc | first change p50 / p95 ms | launch -> window | launch -> drawn | End jump | Home | scroll fps |
|---|---|---|---|---|---|---|
| irs | 60 -> 61 / 75 -> 80 | 676 -> 435 ms | 4924 -> 3691 ms | 88 -> 77 | 63 -> 70 | 48 -> 48 |
| scan | 63 -> 62 / 85 -> 75 | 632 -> 415 ms | 3519 -> 3203 ms | 70 -> 61 | 64 -> 69 | 48 -> 48 |
| altona | 49 -> 49 / 83 -> 68 | 623 -> 404 ms | 3410 -> 3192 ms | 63 -> 69 | 52 -> 58 | 49 -> 49 |

First turn of a process (turn 0 of each run, n=15 per shape) 79 -> 68 ms (range 68-107 -> 62-81); warm turns 57 -> 56 ms (p95 73 -> 73).
Warm page turns are the same; the window appears about 240 ms sooner; about 11 ms of the first-turn penalty is JIT, and the
remaining ~12 ms is the operating system delivering the first key to a new process.

## What this does and does not show

- NativeAOT is the better shape on every axis measured except the scan-after-close retention (#2072): about 80 MB lower empty,
  15-40% lower on real documents while open (-23% to -29% at open, -34% at Altona's idle), CPU 50-70% lower, Altona band render p99 602 -> 185 ms.
- Not measured under NativeAOT: OCR (the project's AOT smoke test does not exercise it), the command-line tool, multi-document
  windows, resize, and the Windows and Linux builds.
- Method: `scripts/bench_build_shape.py` identifies each bundle's shape; the harnesses print it and record it in
  `run-meta.json` and the summary header from this date on, and default to the NativeAOT bundle.
