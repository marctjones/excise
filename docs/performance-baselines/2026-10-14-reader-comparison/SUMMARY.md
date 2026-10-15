# Reader comparison and engine baseline, 2026-10-14

Candidate: `dcd0a2e9` (develop), Release R2R `.app` bundle built from it (`scripts/build-macos-app.sh`).
**Build shape: ReadyToRun, NOT NativeAOT.** Every GUI figure here is the default `.app` bundle (ReadyToRun, tiered compilation); the command-line figures are a plain Release `dotnet build` (JIT); test tiers run Debug. On the same commit NativeAOT rests about 80 MB lower and uses 55-70% less CPU than ReadyToRun (`docs/performance-baselines/2026-10-15-aot-vs-r2r/`), so none of these numbers can be compared with NativeAOT numbers.
Machine: Apple M5, 24 GB, macOS 26.6.2. Readers: Preview 11.0, Acrobat 26.002.22008, Firefox 157.0 (pdf.js).
Chrome is not in these tables: the user's own Chrome was open and the harness refuses to run beside it.
Harnesses: `scripts/reader_bench.py` (memory/CPU), `scripts/reader_speed_bench.py` (responsiveness),
`scripts/run-gui-perf-scenarios.sh` (excise only), `scripts/check-perf-budgets.sh`,
`scripts/run-reference-performance-bench.sh`. Firefox was added to the first two for this run.

Load at start of each stage is in `env/LOAD-*.txt`. Stages in this directory ran **quiet**
(load1 < 2.0, no Godot) except the CLI stage (load1 3.7, tagged LOADED; ratios, not absolute ms, are the result).
A first pass under load (load1 4.6-7) was discarded; its numbers differ most on idle CPU and Altona footprint.

## Memory and CPU (footprint MB summed over each app's process tree; 2 repeats, medians)

| doc | app | opened | paged-30 | idle-30s | closed-idle-45s | peak (all) | CPU s | idle CPU % |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| empty | excise | 204 | - | 200 | - | 278 | 0.6 | 0.25 |
| empty | preview | 54 | - | 54 | - | 55 | 0.8 | 0.03 |
| empty | firefox | 803 | - | 984 | - | 1030 | 10.2 | 19.29 (see notes) |
| empty | acrobat | 1014 | - | 979 | - | 1156 | 6.5 | 1.24 |
| w9 (6 p) | excise | 326 | 388 | 388 | 322 | 579 | 4.4 | 0.15 |
| w9 | preview | 243 | 196 | 150 | 150 | 344 | 11.1 | 0.07 |
| w9 | firefox | 984 | 1118 | 1231 | 871 | 1379 | 11.4 | 0.65 |
| w9 | acrobat | 1183 | 1161 | 1157 | 1050 | 1495 | 9.5 | 1.36 |
| irs (126 p) | excise | 377 | 609 | 588 | 484 | 691 | 7.3 | 0.20 |
| irs | preview | 207 | 321 | 249 | 200 | 445 | 49.1 | 0.10 |
| irs | firefox | 1246 | 1712 | 1800 | 1007 | 2149 | 15.2 | 0.86 |
| irs | acrobat | 1230 | 1281 | 1381 | 1236 | 1497 | 10.3 | 0.93 |
| altona (17 p, 122 MB) | excise | 551 | 676 | 497 | 323 | 798 | 8.0 | 0.18 |
| altona | preview | 221 | 293 | 227 | 180 | 359 | 14.3 | 0.07 |
| altona | firefox | 1657 | 2423 | 1733 | 867 | 2828 | 15.6 | 0.48 |
| altona | acrobat | 1902 | 1981 | 1781 | 1545 | 2098 | 15.2 | 1.06 |
| scan (20 p) | excise | 323 | 542 | 462 | 252 | 622 | 3.7 | 0.12 |
| scan | preview | 212 | 278 | 176 | 170 | 389 | 28.1 | 0.07 |
| scan | firefox | 1003 | 1835 | 1649 | 867 | 2136 | 11.9 | 0.89 |
| scan | acrobat | 1568 | 1558 | 1549 | 1446 | 1824 | 9.5 | 0.98 |

excise vs Preview, idle-30s after paging: w9 2.6x, irs 2.4x, altona 2.2x, scan 2.6x; empty window 3.7x.
excise vs Acrobat: 2.3-4.9x lower memory (idle-30s), 4.6-9x lower idle CPU. The agreed bar (beat Acrobat on memory and idle
CPU, within 2-3x of Preview) holds on every document here.

## Responsiveness (screen-probe, quiet; ms from input event; page area only)

