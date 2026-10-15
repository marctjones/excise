# After the #2069, #2070 and #1626 merges, 2026-10-15

Build: develop @ 415ae97 (three perf branches merged, t0 30/30, t1 107/107), Release R2R `.app` bundle.
**Build shape: ReadyToRun, NOT NativeAOT.** Every GUI figure here is the default `.app` bundle (ReadyToRun, tiered compilation); the command-line figures are a plain Release `dotnet build` (JIT); test tiers run Debug. On the same commit NativeAOT rests about 80 MB lower and uses 55-70% less CPU than ReadyToRun (`docs/performance-baselines/2026-10-15-aot-vs-r2r/`), so none of these numbers can be compared with NativeAOT numbers.
Before: `docs/performance-baselines/2026-10-14-reader-comparison/` (develop @ dcd0a2e9, same harnesses, same
documents and repeats). Only excise and Preview (drift control) were rerun; Preview is unchanged code, so its
movement is the measurement drift. #2071 (trim releases band-page samples) is NOT in this build.
Every stage here ran quiet (load1 1.3-1.9, no Godot); see `env/LOAD-*.txt`. The 2026-10-14 CLI "before" ran at load 3.7,
so CLI times are judged against mutool in the same run, not against the earlier absolute times.

## Verdict per fix

| issue | verdict | evidence |
|---|---|---|
| #2070 decode CPU | **confirmed** | Altona scroll 24 -> 49 fps (Preview control 47 -> 42); band render p50 213 -> 101 ms; scenario CPU -20..-24% against noise of +-0.06-0.2 s; CLI `acc-compensation-cover` wall 3.5x -> 2.4x mutool and CPU 13.7x -> 4.9x. Text-only scenarios (irs) unchanged, as expected. Band p99 unchanged (623 -> 601 ms), as the investigator predicted. |
| #1626 allocations | **confirmed** | exact allocation, quiet machine: save-roundtrip 942 -> 768 MB (budget PASS), text-extract 352 -> 276, search 250 -> 185, redaction-save 3138 -> 2666 (still over its 1213 budget). |
| #2069 page-turn latency | **small real gain** (interleaved A/B, see the A/B section; the first comparison here crossed two sessions and was not conclusive) | first-change p50 with real keys: scan 61 -> 54 ms (5 of 5 paired repeats lower), irs 59 -> 54 ms (3 of 5). The turn is still ~54 ms. |
| #2071 trim memory | **confirmed** (A/B section) | Altona idle-30s footprint 493 -> 335 MB (spread 4 and 1); unchanged after close; irs unchanged; CPU unchanged. |

## Responsiveness (screen-probe, real key events; ms from input)

| doc | app | first change p50 / p95 | End drawn | Home drawn | scroll fps | hitches | launch -> drawn |
|---|---|---|---:|---:|---:|---:|---|
| irs | excise before | 56 / 91 | 97 | 57 | 48 | 0 | 839 / 3052 |
| irs | excise after (n=1) | 65 / 82 | 73 | 75 | 49 | 1 | 687 / 2885 |
| irs | preview before / after | 25 / 55 -> 41 / 70 | 35 -> 43 | 667 -> 633 | 24 -> 42 | 0 -> 18 | 647 / 1539 -> 611 / 1483 |
| altona | excise before | 56 / 85 | 66 | 38 | 24 | 0 | 706 / 2500 |
| altona | excise after | 40 / 79 | 59 | 63 | 49 | 0 | 617 / 3190 |
| altona | preview before / after | 30 / 55 -> 39 / 75 | 604 -> 612 | 260 -> 262 | 47 -> 42 | 0 -> 22 | 731 / 1627 -> 705 / 1587 |
| scan | excise before | 56 / 79 | 44 | 55 | 48 | 0 | 704 / 2438 |
| scan | excise after | 63 / 77 | 72 | 68 | 49 | 0 | 623 / 1363 |
| scan | preview before / after | 38 / 61 -> 41 / 69 | 120 -> 123 | 69 -> 73 | 49 -> 42 | 1 -> 22 | 557 / 3212 -> 528 / 1434 |

