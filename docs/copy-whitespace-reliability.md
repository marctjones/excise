# Copied-text whitespace fidelity — reliability assessment

This document measures how reliably excise's **copy** path reproduces the
whitespace of a document — word spacing, line breaks, paragraph separation, and
list structure — and is deliberately blunt about where the behaviour is a
heuristic that breaks. It is the honest counterpart to the feature: read it
before trusting copied structure on an unfamiliar document.

The feature it describes: `TextSelectionEngine.JoinText` gained a
`WhitespaceMode` (Preferences → Text Selection → *Whitespace*):

- **Smart** (default) — line-faithful word spacing, plus a blank line at
  detected **paragraph** breaks and preserved indentation for bullet/number
  **lists**.
- **LineFaithful** — the prior behaviour: one `\n` per visual line, no paragraph
  or list detection. Byte-identical to what shipped before, for documents where
  the heuristics mis-read the layout.

Neither mode reflows wrapped lines, and neither mode changes reading order or
word spacing — those come from the `ReadingOrderStrategy` layer (#774/#824)
underneath, and they are the ceiling on everything here.

## How this was measured (and why the oracle is split)

Two oracles, because no single one is valid for every dimension:

1. **poppler `pdftotext`** (independent, not excise) — the oracle for **word
   spacing** and **line breaks**, the two dimensions where excise does *not*
   intend to diverge. Run over real corpus PDFs by
   `scripts/copy-whitespace-parity.sh` (harness:
   `Excise.Avalonia.Tests/CopyWhitespaceParityHarness.cs`, gated behind
   `COPY_WHITESPACE_PARITY=1` so it never joins the routine test run). Agreement
   is measured **order-insensitively** (multiset Jaccard over
   alphanumeric-normalised tokens / lines) so that reading-order differences
   between the two tools do not masquerade as spacing errors. Newlines fold to
   spaces for the word metric, so excise's *intentional* paragraph blank lines
   are not counted as errors; a genuinely **dropped word space** fuses two
   tokens into one and *is* penalised.

   `pdftotext` is **not** the oracle for paragraph/list structure — its default
   output does emit paragraph blank lines but does not normalise list markers or
   indentation, so scoring those features against it would register our own
   feature as "divergence". Hence oracle #2.

2. **Construction-known synthetic fixtures** — the oracle for **paragraph** and
   **list** detection. `Excise.App.Tests/Unit/CopyWhitespaceModeTests.cs` places
   every glyph, so the correct output is known exactly. 19 fixtures, all green.
   This is where the paragraph/list grades below come from.

> **A tool must not be its own oracle for the property it exists to guarantee.**
> Both oracles are external to the code under test — pdftotext is a different
> program, and the synthetic fixtures assert against hand-computed ground truth,
> not against excise's own output.

## End-to-end corpus agreement vs pdftotext

Real PDFs from `test-pdfs/`, sampled pages, the **whole** copy path
(`SortReadingOrder(ColumnAware)` → `JoinText(Smart)`). Reproduce with
`scripts/copy-whitespace-parity.sh`; the raw numbers are regenerated into
`tests/copy-whitespace/parity-results.md`.

| File | Kind | Pages | Word-token agreement | Line-break agreement |
|------|------|------:|---------------------:|---------------------:|
| producingoss.pdf | multi-paragraph prose | 8 | 87.3% | 40.3% |
| foss-primer.pdf | prose + dotted-leader TOC | 6 | 6.6% | 49.1% |
| scotus-trump-v-us.pdf | two-column legal prose | 8 | 35.4% | 8.2% |
| irs-pub509-2026.pdf | multi-column instructions | 6 | 59.1% | 6.3% |
| cdc-vis-covid-19.pdf | graphical health notice | 2 | 73.6% | 25.0% |
| **AGGREGATE** | — | 30 | **50.8%** | **25.7%** |

**#833 update — degenerate glyph widths no longer over-space.** The aggregate
rose from 37.9% to 50.8%. Some fonts report a near-zero glyph advance width
(e.g. `TT0` in scotus), which made the old width-based gap
(`cur.Left − prev.Right`) fire a space between *every* glyph (`"C i t e a 3 s"`).
Two targeted changes: (1) on lines that already carry real space glyphs the
word-space heuristic stays OUT of the way (real spaces do the separating), and
(2) when a line's glyph widths are degenerate (~0) the heuristic switches to a
width-independent advance-vs-median rule. Normal-width documents keep the
original width-based rule unchanged. Result: scotus 1.4%→35.4%, cdc 0.8%→73.6%,
with clean prose (producingoss 87.3%) unchanged. The residual loss is **not**
the whitespace layer — it is the reading-order/extraction layer beneath it
(multi-column interleave, #774/#824; and foss's dotted-leader word *fusion*),
which this change does not touch.

**This is true by construction, not by assertion.** The word-token metric folds
all newlines to spaces and strips non-alphanumerics before comparing, so Smart
mode's only outputs — inserted `\n\n` paragraph breaks and leading indent spaces
— *cannot move these numbers at all*. Whatever Smart vs LineFaithful does, the
word-token and (after `\n\n` folding) line columns are identical. So the low
scores measure the shared reading-order/word-spacing layer (#774/#824), which
this change does not touch; they are the pre-existing ceiling, now measured. The
whitespace feature can only be as good as the lines it is handed:

- **producingoss.pdf** — clean single-column prose. Word agreement 87% (the
  residual is hyphenation: excise keeps `unfamil-`/`iar`, pdftotext dehyphenates)
  and, on manual spot-check (not a counted metric here — the counted
  paragraph/list grade is the synthetic fixtures), paragraph blank lines land
  where the oracle's do. This is the case the
  feature is *for*, and it works.
- **foss-primer.pdf** — a table-of-contents page with dotted leaders. excise
  **drops the space between adjacent words** (`DesigningAround`), collapsing
  token agreement to 6.6%. That is a word-*fusion* gap in the layer below —
  separate from the #833 over-spacing and not addressed by it.
- **scotus-trump-v-us.pdf** / **irs-pub509-2026.pdf** — two/multi-column pages.
  The **per-glyph spacing symptom** (`"C i t e a 3 s"` — a space between nearly
  every glyph, from a near-zero glyph advance-width, #833) is now **fixed**: on
  degenerate-width lines the word-space rule compares origin-to-origin advances
  to the line median instead of the width-based gap. What remains is line-level
  **interleave** (`"vlJ la io rn n"` = two lines woven together) — `ColumnAware`
  (#774/#824) falls back to interleaved order when a full-width line (running
  header, page number) spans the gutter. No whitespace policy recovers that; it
  is why scotus (35.4%) and irs (59.1%) are still bounded.
- **cdc-vis-covid-19.pdf** — a large-type graphical notice; word agreement rose
  to 73.6% once the degenerate-width over-spacing was fixed (#833).

The line-break agreement column is low across the board even where words match,
because `pdftotext`'s line segmentation and excise's frequently differ on
justified/wrapped text and neither is "wrong" — it is a weak metric, kept only
to show it is not *worse* under Smart mode (the `\n\n` folds out before scoring).

## Per-category reliability

Grades combine the corpus numbers, the synthetic fixtures, and manual spot
checks. "Heuristic where it breaks" is the concrete failure mode.

| Category | Reliability | How heuristic / where it breaks |
|----------|-------------|---------------------------------|
| **Word spacing** (same line) | **Solid on clean fonts** — 87% on prose. **Degenerate widths handled (#833):** defers to real space glyphs, and on ~0-width fonts switches to an advance-vs-median rule, so those no longer over-space (scotus 1.4%→35.4%, cdc 0.8%→73.6%); normal-width docs keep the original rule. | Still misses spaces on lines that lack real space glyphs *and* have unusual tracking (dotted-leader TOCs fuse words). Not a Smart-mode behaviour — the rule is shared with LineFaithful. |
| **Line breaks** | **Solid** — one break per visual line, deterministic. | The break *decision* is faithful; whether a wrapped line "should" have been joined is a separate (unreliable) question we do not attempt. |
| **Paragraph separation** | **Good on clean single-column prose** — counted grade is the synthetic fixtures (all pass); producingoss agreement is spot-checked, not counted. | Fixed factor (gap > 1.6× median leading). **Tight leading** can read a paragraph break as one paragraph; a **heading or figure gap** can read as a spurious paragraph break. Not adaptive per block. |
| **Simple bullet lists** | **Good** — markers (•, -, –, *, ·, ‣, ◦) detected, items kept tight on their own lines; synthetic fixtures pass. | A sentence that *opens* with a hyphen/asterisk is mis-tagged as a list item (kept narrow: marker must be followed by a space). |
| **Numbered lists** | **Good** — `N.` / `N)` / `a.` / `a)` detected; fixtures pass. | A line that legitimately starts `1998 was...` is safe (no `.`/`)`), but `1. ` inside running prose (e.g. a footnote ref) is mis-tagged. Roman numerals only as a single letter. |
| **Nested / indented lists** | **Heuristic** — indentation preserved as 2 spaces/level (capped at 4). | Depth is quantised from the left-edge offset; uneven or deep nesting mis-levels. **Continuation lines** of a wrapped list item are not indented under the marker — they fall back to a plain line break. Deferred: #825. |
| **Justified text** | **Line breaks solid; spacing best-effort.** | Wide justified inter-word gaps are still one space (good), but the paragraph heuristic sees justified blocks' even leading fine. No wrap-reflow (justified reflow is unreliable). |
| **Tables** | **Not supported — treated as text.** | No table model. A table row copies as a line; a cell that starts with a number/dash can be mis-read as a list marker. A wide-gap table can interleave under `Simple`/fallback reading order. Deferred: #825. |
| **Headers / footers** | **Not separated.** | Running headers/footers copy inline; worse, a full-width header/footer **defeats column detection** (#774/#824), scrambling multi-column pages (scotus, irs). |
| **Multi-column** (via #774/#824) | **Bounded** — clean two-column pages read column-by-column; **fails** when a full-width line spans the gutter → interleaved scramble. | This is the single largest source of corpus error (scotus 1.4%, irs 64%). Reading-order problem, not whitespace. |
| **Text around figures** | **Unreliable.** | Figure captions and wrapped-around text share Y-bands with body text; grouping mixes them. Out of scope for #774; unaddressed here. |

## What is deliberately deferred (issue #825)

Rather than ship a confident-but-wrong transform, these are left as documented
gaps:

- **Wrap-reflow** — joining a paragraph's wrapped lines into one. Justified
  spacing + hyphenation make it unreliable; Smart keeps hard breaks.
- **Nested-list depth and wrapped list-item continuation lines.**
- **Table detection / table-vs-list disambiguation.**
- **Adaptive paragraph-gap threshold** (currently a fixed 1.6× median leading).
- **The reading-order ceiling** — multi-column scramble on full-width lines,
  dropped word spaces on dotted leaders, near-empty extraction on graphical
  pages. These bound everything above and are tracked with #774/#824.

## Bottom line

On **clean single-column prose**, Smart mode is a real improvement: paragraph
blank lines and lists come out as the reader expects, at ~87% word agreement
with an independent tool. On **multi-column government forms and graphical
pages**, the copy path is currently unreliable — but the failure is in the
reading-order/extraction layer, which the whitespace layer sits on top of and
cannot repair. Smart mode never *removes* content and is non-destructive; where
its structure guesses are wrong, **LineFaithful** mode reverts to the plain,
predictable behaviour.
