# Fix order

> ## ⛔ FEATURE FREEZE — in effect from 2026-08-10
>
> **Fix errors only. No new feature development until the freeze is lifted.**
>
> Frozen (do not start): **#900 / #901 / #902** in-place text editing,
> **#903** document diff, **#921** wiring FDF/XFDF, **#784** UI
> internationalization.
>
> **UNFROZEN 2026-08-11 — annotations (#912).** Finishing the unreachable
> annotation types and fixing annotation rendering bugs is explicitly back in
> scope. The rest of the freeze stands.
>
> Not frozen: defect fixes, diagnosis of measured-but-unexplained symptoms,
> dead-code removal, and **test/gate infrastructure whose purpose is finding
> errors** (#904, #907, #695).
>
> The grey area is anything that lets excise do something it could not do
> before, even when the engine code already exists and only the surface is
> missing — #921 is that shape. **Treat it as frozen.** (#912 was too, until
> it was explicitly unfrozen.)
>
> Rationale: ~33 open issues resolve to five underlying defects (below).
> Building new surface on known-bad foundations adds reach to defects that
> already exist. If a fix naturally suggests a feature, file it and move on.

Generated 2026-08-10 from an audit of every open issue, re-examined the same day
for root cause and for whether the work is wanted at all. **Re-examined again
2026-08-12**: clusters A and B rewritten from new evidence, a
"Cross-cutting structure" section added (what has to change, not just what to
fix), and three tier entries struck because the issues had been closed while
still scheduled here — the staleness this file warns about, in this file. **Ordered by what each
one blocks and by whether its root cause is actually known — not by priority
label.**

Two rules govern the sequence:

1. **Root cause must be established before an issue is scheduled.** Six issues
   below have a *measured symptom* and an *unknown cause*; they are grouped
   separately, and the first task for each is a diagnosis, not a fix. This list
   says so explicitly rather than letting a `priority: high` label imply
   someone knows what to do.
2. **Redaction correctness outranks everything.** It is what the tool exists to
   guarantee, and its failures are silent — no crash, no error, the name is
   just still in the file.

Re-verify before starting anything here. Issues go stale: #876 recorded 80s for
a render that measured 114s nine days later, and #909's premise changed the same
day it was written.

---

# Root-cause clusters

Re-examined 2026-08-10. **The tracker has ~34 open issues and roughly five
underlying defects.** Several issues are the same problem wearing different
labels, and reading them one at a time hides that. Fix the root and the
consequences close with it; schedule a consequence on its own and you buy the
same work twice.

## A. Text assembly — `page.Text` consults no geometry at all

**Re-examined 2026-08-12 on page 117 of `irs-1040-instructions.pdf` (worst page,
0.774). Both defects are now established, and they are INDEPENDENT — the earlier
"fix defect 1, then re-read page 117" discriminator is superseded.**

### Defect 1 — one block is mis-positioned (#899)

`PdfPage.Text` drops every letter outside the CropBox: 1043 of 3928 on page 117.
The page is `MediaBox`/`CropBox` 0–792 with a `BleedBox` to **1008**, and the
dropped letters sit at Y 794–959.

**That off-page block is two different things, and only one is a bug:**

| off-page content | mutool also excludes it? | verdict |
|---|---|---|
| `Page 117 of 126 … MUST be removed before printing.` | **yes** | correctly off-page — a printer proof mark |
| `…should have received Form 1095-A from the Marketplace…` | **no, mutool has it** | genuinely mis-positioned |

So the character-count gap is NOT all defect. Any fix that simply stops
filtering by CropBox would raise the parity score while making the output worse
— excise would emit proof marks no other tool reports.

excise's matrix arithmetic is **not** broken in general: hand-composing the
content stream (`1 0 0 -1 0 1008 cm` → `1 0 0 1 42 33 cm` → `1 0 0 1 0 85 cm` →
`1 0 0 -1 0 12.97168 Tm`) gives `(42, 877.0)`, exactly what excise reports.

**Signature to chase:** the bad block is 216pt too high, and 1008 − 792 = 216.

**Refuted 2026-08-12 — do not re-run:** unbalanced `q`/`Q` (token-accurate count
is 7/7, depth never negative — a naive `' q '` substring count says 2/4 and will
mislead you); `BDC` with an inline dictionary operand desyncing the parser
(minimal two-case repro places both at the same Y); a Form XObject `/Matrix`
(the page has none, single content stream).

### Defect 2 — assembly ignores geometry entirely (#938)

No longer "unconfirmed". It is structurally certain from four lines of code:

```csharp
foreach (var letter in letters)   // content-stream order
    sb.Append(letter.Value);      // no spacing, no line breaks
```

`page.Text` cannot insert a space between runs (`fileif`), cannot break lines
(`market-place`), and cannot order columns — regardless of whether defect 1 is
fixed. A space appears only where the PDF happens to contain a space glyph.

**Root cause is LAYER INVERSION, not a missing algorithm** — see S1 below. The
correct implementation exists, is validated against poppler, and lives in an
assembly Core cannot reference.

| issue | relationship |
|---|---|
| **#899** | owns defect 1 (positioning) |
| **#938** | owns defect 2 (assembly) — split out 2026-08-12 |
| #773 reading-order heuristics | largely subsumed: `ReadingOrderStrategy.ColumnAware` already exists |
| #825 copy-whitespace deferred cases | its "reading-order ceiling" is defect 2 |
| ~~#924 search~~ | **closed** — the search half was separable and is fixed |

## B. The document is open twice — FIXED on `fix/917-single-document` (2026-08-12)

**Root: #917.** One `PdfDocument` now serves both the save path and the viewer.
Full `Excise.App.Tests` 1326/1326; pinned by a test asserting the two are the
same instance BEFORE and AFTER a mutation.

**The finding worth keeping.** The old per-mutation `SaveToBytes()` + reparse was
doing **three jobs at once**, and only the first was visible:

1. **Data sync** — gone by construction.
2. **The structural-change SIGNAL.** The viewer rebuilds its continuous layout
   from `DocumentProperty.Changed`, and an Avalonia styled property only raises
   that when the VALUE changes. The reparse handed it a new instance every time,
   so the rebuild came for free. Without it the view kept the pre-mutation page
   order. Replaced with an explicit `DocumentStructureChanged` event.
3. **Bitmap lifetime.** The first replacement for (2) bumped `RenderVersion` —
   which means "page CONTENT was rewritten" and disposes the displayed bitmap.
   A page MOVE changes order, not content, so layout touched a disposed bitmap.

Each regression surfaced **only** in the full 1326-test run, never in targeted
filters, and each was confirmed against `develop` before being blamed on the
change. This is the clearest evidence for S2 below.

| issue | relationship |
|---|---|
| **#917** | fixed, unmerged |
| #922 mutation resync cost | ⚠️ **the reparse is gone, but its cost is now UNMEASURED.** An attempt to demonstrate the win failed twice: perf-budget times moved 9–16% while allocations stayed identical (this repo's documented signature of noise), and a direct page-move probe reported 0.3ms on both branches — because a validity check showed the move was not happening. #922's own 149.7ms figure stands; nothing here has confirmed or refuted it. Re-measure before claiming |
| #926 save-over-open on Windows | should be fixed — the viewer no longer opens file-backed — but unverifiable on a macOS-only box |
| #912 / #934 annotations | every row needed a hand-written viewer mirror; that requirement is now gone |

## C. The writer

**Root: #923.** No `/ObjStm`, no `/XRef` streams, so every save inflates.

| issue | relationship |
|---|---|
| **#923** | up to 2.79x on open-and-save with zero edits |
| #908 CFF fonts embedded unsubsetted | independent cause, same symptom — output larger than it should be |
| #922 | serialises ~2.8x more bytes than needed, so #923 shrinks its cost proportionally |

## D. Oracles test the pipeline you point them at

**Root: #904.** The suite is excellent at regression and poor at first
discovery. **Verified this pass:** `extraInkTiles` is assigned once in
`Excise.RenderTools/Program.cs:2268` and **never read** — the corpus gate is
directionally asymmetric exactly as #904 claims, gating under-draw only. The
checkbox bug drew ink the reference did not, in the direction nothing watches.

| issue | relationship |
|---|---|
| **#904** | owns it |
| #907 corpus residue | its own triage found **21 of 35** defect-class pages are the GATE scoring against the most-inked oracle, not excise bugs. That is a gate defect, not a page backlog |
| #915 Altona render time | nothing gates render duration, so a ~42% slowdown in nine days was invisible until a test hit its timeout |
| #695 click-everything harness | the GUI-side answer to the same gap |

**What we really want:** gates that can fail in both directions, and invariants
that need no oracle at all. #904 lists those and they are the cheapest part.

## E. Redaction carrier coverage

**Root: you cannot name what was in the box.** An area redaction has a
rectangle, not a term, so it cannot ask "does this carrier mention what I
removed?" without deriving terms — which corrupts (`Younger` → `Ynger`).

| issue | relationship |
|---|---|
| **#916** | outline titles + annotations outside the box or on another page |
| #905 `ScrubTerms` substring-replaces | the corruption mechanism #897 routed around. Still live for `RedactText`, which has a real term |
| #898 does re-redacting skip the scrub? | a verification, not a defect — cheap, and a "yes" is a leak |

---

# Cross-cutting structure — what has to CHANGE, not just what to fix

Added 2026-08-12. The clusters above say which issues share a cause. This says
which **properties of the codebase** keep producing those causes, and what
restructuring removes them. Each has an enforcement gate where one is possible,
because a refactor with no gate regresses.

## S1. Layer inversion — engine capability living above the engine

**Evidence.** `TextSelectionEngine` — reading order, geometric whitespace,
dehyphenation, 1013 lines — lives in `Excise.Avalonia/Services/` and its usings
are `Excise.Core.Document`, `Excise.Core.Text`, `System`, `System.Collections
.Generic`, `System.Linq`. **Zero Avalonia dependencies.** It is pure engine logic
sitting in the viewer, so `Excise.Core`'s own `page.Text` cannot reference it and
concatenates raw letters instead. The good implementation is validated against
poppler by `check-copy-whitespace-parity.sh`; the engine simply cannot reach it.

#917 was the same shape one level up: document ownership split between the App's
service and the App's ViewModel, so neither owned it.

**Refactor R1.** Move `TextSelectionEngine.cs`, `WhitespaceMode.cs`,
`ReadingOrderStrategy.cs` into `Excise.Core/Text/`; point `PdfPage.Text` at
`SortReadingOrder(ColumnAware)` + `JoinText(Smart)`, keeping the CropBox filter.
21 files, 235 references, and the public API baseline of two assemblies.

**Gate.** A test that fails when a type under `Excise.Avalonia/Services/` imports
no Avalonia namespace — i.e. pure logic that belongs one layer down. Crude, but
it names the exact smell and would have caught this the day the file landed.

**Do NOT** fix #938 by adding a second whitespace heuristic inside
`PdfPage.Text`. That is the small-looking change, and two implementations
drifting apart is what produced the bug.

## S2. Full rebuild is the only invalidation primitive

**Evidence.** #922 (mutation → serialise + reparse the whole document), #923
(every save rewrites everything), #919 (`RedactText` re-extracts every letter per
match), #918 (13 sites `Open(File.ReadAllBytes)`).

