# Local gates for excise

**excise has no CI.** GitHub Actions was removed on 2026-09-04 (the "GitHub
Actions removal" section at the end says why and how to recover a workflow).
Every gate runs on this machine, where the corpora and the reference tools
are, and the only supported release artifact is a locally-built, unsigned
macOS app.

## The front door

```bash
scripts/test-tier.sh t0            # fast / pre-push (the installed hook runs exactly this, with the push range on stdin)
scripts/test-tier.sh t1            # merge gate (before anything lands on develop)
scripts/test-tier.sh full          # exec caffeinate -i scripts/run-full-suite.sh [--fresh|--only <re>|--allow-missing-corpora]; resume is the default there
scripts/run-full-suite.sh --status # what a full run has done and what is left; runs nothing
scripts/test-tier.sh t2            # release candidate: exec scripts/release-smoke.sh --release-tests
scripts/test-tier.sh t3            # t2 on this machine plus a printed reminder that Linux/Windows packaging is untested here
scripts/test-tier.sh --report [LOG_DIR|--latest] [--full] [--no-gh]   # exec scripts/report-gates.sh — a reducer, never runs a test
scripts/test-tier.sh --list [t0|t1|full|t2]                           # print the tier's rows from tests/gates.tsv, run nothing
scripts/test-tier.sh --install-hook                                   # once per clone; a hook installed before 2026-09-05 read no stdin — re-install it ("Base selection")
```

| Tier | Cost (headline; "Timings" has the dates and the load caveat) | When |
|------|------|------|
| `t0` | 2–4 min warm, ~5 min cold; 2–3× that under load | Before every push. `--install-hook` installs it as `.git/hooks/pre-push`. |
| `t1` | ~20–25 min (estimate) | Before merging anything to `develop`. |
| `full` | ≈3 h (estimate) | Weekly, and before a release candidate. Chunked, memory-bounded, resumable. |
| `t2` | ~30 min (pre-manifest budget) | Release candidate — `docs/RELEASE_CHECKLIST.md`. |
| `t3` | — | `t2` on this machine, plus a printed reminder that Linux/Windows packaging is untested here. **That packaging is a separate issue**; one machine cannot execute another platform's job. |

Chain semantics: **t0 ⊂ t1 ⊂ full**. `t2` is a curated Release-config set,
not a superset — a row runs there only when its `tiers` cell lists `t2`.

Tier is selected by blast radius — who gets hurt if this is wrong — not by
convenience. excise-specific rule: **you are your own third party.** A local
build you redact a real document with is a binary whose failure hurts someone,
silently — no crash, no error, the name is just still in the file. The
redaction gates are therefore unskippable at every tier that produces a binary
anyone could redact with, including a purely local build: `t0` runs the static
redaction-architecture guard, `t1` runs the full redaction suites and accepts
no flag to skip them — their rows are `checkpoint=never`, so `--resume` re-runs
them every time.

## tests/gates.tsv is the gate map

Every gate excise runs is one row of `tests/gates.tsv`. The three runners
(`scripts/test-tier.sh` for t0/t1, `scripts/run-full-suite.sh` for full,
`scripts/release-smoke.sh` for t2) hold **no step list of their own**: each
derives its plan through `runner_manifest_plan <tier>` in
`scripts/lib-runner.sh`, which validates the file first and refuses to run a
defective one (exit 2, every defect printed with its file:line). File order is
execution order within every tier.

**Do not hand-copy the rows into this document.** That is how the previous
version drifted (it listed 20 steps and was missing three real gates).
`scripts/test-tier.sh --list <tier>` prints a tier's rows, straight from the
manifest, and runs nothing — including the CKPT and KNOWN-ISSUE columns, which
are the live answer to "what is never checkpointed" and "what is accepted red
today".

Thirteen tab-separated columns; `-` is the mandatory empty-cell placeholder
(`read` strips an empty trailing field, which once turned an absent filter
into a csproj path that matched zero tests and exited 0):

| column | meaning |
|---|---|
| `name` | slug; equals the ledger row name. Chunk rows `<name>.chunkNN` are derived by `run-full-suite.sh` and inherit every column. |
| `class` | `BLOCK`, `IMPROVE`, `GRADE` or `SELFTEST` — see below. |
| `tiers` | comma set of `t0,t1,full,t2` with the chain semantics above. |
| `kind` | `script` (a command line run by `sh -c`), `test` (csproj/sln + `filter`), `project` (a whole csproj, unfiltered, trx emitted), `project-chunked` (whole csproj, split by class in `full`, whole elsewhere), `fn` (a `run_*_gate` function in `release-smoke.sh`; t2 only). |
| `target` | the command line or the csproj/sln. Environment placeholders `$CONFIG $LOG_DIR $GATE_ASYMMETRY_BASE $RELEASE_VERSION $AOT_EXTRA_ARGS $RUNNER_BUILD_ARGS`; plan-time placeholders `{TRX:row}` (one unchunked trx), `{TRXARGS:row}` (`--trx …` or the chunk union), `{TRXARGS?:row}` (the same, or nothing when the producer is not in this plan). A cell is never `eval`ed. |
| `filter` | `dotnet test --filter` for `kind=test`; `-` otherwise. |
| `ratchet` | the checked-in floor an `IMPROVE` row compares against (must exist); the design/artifact a `GRADE` row grades from. |
| `knownIssue` | `-`, `#N` (a FAIL is KNOWN while N is OPEN), or `#N/Substring` (KNOWN only when every failing test name — or, for a script row, the log — contains Substring; otherwise NEW naming the unmatched, so one issue cannot mask a second failing class). A CLOSED N makes the report fail STALE. **Never accept a red without one** — recipe under "Accepting a red". |
| `prereq` | space-separated `tool:NAME` (on PATH), `corpus:NAME` (`test-pdfs/NAME` non-empty), `env:NAME`, `file:GLOB`, `opt:NAME` (the runner was invoked with `--NAME`), or `-`. Checked before the row runs. |
| `prereqPolicy` | `fail` or `skip` — what a missing prerequisite (the column, or exit 77 from the gate) becomes. |
| `checkpoint` | `ok` or `never` — may `--resume` skip this row. `never` today (the CKPT column of `--list` is the live list): the redaction family (`redaction-architecture`, `redaction-oracles`, `redaction-oracles-selftest`, `redaction-suites`), `extraction-parity`, `build` and `gui-coverage-reset`. The validator refuses `checkpoint=ok` on any name matching `RUNNER_NEVER_CHECKPOINT` (`redaction|true-redaction|glyph|extraction-parity`) unless the row is a GRADE — `redaction-bench` is that one exemption, because a bench guarantees nothing. |
| `oracle` | `independent`, `spec`, `self`, `none` or `na` — who vouches for the verdict. |
| `note` | why this class and tier, and the measured cost with its date. Mandatory. |

### The four classes, and what each means for you

| class | the runner | what a red means |
|---|---|---|
| **BLOCK** | fails on any failure | a NEW red **blocks the build**: fix it, or file the issue and cite it in `knownIssue`. |
| **IMPROVE** | fails only on regression vs its `ratchet` file | keep working on it. Green means "no worse than the checked-in floor", never "good enough" — floors were set at whatever the behaviour was (CLAUDE.md, Limitations). |
| **GRADE** | never fails the run | a number against the reference tools, printed in the report's GRADES block (table under "The report"); when the bench produced nothing the block says `NO DATA` and the verdict is unaffected. A bench is a survey, not a net. |
| **SELFTEST** | fails when a gate can no longer fail | falsifiability (#1012): a red here means the gate it proves has lost its teeth, and every green from that gate since is suspect. |

### Adding a gate

1. Pick the tier by blast radius (above), not by how long the gate takes.
2. Append one 13-column row. `note` says why this class and tier and the
   measured cost with its date. A `script` row's script must be executable
   and must **exit 77** when a prerequisite is missing — and declare that
   prerequisite in `prereq` so the runner can skip the run entirely. An
   `IMPROVE` row names an existing ratchet file. A gate that could go vacuous
   (every test skips; a filter matches nothing) gets a `SELFTEST` row proving
   it can still fail.
3. Or, if the script is deliberately not a gate (a body another row calls, a
   download script, an analysis helper), add it to `tests/gates-tooling.txt`
   with its reason. `report-gates-selftest` fails when a script is in neither
   place.
4. `scripts/test-tier.sh --list <tier>` shows the row landed. A defective row
   fails every runner at plan time, `tests/gates.tsv:<line>: <defect>`, exit 2:
   duplicate name, missing note, non-executable script, missing ratchet or
   target, a `{TRX:row}` reference to a later row or to one absent from the
   tier, an `fn` row outside `t2`, or a redaction-named row with
   `checkpoint=ok`.

### Accepting a red

1. File the issue.
2. Put `#N` in the row's `knownIssue` cell — or `#N/Substring`, scoped to the
   failing test class or to a token the gate prints in its log — and say why
   in `note`. Scope it whenever you can: an unscoped `#N` makes **every**
   failure of that row KNOWN while N is open, so a second, unrelated failing
   class hides behind the first.
3. Commit it. The acceptance lives in the manifest, so the pre-push hook
   honours it: the row reads KNOWN and the exit is 0.
4. It expires by itself. When N closes, the report fails STALE until the cell
   is cleared — offline too, through `logs/runner-state/known-issues/<N>.rec`.
   A second failing class, or a log without the token, reads NEW.

Worked example: "Base selection" below, where the acceptance is scoped to the
`base=<sha>` line the gate prints.

## The report

Every runner ends with `scripts/report-gates.sh <LOG_DIR>` (body:
`scripts/report_gates.py`), **and its exit code is the runner's exit code.** It
is a pure reducer over `<LOG_DIR>/plan.tsv` (what was promised),
`<LOG_DIR>/ledger.jsonl` (what happened — class and knownIssue travel with the
row, so a report on an old run is honest to that run), the trx/log of failing
rows, the GRADE artifacts, and the newest prior `report.json` of the same tier.
It runs nothing; the only process it may spawn is `gh issue view`.
`scripts/test-tier.sh --report [LOG_DIR|--latest] [--full] [--no-gh]` re-prints
a report at any time (`--latest` = the newest run directory, by the ledger's
newest `recorded` timestamp, that has both a `plan.tsv` and a non-empty
ledger; symlinks such as `logs/release-smoke_latest` are skipped).

Row verdicts, and what to do about each:

| verdict | meaning | action |
|---|---|---|
| `PASS` | passed, or passed from a checkpoint (labelled with the evidence date). | none. |
| `NEW` | a red with no `knownIssue`, or one whose `#N/Substring` qualifier did not match every failure. **Blocks.** | fix it, or file the issue and cite it ("Accepting a red"). |
| `KNOWN` | a red whose `#N` is OPEN (or could not be verified). Does not block. | keep working on N; nothing to do for this run. |
| `STALE` | a `#N` cited in the tier's plan whose issue is CLOSED — evaluated for every `#N`, passing rows included. **Fails.** | delete the acceptance from `tests/gates.tsv`. |
| `SKIPPED` | a `prereqPolicy=skip` row whose prerequisite was missing (exit 77). Visible, never green: the verdict reads `PASS with N SKIPPED`, never bare `PASS`. | install the prerequisite (`scripts/check-test-prereqs.sh` names it; the `scripts/download-*.sh` scripts fetch corpora and vendored tools) or leave it visibly skipped — never count it as evidence for a release. |
| `NOT RUN` | a plan row with no ledger row — an interrupted run. | `full` re-runs resume by default; `t0`/`t1`/`t2` need `--resume`. See "When `full` refuses to start" for the exit-75 case. |
| `NO DATA` | a GRADE row that produced no number. Appears in the GRADES block only. | verdict unaffected; run `full` for a fresh number. |

Exit codes: **0** clean (possibly `PASS with N SKIPPED`); **1** any NEW red or
STALE acceptance; **3** any row NOT RUN (an interrupted run can never read
green); **2** nothing to report (no or unreadable ledger/plan).

The summary is at most 20 lines: a header, the VERDICT with the per-class tally,
only the non-PASS rows (NEW first, then KNOWN, STALE, SKIPPED, NOT RUN; capped
with `+N more (--full)`), one IMPROVE line, the GRADES block, one footer.
`--full` appends every row with its status, rc, duration, class, knownIssue,
log and trx. This is the real report of the `t0` run of 2026-09-05 03:21
(`logs/test-tier_t0_20260905_032147`; the tree was dirty with the three
uncommitted docs that describe this design) — the shape every pre-push run
prints, including the one KNOWN line every pre-push run shows until the
backlog lands on `origin/develop` (a different `base=` token) or #1358 closes:

```
excise gates  t0 @7acd63ff (tree DIRTY)  2026-09-05 03:21→03:30 (8m29s)  logs/test-tier_t0_20260905_032147
VERDICT PASS (exit 0)   BLOCK 20/21 pass · IMPROVE 1/1 at-or-above floor · SELFTEST 12/12 · GRADE 0/0 reported   known 1 · skipped 0 · not-run 0 · stale 0 · checkpointed 0
KNOWN    gate-asymmetry         BLOCK    #1358 OPEN   log matches /base=a87dc32aa8c2: together.
IMPROVE  held: unwired-api 123 baselined (=)
GRADES vs reference tools
  conformance   NO DATA — no corpus-scan-* agreement line in this run
                registry strict 0.5% (929/964 modes unknown) — measures paperwork, not code (milestone RC22)
  extraction    0.9999 of mutool's letters over 332 pages, worst floor 0.946 (=)   [baseline tests/extraction-parity/baseline.json 2026-08-13 (not from this run)]
  redaction     secure 0.969 A-  vs iText 0.469 F · PyMuPDF 0.629 C · raster 0.984 A   n=127  (=)   [redaction-bench history 2026-08-27 (not from this run)]
  render perf   wall ×3.5 mutool / ×1.5 pdftocairo / ×2.6 gs / ×0.6 pdfbox (median of 6 fixtures); RSS ×1.7 mutool; regressionGate PASS (=)   [2026-08-29 (not from this run)]
  annotations   NO DATA — no logs/annotation-bench_*/summary.txt from this run
  image codecs  PIXEL_EXACT 445 · MATCHES_ACCEPTED 38 · FAIL 6 · NEEDS_REVIEW 7 · NON_RENDERABLE 3 of 499 pages (=)   [2026-08-19 (not from this run)]
  bench design  NO DATA — no logs/test-tier_t0_20260905_032147/bench-design-coverage.log in this run
PASS 33 rows (--full lists every row)   knownIssue verification: gh reachable, 1 issue checked
```

A NEW row looks like this (two lines of the 2026-08-31 full-suite ledger,
`logs/full-suite_Debug_20260831_153932`, captured 2026-09-05 before a prior
`report.json` existed; both were real, un-cited failures at the time):

```
NEW      Excise.App.Tests.chunk02     BLOCK    rc=1   17s   1 failed: PublicApiApprovalTests.ExciseApp_PublicApi_MatchesApprovedBaseline (54 passed, 0 skipped)  ← no knownIssue: fix it, or file the issue and cite it in tests/gates.tsv
NEW      Excise.App.Tests.chunk09     BLOCK    rc=1    3s   1 failed: RevealHiddenTextTests.RevealToggle_FlushesHighlightsForHiddenText (168 passed, 0 skipped)  ← no knownIssue: fix it, or file the issue and cite it in tests/gates.tsv
```

**Δ.** Every IMPROVE and GRADE number carries its movement against the newest
prior `report.json` of the same tier: `(=)` unchanged, `(Δ ±x)` moved,
`(no prior)`. The report writes `<LOG_DIR>/report.json` for the next one to
diff against and never treats its own file as the prior.

**GRADES.** The block is the same seven lines in every tier, mapped by
`GRADE_ROWS` in `scripts/report_gates.py` (re-derive from there, not from
here). Only `full` runs the GRADE rows; `extraction-parity` (a `t1`/`t2`
IMPROVE row) refreshes the extraction line in those tiers. Every other line in
a `t0`/`t1` report restates the newest artifact on disk with its own date and
`(not from this run)`, or reads `NO DATA` — nothing to act on there.

| grade line | producing row | the number | oracle |
|---|---|---|---|
| `conformance` | `corpus-scan-verapdf`, `-pdfjs`, `-pdfium`, `-isartor` | each scan's "excise behaves correctly on N/M" — pages agreeing with the oracle majority, per corpus | mutool, pdftocairo, Ghostscript, PDFBox, PDFium |
| (`registry`, printed under conformance) | none — reads the generated capability scorecard | strict-mode share of the PDF-spec capability registry; **measures paperwork, not code** (milestone RC22) | none |
| `extraction` | `extraction-parity` | letters extracted as a fraction of mutool's, over the smoke corpus, plus the worst page floor | mutool |
| `redaction` | `redaction-bench` | security × fidelity score vs the comparison redactors, n fixtures; scored by the ONE scorer `scripts/archive-bench-run.sh` (`REDACTION_BENCH_HISTORY` redirects its history line into the run directory) | iText, PyMuPDF, raster |
| `render perf` | `reference-performance` | fresh-process wall/CPU/RSS ratios vs the reference renderers, median of the fixtures; `regressionGate` reported, never enforced | mutool, pdftocairo, Ghostscript, PDFBox |
| `annotations` | `annotation-bench` | per-annotation rendering agreement, Group A/B separately (#1053) | every independent renderer |
| `image codecs` | `image-conformance` | PIXEL_EXACT / MATCHES_ACCEPTED / FAIL / NEEDS_REVIEW / NON_RENDERABLE page counts vs the image feature matrix | all oracles |
| `bench design` | `bench-design-coverage` | redaction-bench completeness per tier × category (#1185); static | none |

**Known-issue memory.** For each distinct `#N` in the tier's plan the report
asks `gh issue view N --json state,title` once (10 s timeout; after the first
network failure the rest are left "unverified", which keeps the row KNOWN).
Every successful answer is written to `logs/runner-state/known-issues/<N>.rec`
(sentinel-validated, tmp-then-rename). A record that remembers CLOSED makes the
row STALE **even offline** — a laptop off wifi cannot launder a closed issue.
`--no-gh` skips the network but still reads the records.

The reducer decides the exit code of the pre-push hook, so it has its own
SELFTEST row (`report-gates-selftest`, `scripts/test-report-gates.sh`): hermetic,
a few seconds (1.2 s measured 2026-09-05 idle, 4.0 s under load the same
night), pins every verdict rule, the exit codes, the offline memory, the
20-line cap and Δ. A reducer whose failure branches were never seen red is
#1012's shape.

## Exit 77 — the "prerequisite missing" protocol

A gate that cannot run here says so with **exit 77, never 0**. The manifest
decides what that means through `prereqPolicy`: `skip` → a visible SKIPPED row;
`fail` → FAIL like any other red. The `prereq` column gives the same verdict
without paying for the run; exit 77 catches what the column did not enumerate.
`dotnet test` rows cannot produce a 77 — their hole (a tool vanishes, every
test skips, the run stays green) is covered by the oracle floors and the skip
budgets instead.

This closed five gates that used to exit 0 on a missing prerequisite:
`run-accessibility-smoke.sh` (platform probe not PASS — policy `skip`, the one
row that stays SKIPPED until Accessibility permission is granted to the
terminal), `run-visual-regression-local.sh` (any group skipped — the `visual`
row is policy `skip`, so it reads SKIPPED), `check-perf-budgets.sh` (corpus or
budgets file missing), `check-copy-whitespace-parity.sh` (tool or corpus
missing, non-strict branch) and `verify-bench-tiers.sh` (zero files verified)
— the last three are policy `fail` rows, so their 77 reads FAIL.
`check-gate-asymmetry.sh`'s `GATE_ASYMMETRY_ALLOW_NO_BASE=1` escape hatch
exits 77 too, and because `gate-asymmetry` is `prereqPolicy=fail` a run without
a base reads as a FAIL row — never a green. The flag-gated release rows
(`tests`, `visual`, `package`, `packaged-gui`, `aot`) use `opt:NAME` with policy
`skip`: a `t2` run without the flag shows them SKIPPED rather than silently
omitting them.

## Base selection

`gate-asymmetry` (#618: a perf-path change may not rewrite a correctness
expectation in the same range) compares HEAD against a base. Its row is
`scripts/check-gate-asymmetry.sh $GATE_ASYMMETRY_BASE`; every runner exports
that variable from `runner_gate_asymmetry_base <tier>`, in this order:

1. **The pre-push hook.** git feeds `<local ref> <local sha> <remote ref>
   <remote sha>` per pushed ref on stdin; the hook exports the remote sha as
   `GATE_ASYMMETRY_BASE`. That IS the range the gate is defined over ("two
   pushes, not two commits"). An all-zero sha (a new remote branch) falls
   through to 2. **A hook installed before 2026-09-05 reads no stdin — run
   `scripts/test-tier.sh --install-hook` once in every clone.**
2. **A manual tier run** uses the last commit at which this tier finished with
   no NEW failure: `logs/runner-state/tier-pass/<tier>.rec`, written on a
   report exit 0 (a `full` pass also records `t1` and `t0`; a `t1` pass records
   `t0`). Keyed on content, never existence: the record counts only if its
   `--CKPT-OK--` sentinel is the last line, the sha is a commit that is an
   ancestor of HEAD, and its manifest fingerprint equals the current
   `tests/gates.tsv`; otherwise it is ignored. Branch is deliberately not in
   the key — ancestry is the real relation.
3. **Fallback**, the first run on a machine: `git merge-base origin/develop HEAD`.

The gate prints `base=<sha>` so an acceptance can be **scoped** to one range —
the worked example for "Accepting a red". As of 2026-09-05 the
`gate-asymmetry` row carries `#1358/base=a87dc32aa8c2`: KNOWN only for the
backlog range starting at `origin/develop` = `a87dc32a` (the acceptance is
base-scoped, not count-scoped; `git rev-list --count a87dc32a..HEAD` says how
long it is today). Against any other base the same red reads NEW, and the
acceptance retires itself when #1358 closes (STALE) or `origin/develop` moves
(different token). Bootstrap on day one: no record → base `a87dc32a` → the gate
fires on the backlog → KNOWN → exit 0 → record written at HEAD → every later
manual run diffs a real range, where a failure is NEW.

## --resume

- `full` **resumes by default**; `--fresh` discards the checkpoints for this
  tree and starts over. `--resume` is opt-in on `t0`, `t1` and `t2` so the
  pre-push hook keeps skipping nothing.
- Markers live under `logs/runner-state/<runner>_<config>_<branch>[-dirty]/` —
  the key is label, configuration, **branch** and dirtiness, deliberately not
  the commit (#1027: a sha in the key moved every commit into a fresh empty
  state directory, so a long run could only finish by passing first time with
  no commits during it). The commit each step ran at is recorded inside its
  marker and reported as a span. Markers are keyed on **content**: the row's
  command hash (kind, target, filter). A changed row re-runs. A marker written
  before 2026-09-05 carries no command hash and re-runs once.
- A marker from a different commit is accepted and the span of commits is
  reported. Anything torn, truncated or unsentinelled re-runs: checkpoints
  fail toward re-running, never toward skipping.
- `checkpoint=never` rows re-run on every invocation, resumed or not — the
  seven listed under the `checkpoint` column above: the redaction family,
  `extraction-parity`, `build` (a checkpointed build let a stale
  `Excise.Ocr.dll` through on 2026-08-31), `gui-coverage-reset`. No flag skips
  them.
- A `--no-build` row must prove the binary is fresh (`scripts/assert-fresh.sh`
  before every `dotnet test --no-build`), and a `dotnet test` command that
  executed zero tests is a FAIL, not a pass.

### When `full` refuses to start (exit 75)

`run-full-suite.sh` guards the machine before each step, and aborts with
**exit 75** (`RUNNER_EXIT_RESOURCE`) in two cases: the data volume has under
`RUNNER_MIN_FREE_GIB` (20) GiB free — immediate, because macOS grows swap there
and a memory spike without headroom panics the box instead of failing a test
(2026-07-29) — or `kern.memorystatus_vm_pressure_level` stays above
`RUNNER_MAX_PRESSURE` (2) for `RUNNER_PRESSURE_RETRIES` checks. Both happen
before the report runs, so `--report --latest` on that directory reads NOT RUN
/ exit 3 — expected, not a second failure. Checkpoints are kept. Free `logs/`
and `artifacts/` with `scripts/clean-test-artifacts.sh --keep N` (never
`test-pdfs/` — that is the gitignored corpora, and the download scripts would
have to re-fetch every one), wait for the machine to go idle, check
`scripts/run-full-suite.sh --status` for what is left, and re-run
`scripts/test-tier.sh full` — it resumes.

## The independent-oracle subsets

The Differential + Corpus tests — the ones that check excise against mutool,
Ghostscript, pdftocairo, pdftoppm, qpdf, PDFBox and PDFium — used to run only
on the Linux job. Nothing in them was Linux-specific; they needed reference
tools and corpora, which this machine has. They are `t1` rows: `oracle-tools`
(every tool — mutool, gs, pdftocairo, pdftoppm, pdftotext, pdfsig, qpdf,
tesseract — the PDFBox jar, the PDFium library and the smoke and federal
corpora resolve, or FAIL before any oracle row can skip its way green),
`rendering-oracles` (3,701 passed / 1 failed / 919 skipped measured
2026-09-04; the one failure is accepted through a class-scoped `knownIssue` —
see the KNOWN-ISSUE column of `--list t1` — so a second failing class reads
NEW), `app-oracles` (13/13), `core-oracles` (14/14), each with a `*-floor`
row, plus the parity ratchets `extraction-parity`, `copy-whitespace-parity`
and `advance-parity`.

**Why the floors.** `dotnet test` exits 0 when every test skipped and 0 when
every test passed. These tests gate on `Assert.SkipUnless(IsAvailable)`, so a
vanished tool turns the whole subset into skips and the gate goes green having
verified nothing. The rendering floor is **3000, not the Linux job's 60** —
that 60 was what a corpus-less runner could reach; carrying it over would have
let 3,600 tests vanish in silence. 875 of the 919 skips (2026-09-04) are
`RedactionCollateralHarness` fixtures with under 200 characters of text (#1046
documents that as intended). Do not lower a floor to make a run pass.

`rendering-deterministic` deliberately EXCLUDES `Corpus` and `Differential`;
the oracle rows are its complement. Before this, `t1` ran the exact inverse of
the oracle job, so the tests that exist because excise must not be its own
oracle ran in no tier at all. Every runner exports `EXCISE_PDFBOX_JAR` when the
jar is vendored — PDFBox is gated on the variable, not the file (#935).

## tests/gates-tooling.txt — the companion

Every script under `scripts/` that is deliberately **not** a row is listed
there, one per line with its reason (skip-allowlist shape, #854): bodies of
gates, download scripts, fixture generators, analysis helpers, the ad-hoc
runners. `report-gates-selftest` fails when a script is in neither place, so a
new script must either become a gate or be justified there ("Adding a gate").
Notable entries: `scripts/check-coverage-floor.sh` (coverage is an
observation, not a gate; wiring the floors is #1359),
`scripts/run-gui-display-sweep.sh` (the manual sharded fallback for the
144-page sweep every full `Excise.App.Tests` pass already runs),
`scripts/check-contract-manifest-agreement.sh` (the same comparison runs in
`t0` inside `Excise.Cli.Tests`), and `scripts/t.sh` (#950), the ad-hoc
single-project runner that replaces the deleted wrappers:

```bash
scripts/t.sh Excise.Core.Tests/Excise.Core.Tests.csproj --filter Redaction
```

## Timings, honestly

Measured with dates; re-measure before quoting. The rows' `note` cells carry
the per-gate numbers and the report prints `durationSeconds` per row.

**Load matters.** Under load the same tier takes 2–3× longer: the 2026-09-05
03:21 `t0` (the report shown above) took **8m29s** with a load average
reported at 19–22, against 2–4 min idle. A slow run is not a red; a red under
load may be a false one — the 144-page display sweep in `Excise.App.Tests` has
produced false reds from CPU contention three times (#619, CLAUDE.md). Run a
tier alone.

- **t0: 2–4 min** over eight warm runs 2026-08-30..09-04 (log-mtime spans
  1m57s–3m49s; the run of 2026-09-05 00:10, 2m42s). Budget ~5 min for a cold
  build. The "~30s" this document and CLAUDE.md quoted until 2026-09-05 was
  stale by a factor of 5–10.
- **t1: ~20–25 min**, a 2026-09-04 estimate on an idle machine — no t1 ledger
  exists yet. Its two biggest rows measured under load on 2026-08-31:
  `app-tests-unchunked-evidence` 1467 s, `redaction-suites` 525 s. The first
  manifest-driven t1 writes the ledger that settles it.
- **full: ≈3 h.** The only complete ledger (2026-08-31) sums to ~80 min of
  executed rows with 24 checkpointed, so it is a floor, not a measurement.
  Now included by chain inheritance and the new rows: the t1 rows (~20–25
  min), `redaction-bench` 28m34s (2026-08-29), `image-conformance` ~6 min
  (artifact mtimes 2026-08-18), `reference-performance` ~4–5 min (derived from
  2026-08-29 walls); unmeasured: `annotation-bench`, `render-quality-scan`,
  `license-manifest`. The t1 oracle rows re-run inside full's chunked
  whole-project rows — roughly 20 minutes of duplication that buys the floor
  checks, which need the filtered trx.
- **t2: ~30 min** is the pre-manifest release-smoke budget; the 2026-08-31
  `release-smoke_*` directory on disk is the 1m31s delegation from
  run-full-suite, not a t2. The first manifest-driven t2 writes its ledger.

## A future GitHub Action

tests/gates.tsv is what a future GitHub Action will consume — one job per tier, `runner_manifest_plan <tier>` is its only reader. Nothing is built for Actions here.

## Coverage

`tests/coverage-floors.tsv` carries only the `full` profile — corpora and
reference tools present, unfiltered. The `ci` rows were deleted with the
runner they described. Coverage stays an **observation**
(`docs/RELEASE_CHECKLIST.md`); `scripts/check-coverage-floor.sh` is tooling,
`coverage-floor-selftest` in `t0` keeps the ratchet mechanism honest, and
wiring the floors into a tier is #1359.

## What changed 2026-09-05

- `tests/gates.tsv` became the single declaration; the runners' own step lists
  are gone, and `scripts/report-gates.sh` decides every exit code.
- **Deleted** the six dead runners (`run-all-tests`, `run-atomic-tests`,
  `run-passing-tests`, `run-avalonia-tests-linux`, `run-coverage`,
  `run-long-tests`) and the two wrappers `run-corpus-tests.sh` (a strict
  subset of `rendering-oracles`, without the freshness or zero-tests guards)
  and `run-automation-tests.sh`. `scripts/t.sh` is the ad-hoc form;
  `report-gates-selftest` is the no-resurrection pin.
- `run-full-suite.sh --everything` is a no-op (full is everything);
  `--allow-missing-corpora` is the way through a missing corpus and never
  drops the scans from the plan. `check-pdf-capability-registry.sh` restores
  the 16 generated files after a passing diff (#1357).
- **Known-red on 2026-09-05**, all KNOWN via the manifest (the KNOWN-ISSUE
  column of `--list <tier>` is the live list): `gate-asymmetry` (#1358,
  scoped to base `a87dc32a`), `rendering-oracles` (#1361, scoped to
  `CertainChannelReferenceComparisonTests`), `corpus-scan-pdfjs` (#1363,
  **unscoped** — any red on that row reads KNOWN while #1363 is open; the two
  `MISSING_CONTENT` pages named in its note are the reason, not a qualifier).
  Anything else red is NEW.

## GitHub Actions removal

Removed at `a708774d` (the commit before the teardown). To recover a workflow:

```bash
git show a708774d:.github/workflows/ci.yml
```

Restoring GitHub Actions for **Linux and Windows packaging only** — no gates,
no cross-platform test matrix — is tracked as a separate issue. The gates stay
local.