- excise irs has one run: r1 aborted on a System Events error (`excise irs r1`), so that row is n=1.
- Preview's hitches went from about 0 to 18-22 between the two runs on unchanged code, so frame-pacing metrics
  were not measured under the same conditions; treat fps and hitch deltas with that in mind. The Altona scroll
  fps gain (24 -> 49) is large enough to stand regardless; the page-turn p50 deltas are not.
- Why #2069 is not confirmed: the bundle contains `RenderLoopWarmth` (checked in `Excise.Avalonia.dll`) and the
  bench's 1.5 s turn gap is inside the 3 s hold, yet real-key turns did not get faster on irs and scan. The
  investigator measured with an in-process PageDown plus screen-probe; whether real key events reach the viewer's
  handler (rather than a window-level binding) is untested. Follow-up: an interleaved A/B of the baseline bundle
  and this bundle, 5 repeats per arm on irs and scan, so drift cancels.

## Memory and CPU (reader bench, 2 repeats, footprint MB; before -> after)

| doc | app | opened | paged-30 | idle-30s | closed-45s | CPU s | idle CPU % |
|---|---|---|---|---|---|---|---|
| empty | excise | 204 -> 203 | - | 200 -> 200 | - | 0.6 -> 0.6 | 0.25 -> 0.25 |
| w9 | excise | 326 -> 326 | 388 -> 396 | 388 -> 388 | 322 -> 348 | 4.4 -> 4.2 | 0.15 -> 0.15 |
| irs | excise | 377 -> 376 | 609 -> 601 | 588 -> 582 | 484 -> 414 | 7.3 -> 7.3 | 0.20 -> 0.22 |
| altona | excise | 551 -> 534 | 676 -> 702 | 497 -> 493 | 323 -> 320 | 8.0 -> 6.9 | 0.18 -> 0.18 |
| scan | excise | 323 -> 323 | 542 -> 534 | 462 -> 349 | 252 -> 249 | 3.7 -> 3.7 | 0.12 -> 0.13 |
| (control) preview, all docs | | within 4 MB | within 9 MB | within 21 MB | within 21 MB | within 0.5 s | 0.02-0.10, unchanged |