| doc | app | launch->drawn | turn first change p50/p95 | no-response turns | End jump drawn | Home drawn | scroll fps | hitches |
|---|---|---:|---|---:|---:|---:|---:|---:|
| irs | excise | 3052 | 56 / 91 | 0/40 | 97 | 57 | 48 | 0 |
| irs | preview | 1539 | 25 / 55 | 12/40 | 35 | 667 | 24 | 0 |
| irs | firefox | 9631 | 31 / 50 | 5/40 | 56 | 33 | 24 | 1 |
| irs | acrobat | 11538 | 28 / 43 | 0/20 | 65 | 66 | 47 | 3 |
| altona | excise | 2500 | 56 / 85 | 2/32 | 66 | 38 | 24 | 0 |
| altona | preview | 1627 | 30 / 55 | 12/32 | 604 | 260 | 47 | 0 |
| altona | firefox | 9536 | 22 / 46 | 14/32 | 562 | 563 | 49 | 0 |
| altona | acrobat | 9602 | 45 / 187 | 0/32 | 51 | 54 | 8 | 2 |
| scan | excise | 2438 | 56 / 79 | 0/38 | 44 | 55 | 48 | 0 |
| scan | preview | 3212 | 38 / 61 | 0/38 | 120 | 69 | 49 | 1 |
| scan | firefox | 9985 | 26 / 38 | 0/38 | 28 | 24 | 24 | 0 |
| scan | acrobat | 18769 | 30 / 46 | 0/38 | 52 | 44 | 33 | 1 |

Run-validity notes: one Acrobat irs run failed verification and is excluded (n=1 for that row). Firefox altona r1
read page 3 where 17 was expected (keys dropped), irs r2 read 16 for 21; Preview irs r2 read 9 for 21. Those
runs are kept with a note, so Firefox/Preview turn counts on those rows are unreliable. excise altona reads 16
of 17 at the end: the mixed page size document labels the page that dominates the viewport.

## excise in-app scenarios (3 repeats, quiet, 0 step failures)

| scenario | peak MB | final MB | band render p50 / p99 ms | CPU total s (spread) |
|---|---:|---:|---|---:|
| null (launch, quit) | 199 | 197 | - | 0.8 (0.2) |
| w9-launch-idle | 378 | 320 | - | 1.9 (0.3) |
| irs-page30 | 536 | 458 | - | 4.8 (0.07) |
| irs-zoom | 563 | 491 | 15.8 / 25.8 | 3.5 (0.03) |
| irs-search-redact | 484 | 411 | - | 3.3 (0.04) |
| altona-scroll | 729 | 418 | 213 / 623 | 7.2 (0.06) |
| altona-close | 726 | 314 | 208 / 658 | 7.5 (0.09) |
| altona-replace-w9 | 721 | 374 | 136 / 617 | 8.1 (0.2) |
| soft-trim-cycle | 832 | 755 | 132 / 637 | 9.3 (0.4) |

Scenario idles are 10-20 s, shorter than the app's 30 s idle trim, so "final" is pre-trim, not a resting figure.
The empty app baseline is 199 MB footprint.

## Engine, fresh process per run (excise-cli, Release JIT build output, not the R2R/AOT shape)

Full table: `cli/reference-summary.md`. Gate: PASS (42 runs). Wall vs mutool 0.8-4.3x, CPU vs mutool 1.1-13.7x,
RSS vs mutool 0.4-6.0x (lower only on the two large-image fixtures). Startup is ~55-70 ms per process in this shape.

perf budgets (budgets captured 2026-07-25, #1626): redaction-save 3155 ms / 3138 MB allocated vs 1127 ms / 1213 MB (FAIL),
text-extract 654 ms / 352 MB vs 532 ms / 219 MB (FAIL), search 274 ms / 250 MB vs 32 ms / 22 MB (WARN),
save-roundtrip 901 ms / 942 MB vs 801 ms / 804 MB (WARN). Allocation is identical at 41fd61f3, 462a6122 and 2d634f41
(`cli/search-bisect-logs`), so the case-fold, accent and overprint work did not move it.

## Hotspot candidates, ranked (none is below the measured noise)

1. Page-turn response is a flat 56 ms on three documents (first change equals drawn), against 22-45 ms in the
   other readers. A fixed delay, not render cost: look at input-to-render scheduling before render speed.
2. Resting footprint after paging is 2.2-2.6x Preview; the empty window alone is 200 MB against Preview's 54.
   soft-trim-cycle ends 556 MB above the empty baseline after scrolling back (re-inflation after a trim).
3. `acc-compensation-cover` uses 3.1 s CPU for 0.9 s wall (13.7x mutool CPU); `multilingual-cjk` 4.6x. Parallel work
   that is not paying off. One `sample <pid>` of the CLI on a quiet machine would show where.
4. Allocation budgets (above) are 1.6-2.6x on redaction-save and text-extract, 11x on search, all pre-existing.
5. Launch to fully drawn is 2.4-3.1 s against Preview's 1.5-3.2 s (faster than Firefox and Acrobat).

## Method notes

- Firefox adapter: fresh `-profile` with a seeded `user.js` and `-no-remote`; `n` pages; the page box is read by OCR
  with a pixel probe for a first-run "Privacy Notice updated" infobar that no pref switched off (three tries).
- Firefox "empty" idle CPU (19%) was measured with no document open; its cause was not investigated, so it is not a reader-vs-reader figure.
- Peak footprint is not compared on Altona; use the final (idle) column.