**The sharpest evidence is #917's fix.** Removing one whole-document rebuild
broke three unrelated things, because the rebuild was silently serving as the
change-notification mechanism *and* the bitmap-refresh mechanism. Nobody designed
that; it accreted because "rebuild everything" is the only invalidation the
codebase has, so every consumer that needed to know something changed rode on it.

**Refactor R2.** An explicit change model. #917's fix introduced the seed —
`DocumentStructureChanged` for "page order changed, content did not" alongside
the existing `RenderVersion` for "content was rewritten". Finish the taxonomy
(structure / content / annotations / form values), give each its own
invalidation, and stop using a full reparse as a proxy for any of them.

This is the prerequisite for #922 and #919 being fixable *cleanly* rather than by
caching harder.

## S3. Built twice, wired once

**Evidence.** **96 public API entries are implemented but unreachable from
production** — 79 tests-only in `Excise.Core` alone. Named instances:

| # | what | size |
|---|---|---|
| #928 | a **second redaction path** (`TextRedactor`/`PdfRedaction`), unused, tested, **and documented as glyph-level when it is operator-level** | 636 lines |
| #921 | FDF/XFDF import/export, implemented and tested, wired to nothing | 1782 lines |
| #938 | a second text assembly (the good one) | 1013 lines |
| #917 | a second document | — |
| #908 | `CffSubsetter` — 25 test references, 0 production callers | — |

