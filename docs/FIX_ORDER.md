# Fix order

Generated 2026-08-10 from an audit of all 38 open issues. **Ordered by what each
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
