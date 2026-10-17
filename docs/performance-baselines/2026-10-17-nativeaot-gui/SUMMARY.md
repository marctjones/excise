# NativeAOT GUI verification, 2026-10-17 (macOS 27.0.1; the earlier 2026-10-15/16 data was macOS 26.6.2)

Bundle: NativeAOT, commit with #2077's fix, Info.plist `MallocSpaceEfficient=1`. Real-key runs drive the app with synthetic key events and need the foreground.

## A. In-app scenarios (background launch, 3 repeats, no focus taken)
- `business-open` open stall 24-68 ms (was 625 ms), open wall 234-250 ms. #2077's freeze is gone.
- `irs-redact-save` `save-redacted` 16.1 s, UI blocked the whole time (19.7 s before #2075's first stage; #2074 open).
- `scroll-30` on the 455-page book: 5.2 s with a UI stall under 10 ms: not a freeze.

## B. Responsiveness, excise only, 5 repeats, no zoom (`phaseB.md`, `phaseB-noenv.md`)
First change p50 / p95 ms, End, Home: with the allocator setting irs 41/53, 67, 61; scan 42/53, 55, 53; altona 39/47, 37, 35. The same OS with
the bundle WITHOUT the setting: irs 41/50, 64, 56; scan 40/54, 57, 52. Equal within noise: the setting costs no responsiveness. Against the
2026-10-15 table (irs 61 ms) turns are faster, but that table is from a different macOS release, so the OS, not a code change, is the likely cause.

## C. Five readers with zoom (`phaseC.md`), 3 repeats
Shortcuts verified by screenshot to zoom the PDF (not the page): excise (173%), Preview, Chrome PDF viewer (150%), Firefox pdf.js (170%), Acrobat (first step).
Firefox failed to take keys in the full run (60/60 turns without response; it worked in two single-app probes, so the cause is environmental:
an update notification and a new window opened over the PDF); its row is not a result. Acrobat's zoom answered only the first step per run.
Within the full run, irs page turn first change p50: Acrobat 35, Preview 42, Chrome 61, excise 72; zoom-in first change / drawn: excise 67/100,
Preview 60/232, Chrome 76/76, Acrobat 148/150. The excise page-turn figure in this run (68-72 ms) did not reproduce the 41 ms measured earlier the same
night on the same bundle (a recheck alone gave 68 ms again), so the machine's state changed between them; treat cross-app ratios within one run as
valid and the absolute excise figure as machine-state dependent.

## C, redone: Firefox and Acrobat (`phaseC-firefox.md`, `phaseC-acrobat.md`)
Causes of the first-pass failures, found by reproducing them: Firefox's "Update available" doorhanger (it comes from the installed Firefox's own
pending-update state, so no profile pref removes it) takes keyboard focus about ten seconds after start when Firefox is run after another app in
the same bench process; run on its own (`--apps firefox`) the keys land. Acrobat raises a modal "Updater is unable to check for updates" that blocks
keys; the speed bench now clicks "Remind me later" where it appears. Acrobat had also been killed at the end ("did not quit"), which excluded its rows;
that is now soft. With these: page-turn first change p50 (irs / altona / scan) Firefox pdf.js 33 / 28 / 34 ms, Acrobat 39 / 41 / 37; zoom-in first
change / drawn (irs) Firefox 55 / 516 ms, Acrobat 64 / 457 ms, Preview 60 / 232, Chrome 76 / 76, excise 67 / 100.

## D. Memory and CPU, footprint MB, median of 2 (`phaseD-memory-cpu.md`; excise NativeAOT with the allocator setting)
| doc | | opened | paged-30 | idle 30 s | closed 45 s | CPU s |
|---|---|---:|---:|---:|---:|---:|
| irs | excise / Preview / Chrome / Acrobat | 245 / 295 / 853 / 1237 | 311 / 320 / 872 / 1298 | 200 / 267 / 617 / 1410 | 180 / 171 / 205 / - | 3.8 / 24.1 / 7.3 / 9.7 |
| altona | excise / Preview / Chrome / Acrobat | 447 / 243 / 1236 / 1922 | 493 / 313 / 1128 / 1955 | 186 / 241 / 831 / 1522 | 165 / 179 / 209 / - | 3.8 / 14.7 / 13.0 / 14.9 |
| scan | excise / Preview / Chrome / Acrobat | 202 / 210 / 833 / 1621 | 371 / 310 / 876 / 1596 | 159 / 244 / 572 / 1487 | 147 / 210 / 208 / - | 2.0 / 13.6 / 8.7 / 8.9 |
excise is now the lowest or near-lowest after the idle trim and after close on all three documents (scan closed 252 MB before the allocator setting,
147 MB now), and uses the least CPU. Preview and excise are close while a document is open; Chrome and Acrobat are several times larger. Acrobat's
"closed" columns are blank: it does not quit in the harness (killed after the idle samples).

