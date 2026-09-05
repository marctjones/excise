# PDF specification capability registry

This is the proposed authoritative registry for Excise's ISO 32000 capability
contract.  It is deliberately a directory of small, spec-oriented JSON files:
that keeps ownership, reviews, and future ISO changes local.  `registry.json`
is the only entry point; do not infer support from source paths or broad test
success.

Each capability records two independent facts:

1. The **product decision**: required for the chosen product, supported,
   preserve-only, deliberately blocked, deferred, or still undecided.
2. The **implementation state by mode**: parse, preserve, render, extract,
   mutate, write, author, and execute.  A green renderer test cannot turn a
   preserve-only feature into an implemented editor feature.

Evidence is named rather than implied.  An entry can link source, tests/gates,
atomic fixtures, corpus cases, differential oracles, benchmarks, architecture
records, issues, and user-facing documentation.  The registry holds references
and short explanations only; it never copies ISO text.

Every leaf also has a `tracking` contract: processor roles, normalized support
level, source pin and errata disposition, review commit, owner, implementation
links, positive tests, negative/conservation tests, fixture/corpus/reference
tool consumers, issue links, limitations, and an explicit evidence gap when a
claim is not yet paired with both kinds of test.  Empty arrays are meaningful:
they say the evidence is absent rather than making support look complete.

`legacy-sources.json` makes the existing PDF 2.0 renderer, operator, image,
and annotation matrices first-class migration sources.  They remain active
until their consumers are migrated and this registry's validator is made a CI
gate.  Consequently this first change establishes the data contract without
silently changing any conformance claim.

`product-policy.json` is the accepted product boundary. It makes PDF 1.7 a
first-class legacy compatibility target while PDF 2.0 plus errata is the
current semantic reference. Output preserves the input version where safely
possible and moves to PDF 2.0 when the selected workflow requires it or PDFE
cannot safely author the earlier form. `sections/product-capabilities.json`
tracks non-ISO product behavior—security UX, privacy copies, OCR, audit logs,
and platform integration—with the same evidence rules while making that
distinction explicit.

## Development loop

Run `scripts/check-pdf-capability-registry.sh` before changing a capability
claim. It validates references, regenerates the summary and scorecard, and
rejects stale generated views. `generated/capability-scorecard.md` reports
implementation, measurement, verification-plan, and unknown coverage by
section; unknown is deliberately not credit.

`benchmarks.json` maps critical paths to their owning capabilities and local
benchmark commands. Run `python3 scripts/run-pdf-capability-benchmarks.py
--scenario redact` (or another scenario) to write machine-readable local
timing evidence; the runner reports p50/p95 process-wall samples and any
unmeasured requested metrics. Benchmark success is never conformance or
security evidence; the capability's verification contract remains authoritative
for that.

`corpus-policy.json` governs all corpus inputs. Before a release baseline,
generate a hash inventory with `scripts/build-pdf-corpus-governance.py
--hash-files` and inspect exact and same-name near-duplicate candidates with
`scripts/analyze-pdf-corpus-duplicates.py`. Corpus membership cannot silently
change an allowlist, parity denominator, or performance baseline: those paths
are named stability contracts in that policy.

`renderer-evidence-map.json` and its generated view inventory every xUnit
test method in `Excise.Rendering.Tests`. Each test is assigned one or more
specific renderer facets and parent capability modes, or an explicit general
integration fallback. These are review candidates only: promotion into an ISO
capability requires the named assertion and limitation to be copied into the
leaf's evidence contract.

`evidence-maps.json` extends this discipline to every Core, Rendering, App,
and CLI source file and test contract. The generated implementation and
test-suite maps have explicit facet links or a visible integration fallback;
they make evidence gathering complete without mistaking automated classification
for proof.

`generated/renderer-promotion-queue.json` turns the complete renderer map into
one review contract per target capability mode. It names candidate source,
direct-test, and independent-evidence links plus the evidence still required
for partial or strict promotion. Reviewers promote those links deliberately;
the queue itself does not change a support score.

`generated/evidence-deficiency-report.json` is the operational work queue. It
has one row for every required/supported mode, records current state and
candidate links, identifies missing source/test/fixture/oracle/contract
evidence, and ranks the next review or implementation action. It calls a
missing map a traceability deficiency, never proof that the code is absent.

`test-outcomes.json` and `scripts/collect-pdf-test-outcomes.py` import TRX
results with host, Git revision, and .NET version. This closes the distinction
between a static test reference and a recorded passing/failing execution; stale
results remain historical rather than silently proving current behavior.
The import runs once per full run, not in the t0 gate: the `pdf-registry-outcomes`
GRADE row calls `scripts/check-pdf-capability-registry.sh --refresh-outcomes $LOG_DIR`,
which passes the trx of every test row in that run's `ledger.jsonl` (checkpointed
rows included) as explicit `--trx` arguments, reports the delta against the
committed snapshot and stashes the regenerated files under
`$LOG_DIR/registry-outcomes/`; `--adopt $LOG_DIR` moves them into the tree for
commit. release-smoke (t2) results are not imported. The t0
`pdf-capability-registry` row reads the committed snapshot and never regenerates
it, so a run's own results cannot redden it (#1366).

`scripts/collect-pdf-reference-tool-evidence.py` probes every registered
reference tool's executable and version command without giving it a PDF. Its
generated availability record is regenerated by the registry check. Evince is
tracked only as an optional interactive human-review viewer: it is never a
headless rendering, extraction, or redaction oracle.

`generated/atomic-fixture-evidence.json` inventories every target mode's
explicit atomic-fixture contract, the resolved xUnit test method, and recorded
TRX outcome. `scorecard-groups.json` provides the major-category rollup; the
existing section scores are its drill-down subcategories.
