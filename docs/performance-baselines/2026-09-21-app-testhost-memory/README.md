# `Excise.App.Tests` testhost memory and the forced collections — issue #1772

- **Date:** 2026-09-21
- **Machine:** Apple M-series, 24 GB RAM, macOS 26.6.2 (Darwin 25.6.0), .NET SDK 10.0.12,
  Debug build, run from a git worktree.
- **Method:** the same wrapper `scripts/run-full-suite.sh` uses for the
  `app-tests-unchunked-evidence` row —
  `/usr/bin/time -l sh -c "{ dotnet test … --no-build -c Debug } > log 2>&1" 2> run.rusage`
  — so `maximum resident set size` is comparable to the figures in CLAUDE.md's
  memory table. Every run was serialised against the other agents on this box
  through a shared lock, one testhost at a time (#619).
- **Sampler:** the testhost's RSS every 5 s (`ps -o rss=`), plus `vmmap -summary`
  at each new high-water mark ≥ 512 MB above the last one captured. The sampler
  is for the SHAPE of the curve (sawtooth vs staircase); the 10 % rule in the
  issue is judged on `time -l`'s `maximum resident set size`, because 5 s
  samples miss short spikes.

> ⚠️ Every number here is a measurement with a date, not a property. Re-run
> before quoting it — CLAUDE.md's own table has been 2.2× high and 1.9× low
> within a month of being written.

## Step 1a — the retention report that never ran

`Integration/HeapRetentionReportTests` is `Assert.SkipUnless(EXCISE_HEAP_REPORT=1)`,
so it had never produced a number. Run here with
`EXCISE_HEAP_REPORT=1`, `EXCISE_HEAP_REPORT_PDF=<main checkout>/test-pdfs/federal/irs-1040-instructions.pdf`
(the fixture resolves through `FindUpwards`, which does not reach the main
checkout from a worktree — passing the path explicitly is what makes the test
PASS rather than skip). Result: **1 test, passed, 55 s**, peak RSS 336 MB.

Three open → index → prewarm → page(30 @ 192 DPI) → close cycles:

| cycle | stage | committedMB | liveMB | rssMB |
|---|---|---:|---:|---:|
| 1 | prewarmed | 116.0 | 136.8 | 280.3 |
| 1 | closed | 111.9 | 136.2 | 324.9 |
| 1 | closed+defaultGC | 75.3 | **3.0** | 290.2 |
| 2 | closed+defaultGC | 73.2 | **3.0** | 265.6 |
| 3 | closed+defaultGC | 115.2 | **2.9** | 311.4 |

**Live managed bytes after close are 3.0 / 3.0 / 2.9 MB across three cycles —
flat.** The GUI's own open/close path retains nothing per document. So whatever
drives the App testhost to multiple GiB is not the document lifecycle; it is
either per-test retention held by test-assembly statics, or GC high-water and
native (Skia) memory the allocator does not return. Steps 1b–4 separate those.

## Step 1b — the baseline, and reconciling it with the 8207 MB already recorded

Commit `4fdf1916`, the whole project unchunked and alone on the box:

| | value |
|---|---|
| `time -l` maximum resident set size | **4869 MB** |
| wall clock | 756 s (xUnit reports 12 m 34 s of test time) |
| results | 1663 passed, 0 failed, 18 skipped, 1681 total |
| 5 s sampler peak | 3969 MB |

⚠️ **This is NOT a contradiction of the 8207 MB that #1769 put in CLAUDE.md's
table on the same date.** That figure was measured on `491762a4`; this one is
measured on `4fdf1916`, which is `491762a4` plus the #1768, #1769, #1770, #1771
and #1773 merges — among them ~120 deleted Unit tests, nine windows that
`AnnotationDisplayControlTests` now closes, the #1771 UI-test leak fixes, and
the static gates #1773 moved out of this assembly. Two commits, two numbers;
both belong in the table with their shas.

### Sawtooth or staircase? — **staircase with teeth, i.e. retention**

The testhost's RSS every 5 s, sampled by `comm` so the qpdf/mutool children
that carry the test-output path in `argv` are not counted:

```
    0s      243 MB
  128s      796 MB
  255s     1506 MB
  383s     2443 MB
  509s     2069 MB
  636s     2608 MB
  ...
  end      3965 MB   (= the sampler's peak; the last sample IS the high-water mark)
```

`vmmap -summary` at the 2.8 GB mark splits it as VM_ALLOCATE 1.6 G resident
(the .NET heap and Skia's large reservations), MALLOC_LARGE 542 M + MALLOC_SMALL
292 M resident (native allocations behind finalizable wrappers), mapped file
44 M — so it is roughly two thirds managed-heap-shaped and one third native.

There are 12 drops larger than 100 MB, so the forced collections do reclaim —
that is the sawtooth. But **every trough is higher than the one before it and
the curve ends at its maximum**, which a pure GC high-water pattern does not do.
Something the process keeps is growing monotonically over the run. That is what
step 2 goes after; steps 3 and 4 are about the COST of the collections, not
about the growth.

## Steps 2–4 — one change per run, each run alone, each chained to the last

Every row is a whole unchunked `Excise.App.Tests`, `--no-build`, `-c Debug`,
serialised against the other agents on the box. "after" for one step is
"before" for the next, so the deltas compose.

| # | change | max RSS | Δ RSS | wall | Δ wall | results |
|---|---|---:|---:|---:|---:|---|
| — | baseline `4fdf1916` | 4869 MB | — | 756 s | — | 1663 / 0 / 18 |
| 2 | `SeenSurfaces` → `ConditionalWeakTable` | **1765 MB** | **−64 %** | **408 s** | **−46 %** | 1663 / 0 / 18 |
| 3 | sweep collects every 10th page | 1886 MB | +6.9 % | 391 s | −4.2 % | 1663 / 0 / 18 |
| 4 | per-test collect every 10th test | 1959 / 1783 / 2110 MB | +3.9 / −5.5 / **+11.9 %** | 377 / 372 / 373 s | ≈ −4 % | 1663 / 0 / 18 ×3 |

Results are `passed / failed / skipped` out of 1681, identical on every run, and
no run produced a host-death marker (`rc=0`, no abort, no blame dump).

### Step 2 is the whole story

One static in a test-only instrument held the suite's peak RSS. `SeenSurfaces`
recorded "have I enumerated this window instance?" in a strong `HashSet` that
nothing ever removed from, so every window that received a pointer or key event
stayed reachable for the process lifetime with its visual tree, viewer controls
and tile caches attached. That is the monotonic staircase measured above.

The wall-clock halving is a consequence, not a separate win: the per-test and
per-page forced gen2 collections were tracing a heap that kept growing, so
making the heap small made every one of them cheap.

Behaviour-neutrality was shown, not argued: `artifacts/gui-coverage`'s three
append-only tsvs and `scripts/check-gui-interaction-coverage.sh`'s verdict are
**byte-identical** before and after (sha256 `43416f3c…`, `8e8d2ca2…`,
`ee55cf53…`, `c51508cb…` on both runs, from two runs made back to back here —
not against an older log).

### Step 4 was REVERTED

Three consecutive runs of the every-10th-test collect are 1783 / 1959 / 2110 MB
against step 3's 1886 MB: the worst is **+11.9 %**, over the 10 % line the issue
set, for about 4 % of wall clock. The unconditional collect is the #861 bound on
multi-GiB RSS and is entangled with #363 host stability — the failure it guards
against costs a whole multi-hour run — so 17 s is not worth loosening it on a
straddling measurement. Steps 2 and 3 are kept; the per-test collect stays
unconditional.

⚠️ The honest caveat: step 3's "before" is a single run, so the comparison is
one number against three. The conclusion "not worth it" survives either reading;
the conclusion "it definitely regresses RSS" does not, and is not claimed.

## Step 6 — what the 144-page display sweep is actually for

The sweep is **viewer plumbing**: the bitmap `Image.Source` holds is the bitmap
`SkiaRenderer` produced. Rendering correctness belongs to
`Excise.Rendering.Tests/Differential` and the five-oracle corpus rendering scan.
Measured here, the sweep costs **80.9 s**, not the 229 s the issue quotes.

Answering the issue's question with data rather than taste:

| | cases | case time |
|---|---:|---:|
| as it was | 147 | 64.8 s |
| identical bytes at the same page, deduped | 100 | — |
| + one page per document (with two exceptions) | **62** | **22.9 s** (the sha256 pass is inside it) |

**47 of the 147 cases were not distinct inputs at all.** `test-pdfs/smoke/*` is
a byte-for-byte copy of `test-pdfs/federal/*` (sha256-verified) and both carry
contract files, so those documents were rendered twice under two paths.

The remaining cut keeps one page per DOCUMENT, because the plumbing branches —
render plan and DPI, the pixel-budget clamp, rotation, password, a small page
centred in a larger viewport, expected-non-renderable — are document
properties, not page properties. Two exceptions are kept explicitly: a page
whose contract classifies it differently from its document's first page, and
the last page of any document with five or more selected pages, so "page 1" and
"page N" viewer state are both exercised (12 non-first pages survive).

`EXCISE_GUI_DISPLAY_COVERING_SET=0` restores every discovered page — that is
what a `full`-tier row should pass, and what to set before trusting a bisect
that lands here. The report records `plumbingCoveringSet` and
`discoveredPagesBeforeCovering` so a subset run cannot be read as "we checked
everything".

### Both step-6 assertions were planted and observed RED

- `Dpi = dpi + 1` in `RenderDirectViewerPage` → all four shards of the REDUCED
  sweep fail: *"Expected displayed.Width to be 130 … but found 128"*.
- An empty content stream → `SkiaRenderer_RendersSimpleText_ProducesExpectedBitmap`
  fails: *"Expected inkInBand to be greater than 500 … but found 0"*. That test
  asserted only `Width > 100` and `Height > 100` before, which a renderer that
  drew nothing passes.

## Where it ended up

Steps 2 + 3 + 6, step 4 reverted, same commit lineage, run alone:

| | max RSS | wall | results |
|---|---:|---:|---|
| baseline `4fdf1916` | 4869 MB | 756 s | 1663 / 0 / 18 |
| final | **1949 MB** | **356 s** | 1663 / 0 / 18 |

−60 % peak RSS and −53 % wall clock, with the GUI coverage artifacts and
verdict byte-identical to the baseline on all three comparison runs.

## Step 5 — `[FixedAvaloniaFact]` tests that may only need a dispatcher

Not migrated; listed so the next person does not have to re-derive it. A crude
static scan (no `new Window`, `Show()`, `RenderTargetBitmap`, `PdfViewerControl`,
`new MainWindow`, `TopLevel` or `Dispatcher.UIThread` anywhere in the body)
flags 274 of the ~330 declarations, concentrated in:

```
 24  UI/AttachmentsPanelTests.cs          19  UI/MultiDocumentTabTests.cs
 16  UI/PageOrganizationCommandTests.cs   15  UI/DocumentPrintingTests.cs
 14  UI/MultiDocumentSessionTests.cs      13  UI/FileOpsCommandTests.cs
 13  UI/DocumentPermissionEnforcementTests.cs   8  UI/MultiDocumentWindowTests.cs
  8  UI/ReleasedMemoryReclaimTests.cs      7  UI/PriorityToolbarPanelTests.cs
  7  Controls/RenderAheadTests.cs          6  UI/ViewerCacheTrimTriggerTests.cs
```

⚠️ **That number is an upper bound and should not be acted on as a list.** Most
of these build a `MainWindowViewModel` through
`MainWindowViewModelTestFactory`, which needs Avalonia's statics and the
ReactiveUI dispatcher wiring even though it opens no window — "needs no window"
is not "needs no Avalonia". The cheap runner the issue imagines would keep the
dispatch and drop the per-test window sweep and collection; with step 2 landed,
the collection it would drop is no longer expensive, so the remaining prize is
small. Anyone picking this up should measure one class first.
