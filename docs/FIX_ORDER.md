# Fix order

Generated 2026-08-10 from an audit of every open issue, re-examined the same day
for root cause and for whether the work is wanted at all. **Ordered by what each
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

## A. Text assembly — `page.Text` is built in the wrong order and drops content

**Root: #899.** `page.Letters` is COMPLETE (more characters than mutool finds).
The loss happens between the letter list and the assembled string, so this is
serialisation, not extraction — the opposite of what #637 assumed.

| issue | relationship |
|---|---|
| **#899** | owns the defect |
| #773 reading-order heuristics for untagged PDFs | the general form. #899 is the narrow, corpus-witnessed, gated case |
| #825 copy-whitespace deferred cases | its "reading-order ceiling" is this |
| #903 PDF diff | routes AROUND this by building on `page.Letters`, and says so. Not blocked by it — but the tokenisation mismatch it must handle (`market-`/`place`) is the same phenomenon |
| ~~#924 search~~ | **closed 2026-08-10** — the search half was separable and is fixed |

**What we really want:** correct reading order for multi-column untagged pages.
#924 proved the cheap half is separable: consumers that can use `page.Letters`
or `GetWords()` should, and only the ones that genuinely need a linear string
have to wait for #899.

## B. The document is open twice

**Root: #917.** Two `PdfDocument` instances of the same file, kept in sync by
hand.

| issue | relationship |
|---|---|
| **#917** | owns it |
| #922 mutation resync costs 150 ms / 234 MB | **exists only to paper over #917.** Its own body says so. Fixing it first is work #917 deletes |
| ~~#920 dead render machinery~~ | **closed** — was debris from the same split |
| #912 annotation types | every new row needs a hand-written viewer mirror UNTIL #917 lands |

**What we really want:** one instance. The memory saving is minor (measured
+12–15%); the point is that a hand-written mirror per feature is a bug factory,
and it already produced one near-miss.

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
| 3 | **#898** does re-redacting a redacted copy skip the metadata scrub? | **Not established — this is a verification task.** | Cheap to answer, and a "yes" is a leak. Do it here because the answer determines whether it belongs in Tier 1 at all. |

## Tier 2 — user-visible defects, cause known, fix scoped

| # | issue | cause | notes |
|---|---|---|---|
| 4 | **#924** default+regex search miss visible text | Established. `PdfSearchService` reads `page.Text` for substring/regex, `GetWords()` only for whole-words. Measured: 3 phrases present in letters, absent from `page.Text`. | Highest impact-per-effort on the list. The data is already there. **Not a one-line swap** — phrase matching across word boundaries is the risk; the 180 corpus pages at 1.000 are the regression set. |
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
| 11 | **#906** AOT warns `PdfViewerControl` uses reflection bindings | Established — the compiler names it (IL2026/IL3050). | AOT is now the shipping macOS/Linux build path. A reflection binding that trims away is a runtime failure in the artifact users install. |
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