**#928 is the dangerous one** and should be treated as a redaction-trust defect
rather than dead code: it carries a *false safety claim* about the single
guarantee the tool exists for. Delete it or correct the claim; do not leave a
tested, plausible-looking redactor that does something weaker than its own
documentation says.

**Refactor R4.** A delete-or-wire policy. "Implemented + tested + unreachable" is
a defect class, not neutral inventory — #896 shipped a redaction leak through the
CLI precisely because the safe path existed and nothing used it.

**Gate.** `scripts/check-unwired-api.sh` already exists and works — it
independently confirmed #934 wired up four previously-dead annotation APIs. The
job is to **shrink** the 96-entry baseline (#931), not to keep accepting into it.

## S4. Instruments that cannot run, or score wrongly

**Evidence.** #932/#907 (the corpus gate scores against the single most-inked
oracle, mis-pinning ~21 pages as excise defects for agreeing with the majority),
#935 (two of six reference oracles are installed on no runner anywhere), #929
(App and Core tool-gated oracle tests have no CI home), #904 (the suite finds
regressions but not first-discovery bugs), #936 (claims about the suite go stale
and are wrong in the direction that costs coverage), #937.

**This is not a code refactor — it is capability.** The corpus gate is the only
instrument that has historically produced first-discovery product bugs (v3.6.0:
seven parser/renderer fixes found by measurement, plus a bug in an oracle
itself), and it is currently degraded. Restoring it outranks adding more tests.