No memory change is expected (the memory fix is #2071, not merged), and none is seen beyond noise. The scan idle-30s
(462 -> 349) and irs closed-45s (484 -> 414) moves are within the run-to-run spread on those rows and are not credited.
Idle CPU is unchanged, so #2069's 3 s render-loop hold costs nothing visible at rest. Altona CPU 8.0 -> 6.9 s is #2070.

## In-app scenarios (3 repeats, 0 step failures; before -> after)

| scenario | peak MB | final MB | band p50 / p99 ms | CPU total s (noise) |
|---|---|---|---|---|
| altona-scroll | 729 -> 711 | 418 -> 425 | 213 -> 101 / 623 -> 601 | 7.2 -> 5.5 (+-0.06) |
| altona-close | 726 -> 702 | 314 -> 305 | 208 -> 104 / 658 -> 643 | 7.5 -> 5.8 (+-0.09) |
| altona-replace-w9 | 721 -> 705 | 374 -> 371 | 136 -> 81 / 617 -> 623 | 8.1 -> 6.5 (+-0.2) |
| soft-trim-cycle | 832 -> 845 | 755 -> 779 | 132 -> 103 / 637 -> 645 | 9.3 -> 7.6 (+-0.37) |
| irs-page30 | 536 -> 548 | 458 -> 475 | - | 4.8 -> 4.8 |
| irs-search-redact | 484 -> 469 | 411 -> 391 | - | 3.3 -> 3.3 |
| irs-zoom | 563 -> 562 | 491 -> 483 | 16 -> 15 / 26 -> 24 | 3.5 -> 3.5 |

## Engine (fresh process per run; Release JIT build output)

Excise wall and CPU against mutool in the same run (mutool also ran about 15% faster in this run: drift):
`acc-compensation-cover` 900 -> 480 ms wall (3.5x -> 2.4x mutool), CPU 3065 -> 848 ms (13.7x -> 4.9x). Every other
fixture's ratio to mutool is unchanged within 0.3x, and RSS is unchanged. perf budgets (exact allocation) are in the
verdict table above; `cli/perf-budgets.log`.

## Left open

- #2069: the render-loop hold is a small real gain; why the turn is still ~54 ms (Preview 25, Firefox 22-31) is under investigation (real-key latency budget).
- #2071: merged and pushed (d48ec362); measured in the A/B section. Not measured: the first-zoom re-decode cost after a background trim (the bench does not zoom).
- #1626: re-baseline the budgets (text-extract's 219 MB does not reproduce at its own commit); redaction-save is
  still 2.2x its budget for causes the investigator attributed to later, legitimate changes.
- Not rerun: Chrome (the user's own Chrome was open), Firefox, Acrobat.

## Interleaved A/B, same session (2026-10-15; every run quiet: load1 1.3-2.0, no Godot; `ab/LOAD.txt`)

Bundles: base = dcd0a2e9 (before), after = 415ae97 (#2069, #2070, #1626), final = d48ec362 (adds #2071). excise only,
real keys, runs alternated ABBA so drift and ordering cancel. Raw per-run summaries are in `ab/`.

**#2069, base vs after (5 repeats per arm, speed bench, first-change p50 per run):**
scan base 69 66 59 60 61 -> after 55 54 54 56 51 ms (paired -14 -12 -5 -4 -10; median 61 -> 54); irs base 57 61 59 53 62 ->
after 60 57 54 53 52 (paired +3 -4 -5 0 -10; median 59 -> 54, ranges overlap). End jump, Home and scroll fps unchanged (48-49 fps).
The hold is a real but small gain (5-10 ms) and the turn still takes ~54 ms.

**#2071, after vs final (3 repeats per arm, reader bench, footprint MB):**

| doc | column | without #2071 | with #2071 | change |
|---|---|---|---|---|
| altona | idle-30s | 493 (492 493 496) | 335 (335 336 335) | **-158** |
| altona | closed-45s | 319 | 320 | +1 |
| altona | paged-30 / peak | 782 / 905 (spread 76 / 39) | 757 / 863 (spread 148 / 63) | -25 / -42, within spread, not credited |
| scan | paged-30 / peak | 541 / 627 (spread 2 / 2) | 526 / 612 (spread 1 / 5) | -15 / -15 |
| scan | idle-30s | 352 | 351 | -1 |
| irs | every column | | | within spread (idle-30s 599 -> 595; closed-45s is bimodal 351/480 in both arms) |

CPU seconds (altona 7.4 -> 7.3, irs 7.4 -> 7.3, scan 3.7 -> 3.8) and idle CPU (0.1-0.2%) are unchanged. The bench does not
zoom, so the first-zoom re-decode cost after a background trim (about one band render, ~190 ms CPU) is not measured here.

## Decisions after the page-turn investigation (2026-10-15)

Page turns are 54 ms warm and 95 ms on the first turn of a process: under the ~100 ms threshold for feeling instant, so
everything below is a nice-to-have and was judged on complexity and upkeep against a gain most people cannot feel.

- **Render-loop hold (#2069): removed.** 5-7 ms for a 107-line custom composition visual that depends on how Avalonia
  12.1.2 stops its display link, plus 60 Hz ticking for 3 s after every input.
- **OpenGL backend instead of Metal: not merged** (branch `perf/2069-latency`, d8735ad6). -11 to -15 ms on every turn, but
  OpenGL is deprecated on macOS, scroll ran 1-3 fps slower in single runs, about 1% of pixels differ by 1-8 levels, and window
  resize, multi-document windows and the Windows and Linux builds were not measured.
- **JIT warm-up of the ReactiveCommand pipeline: not merged** (branch `perf/2069-slowest-turn`, 97e929ae). First turn 98 -> 80 ms
  once per launch, for a startup hook that does nothing under a Native AOT build and ~12 ms of JIT at startup otherwise.
- **Where the 54 ms goes (real key, warm, irs):** OS delivery 4, window-sees-it to commit 6, wait for the display tick 9.5,
  render and present call 5, rendered to first screen change 34 (Metal present plus WindowServer; ~25 of it is the floor every
  reader pays). First turn of a process adds ~17 ms of key delivery (WindowServer, outside the app), ~15 ms of first-run JIT, and
  ~10 ms of render-loop restart. Turns after more than 3 s idle pay the restart (+10-12 ms); the idle trim causes no re-render.
- **Not pursued:** deferring non-visual bindings (~1-2 ms), avoiding a relayout on page turn (a redesign), rendering at commit
  (private Avalonia API).