## Excise page-turn latency moved during the night: investigated (irs, first change p50, 3 repeats, NativeAOT)
The same bundle gave 41 ms early in the night and 72-75 ms later, on the same macOS release, so the macOS 27 upgrade cannot be the cause of this
shift (it can only explain the difference from the 2026-10-15 table, which was macOS 26.6.2).

| test | result |
|---|---|
| old bundles (before the allocator setting, before #2077, with it) on the slow machine | 72, 74, 75 ms: not the code |
| `mediaanalysisd` (217% CPU earlier) gone | 75 ms: not that daemon |
| display | built-in, fixed 60 Hz, not mirrored; AC power, no thermal warning, 75% memory free, no swap |
| every other window hidden | 73 ms: not window clutter |
| `EXCISE_LATENCY_ACTIVITY` (an NSActivityLatencyCritical assertion, experiment since removed) | 75 vs 73 ms: no effect |
| 0 / 2 / 4 / 8 background CPU spinners (`nice -n 19 yes`) | **75 / 52 / 45 / 40 ms** |

So page-turn latency is set by how awake the CPU is, not by anything excise does: the early 41 ms runs happened while other work (my own runs and a
system indexing daemon) kept the cores busy, and an idle laptop gives 75 ms. In-app render time barely moves (continuous lookahead render mean 28.4 ms
idle vs 24.7 ms with spinners), so the ~35 ms (two frames at 60 Hz) is outside the render work: waking cores and clock ramp along the input -> main thread ->
compositor -> present path. A `sample` of excise during the turns shows every thread waiting nearly all of the time (no hot spot; the main thread is idle).
The other apps move much less (Chrome 61-62, Acrobat 35-39, Preview 42 in both states), so on an idle machine excise is the slowest reader here (75 ms) and
the realistic figure to quote; the 41 ms figure is the busy-machine one. Thread QoS was then tested and ruled out: an env-gated experiment raised the .NET thread-pool workers (the band renders), Avalonia's compositor timer
thread (`RenderTimerLoop`) and the `CVDisplayLink` threads to user-interactive with `pthread_override_qos_class_start_np` (every thread reported the
override as applied), idle page turn p50, 3 repeats: none 72 ms, compositor only 75, pool only 74, all 71 (69 in a one-repeat verification run with
logging). So the cost is not those threads' priority; what remains is the system's input-to-present path on an idle machine (WindowServer and the display
pipeline waking), which an app cannot reach from its own threads. The experiment code was removed.

## What published sources say (checked after the QoS experiment, 2026-10-17)
- **Avalonia's macOS render timer** (`native/Avalonia.Native/src/OSX/PlatformRenderTimer.mm`, read from the repository) is a plain `CVDisplayLink`
  started and stopped by the compositor; its tick runs on the display-link thread (`RunsInBackground` true) with no QoS or priority code. Frames are
  presented in `metal.mm` with `presentDrawable` and `commit`; `presentsWithTransaction` is used only around resizes. This matches what the profile shows
  (CVDisplayLink and RenderTimerLoop threads) and confirms the app does not set thread priority; it does not say whether that matters. No Avalonia issue,
  blog or documentation page on macOS input latency, thread priority or frame presentation turned up in several searches, so there is nothing published
  from the Avalonia side to confirm or contradict this.
- **Third-party experience agrees with the QoS result.** Ghostty's author set QoS classes on a macOS render thread and reported no noticeable difference
  and that Apple provides few tools to experiment with ([commit](https://git.uoc.run.place/Applied-Software/ghostty/commit/a482224da8577246d4a31f72d298a25352e0df9d)).
  Mozilla is running its own experiment on default thread QoS on macOS ([bug 1995638](https://bugzilla.mozilla.org/show_bug.cgi?id=1995638)).
- **One documented latency effect I tested and did not reproduce**: an Apple Developer Forums report ([thread 841189](https://developer.apple.com/forums/thread/841189),
  unanswered) says apps started through LaunchServices (`open`) show about one frame more input-to-display latency than the same executable run directly.
  Excise started through `open`: 74 ms, executable run directly: 70 ms (3 repeats each, idle machine), within noise.
- **Display-link phase**: `CVDisplayLink` is described as a timer aligned to the display ([Thume, with a later correction](https://thume.ca/2017/12/09/cvdisplaylink-doesnt-link-to-your-display/));
  a key press can wait up to a frame for the next tick. Apple's current recommendation for Metal apps is `CAMetalDisplayLink` (macOS 14 and later) with
  `preferredFrameLatency` ([documentation](https://developer.apple.com/documentation/quartzcore/cametaldisplaylink)). That is a change inside Avalonia, not in excise.
- **Not found**: a source on Apple-silicon clock ramp after idle as a cause of input latency. The spinner dose-response above is my own measurement, and why other
  readers stay near 40 ms on an idle machine remains unexplained.