---

# Recommended sequence

⚠️ **The seven-step sequence that stood here until 2026-08-17 was entirely
stale** — #917, #938, #928, #932, #899 and #923 are all CLOSED, and v3.8.0 is
tagged. It was the exact failure this file warns about in its own header, in
this file, for the second time.

**The sequence is therefore not written down here any more.** A hand-copied
order goes stale the moment an issue closes, and a stale order is worse than
none because it is read as current. It lives on the milestone instead, where it
cannot drift from the issues it orders:

```bash
gh api repos/marctjones/excise/milestones --jq '.[] | select(.state=="open") | "\(.title)\n\(.description)\n"'
```

What survives here is the *method*, which does not go stale — the two rules at
the top of this file, plus:

- **Instruments before fixes.** A fix landed before the gate that measures it
  is a fix nobody can show worked. RC3 orders #1046 and #1041 ahead of every
  repair for this reason.
- **An unknown cause is not schedulable, whatever its priority label.** RC3's
  #1040 is `priority: critical` and still cannot be assigned a fix, because
  nobody knows yet what to fix. It is scheduled as a *diagnosis*, after the
  instrument that gives it a population instead of one document.
- **Bounding damage outranks removing causes when the causes are plural.**
  Replacing a blunt fallback (#1044) caps the harm from every trigger at once,
  including the ones not yet found.

---

# Do we actually want these?

## Recommend CLOSING — decided by constraints already stated, not by fresh judgement

| # | why |
|---|---|
| **#705** AOT probe for `osx-x64` | Intel-Mac support for an audience of one on Apple Silicon. The release builds `osx-arm64`. Nobody will ever run this artifact |
| **#703** AOT probe for `win-x64` | Windows is not a target platform here. I reopened this when Linux AOT landed and Windows did not — that was completeness-seeking, not a need |
| **#895** Windows-only checkbox flake | not reproducible on the only dev machine, on a platform that is not a release gate |

These follow the standing decisions: audience of one, macOS is the release gate,
Windows/Linux are not development boxes. Recorded here so they are not
re-derived a third time.

## Recommend REFRAMING — the issue asks for the wrong thing

| # | asks for | should ask for |
|---|---|---|
| **#907** | triage 11 unexplained corpus pages | **fix the manifest statuses and the gate's oracle scoring.** Its own body shows 2 of 11 are corroborated refusals mis-pinned as `DECODE_ERROR`, and 21 of 35 defect-class pages are scoring artefacts. Only ~3 are genuine excise gaps |
| **#844** | research how to run parity gates on CI | **do the small change the issue already contains** — pin archival URLs (`irs-prior/p509--YYYY.pdf`), and self-host the .gov files, which 17 USC §105 permits. The analysis is done; nobody scheduled the work |
| **#894** | `priority: critical` | downgrade, or state that the label means **"do not remove the gate"**. Impact is mitigated by `check-test-count.sh`; the cause is probably a vstest bug with no user-facing symptom |

## The genuine product question — yours, not mine

**#900/#901/#902 (in-place text editing) versus #903 (document diff).**

You named in-place editing as the remaining gap versus commercial tools. #903
argues, credibly, that document comparison is *more* valuable for the actual use
case and *more* tractable here:

- every commercial PDF tool ships diff; no open-source viewer does it well
- it builds on `page.Letters`, which is complete — so it sidesteps cluster A
  entirely, the way redaction does
- #901 (same-line editing, no reflow) is genuinely achievable; #902 (reflow) is
  bounded by cluster A and is honest about its low ceiling on untagged files

They are not exclusive, but #901 and #903 are each multi-week. **Which one gets
built first is a product call, not an engineering one.**

## Everything else: keep, unchanged

#861, #909, #910, #911, #913, #914, #918, #919, #921, #923, #916, #899, #917,
#906, #908, #912, #695 — all have an established cause or an honest "cause
unknown, diagnose first" marker, and all still describe something true.

---

## Tier 1 — redaction correctness

The core purpose. Every entry has an established cause.

| # | issue | cause | why first |
|---|---|---|---|
| 1 | **#916** area redaction leaves 3 carriers holding the string | Established. Outline titles have no position; `InteractiveRedactionScrubber` reaches only annotations on *this* page overlapping *this* box. Measured, pinned by `KnownGaps_AreStillGaps`. | A redaction that reports success and leaves the name in a bookmark is the failure this program exists to prevent. **Needs a decision first** (wholesale / positional / surface-to-user), not code. |
| 2 | **#905** `ScrubTerms` substring-replaces | Established — `result.Replace(term, "", Ordinal)`, no word boundaries. | Corrupts documents it is meant to protect (`Theodore` → `odore`). It is the mechanism #897 deliberately routed around; it is still live for `RedactText`. Cheap. |
| ~~3~~ | ~~**#898**~~ **CLOSED** — re-redaction scrub verified | **Not established — this is a verification task.** | Cheap to answer, and a "yes" is a leak. Do it here because the answer determines whether it belongs in Tier 1 at all. |

## Tier 2 — user-visible defects, cause known, fix scoped

| # | issue | cause | notes |
|---|---|---|---|
| ~~4~~ | ~~**#924**~~ **CLOSED 2026-08-10** — search now uses the word path; the deep half is #899/#938 | Established. `PdfSearchService` reads `page.Text` for substring/regex, `GetWords()` only for whole-words. Measured: 3 phrases present in letters, absent from `page.Text`. | Highest impact-per-effort on the list. The data is already there. **Not a one-line swap** — phrase matching across word boundaries is the risk; the 180 corpus pages at 1.000 are the regression set. |
| 5 | **#923** open-and-save inflates files up to 2.79x | Established. Writer emits **no** `/ObjStm` (785 → 0) and no `/XRef` streams. Confirmed through the shipping CLI with zero changes applied; qpdf validates the output. | ⚠️ **Run t1, not t0.** This changes the serialization that every redaction saved-bytes assertion reads. veraPDF/PDF-A conformance is part of acceptance — PDF/A-1 forbids object streams. Check the encryption interaction (#639–#643) before starting. |
| 6 | **#919** `RedactText` re-parses per match — 10.6s on a six-page form | Established. `RedactArea` called once per match; each re-parses the content stream and invalidates the letter cache. | A ten-second freeze on the one operation where a user interrupting half-way is a security failure. Batch the rectangles per page. |

## Tier 3 — architecture that generates bug classes

Fixing these removes whole categories of future defect. #917 is the keystone.

| # | issue | cause | notes |
|---|---|---|---|
| 7 | **#917** same file opened into two unsynchronised `PdfDocument` instances | Established. `LoadDocumentInstancesAsync` opens twice; every mutation needs a hand-written mirror. | The cost is **correctness, not memory** (measured: usually +12–15%). It already produced one near-miss — #912's annotations were invisible on screen while five tests passed. Resolving it subsumes #922. |
| 8 | **#922** every mutation, including undo, serializes + reparses the whole document | Established. 149.7 ms / 234.6 MB per resync on a 4 MB file, from 10+ call sites. | Exists only to paper over #917. If #917 lands, this mostly evaporates — so **do not fix this first**. The `ToArray()` copy is real but only 0.2 ms; not the finding. |
| 9 | **#918** 13 sites read whole PDFs into memory; merge holds every source resident | Established. 127 MB vs 4.7 MB on a 122 MB file; merge measured linear at ~24 MB per 4 MB source. | CLI sites (5) are unblocked — no writability constraint there. GUI sites are blocked on #917. |

## Tier 4 — correctness debt, cause known

| # | issue | cause | notes |
|---|---|---|---|
| 10 | **#899** text assembly drops content on multi-column pages | Established, and **it is assembly, not extraction** — `page.Letters` has *more* characters than mutool finds; the loss is between the letter list and the string. | The deep fix behind #924. Does **not** affect redaction (verified — `RedactText` uses the letter path). |
| ~~11~~ | ~~**#906**~~ **CLOSED** — AOT reflection-binding warning resolved | Established — the compiler names it (IL2026/IL3050). | AOT is now the shipping macOS/Linux build path. A reflection binding that trims away is a runtime failure in the artifact users install. |
| 12 | **#908** `CffSubsetter` never called — CFF fonts embedded unsubsetted | Established. 25 test references, 0 production callers. | Interacts with #923: both inflate output. Sequence after #923 so the size effect is attributable. |
| 13 | **#912** remaining annotation rows (Square/Circle/Line/Arrow, then Ink/Polygon/Stamp) | Established. | Each new row **must mirror to the viewer document** until #917 lands, or the feature is invisible. |

## Tier 5 — dead code and tooling

Cheap, and each one reduces the chance of the next mistake.

| # | issue | notes |
|---|---|---|
| 14 | **#920** 283 lines of unreachable render machinery | Deletion. Do not re-add `CurrentPageImage` to make a test pass — nothing binds it. |
| 15 | **#914** `SKBitmapPool` entirely dead | Delete, or wire its diagnostics into the resource report for #861. |
| 16 | **#910** `Excise.App` has no public-API snapshot | The unwired-API gate's own headline justification (#896's `RedactWithOptions`) lives in the one assembly it cannot see. |
| 17 | **#911** Roslyn analyzers | Benchmark it against #920 — a rule set that misses 283 lines of dead private code is not configured. |
| 18 | **#913** 8 public members with neither callers nor tests | Includes the 4 tool-noise filters. |
| 19 | **#921** FDF/XFDF: 1,782 lines unreachable | **Decide wire-or-delete.** If wiring: CLI first, verify round-trip against a non-excise tool, and confirm `/P` bits 6/9 are honoured (#642). |
| 20 | **#909** coverage floors never ratchet up | Premise changed 2026-08-10 — see the issue comment before planning. |

## Tier 6 — symptom measured, CAUSE NOT ESTABLISHED

**Do not schedule these as fixes.** The first task in each is a diagnosis, and
until that lands nobody knows the size of the work.

| # | issue | what is known | what is not |
|---|---|---|---|
| 21 | **#915** two Altona pages take ~114s in Debug, 6–10x mutool in Release | The timings, and a ~42% slowdown between 2026-08-01 (80s) and 2026-08-10 (114s). | **Why.** Both figures are n=1. First task: bisect the nine-day window — the commit range is small. Time `SkiaRenderer.RenderPage` alone, document already open. |
| 22 | **#861** `Excise.App.Tests` peaks at 8.5 GB RSS | The peak, and that one chunk alone reaches 77% of it. | Cause. The issue says so itself. Suggestive that it is concentrated in render-heavy GUI classes, not accumulated — trace peak RSS *within* chunk05 rather than re-running the suite. |
| 23 | **#894** a discovered test produces no result | Still live — 1 of 3944 lost per run, a different one each time. | Cause. "vstest result channel" is a hypothesis. **Impact is mitigated** by `check-test-count.sh` in t0 and CI, so this is now low-value chasing of a probable vstest bug. |
| 24 | **#907** 11 unexplained defect-class corpus pages | That they are pinned. | Everything. This is a triage task by construction. |
| 25 | **#895** Windows-only checkbox flake | Fails on Windows CI. | Cause. Not reproducible on the only dev machine — macOS-first, so deprioritized deliberately. |
| 26 | **#904** the suite finds regressions but not first-discovery bugs | A real observation about test design. | Not a defect with a cause; it is a strategy question. |

## Tier 7 — features and epics

No root cause to establish; these are scope, not defects. Sequence by appetite,
not by this list: **#901** (text editing tier 1) → **#900** (epic) → **#695**
(click-everything harness) → **#903** (document diff) → **#902**, and the R7
deferred set (#773, #784, #785, #825, #844, #703, #705).

---

## Things to do before any of this

- **Tag a release.** Still v3.6.0 with 6 commits on `develop`, and the Native
  AOT release path has never executed. It should be watched the first time.
- Re-run `scripts/check-extraction-parity.sh` if touching #899 or #924 — the
  floors in `tests/extraction-parity/baseline.json` are the regression gate.
