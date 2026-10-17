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
