# Changed-symbol review (#1945)

`scripts/code-health-review.py` combines the inventory, structural, duplication,
dependency, test-strength and diagnostic evidence. It is an individual-rule
review ratchet, never a composite score or behavioral release verdict. Existing
debt remains in `existingDebt`; unrelated changes do not fail for that debt.
Increases require review: structural measurements, duplicate occurrence counts,
diagnostic counts, new dead/unwired candidates, API removal/signature changes,
and lost direct-test attribution. These are conservative review triggers, not
proof of defects. Adding a statement may legitimately increase a count; record
the reason and a bounded exception rather than hiding the increase.

Metrics cannot justify weakening behavioral tests, deleting code, or changing
release claims. Missing coverage/execution input and unbound symbols stay
unknown. Static oracle calls remain candidates rather than executed assertions.
Dependency candidates retain dynamic-mechanism blind spots; reviewers must read
the dependency report and API diff. A symbol rename is removed plus added.

## Stable snapshots

Finish concurrent source edits first. Stage intended new/deleted source and
project files so the tracked-only syntax tools see the same population. Capture
the six reports in a temporary directory outside the checkout using their
documented commands, naming them `inventory.json`, `structural.json`,
`duplication.json`, `dependencies.json`, `testStrength.json`, and
`diagnostics.json`. Use `inventory.json` as the test evidence input. Capture
diagnostics to stdout without `--write-baseline`, passing the freshly captured
`--inventory` explicitly. Diagnostics rejects stale or incomplete inventories.
All captures must use the same immutable source, toolchain and configuration.

Run each producer twice and compare bytes where its contract promises
determinism. Record source/configuration fingerprints and inspect Git status
before and after the capture. Reject concurrent edits or mixed snapshots.
The dependency collector fingerprints raw bytes; syntax tools normalize LF.
The snapshot producer explicitly attests this cross-contract association.
The report requires the diagnostic inventory fingerprint. A file hash alone
cannot prove that a diagnostic capture used fresh source.

The single-command driver in [README.md](README.md) performs this sequence,
adds compact summaries, directed graphs and entry-point views, and checks the
whole source/configuration identity before and after collection. It keeps raw
evidence outside the checkout and distinguishes dirty diagnostics from clean
capture candidates; neither is automatically a reviewed baseline.

```sh
python3 scripts/code-health-review.py bundle --directory /tmp/d17-before --reviewer REVIEWER > /tmp/d17-before/snapshot.json
python3 scripts/code-health-review.py bundle --directory /tmp/d17-after --reviewer REVIEWER > /tmp/d17-after/snapshot.json
python3 scripts/code-health-review.py report --before /tmp/d17-before/snapshot.json --after /tmp/d17-after/snapshot.json --as-of YYYY-MM-DD > /tmp/d17-review.json
python3 scripts/code-health-review.py self-test
```

Reports go to stdout and never modify source, tracked baselines or registries.
Exit 0 means no unexcepted increase, 1 means explicit review required (the full
report is still emitted), and 2 means invalid/missing/incompatible evidence.
The explicit date makes expiry evaluation reproducible. Manifests pin every
artifact hash; the final report pins both manifests, the script and registry.
Input-hash and inventory mismatches fail. Contract/tool changes require
recapturing both sides under the reviewed tool; they never reset legacy debt.

Store reviewed baselines only from a stable, reviewed source snapshot. A dirty
snapshot is diagnostic evidence, never a clean release baseline. Review baseline
diffs by rule and symbol; do not accept a mass baseline refresh to make a failing
ratchet pass. Preserve the prior evidence and review report alongside its
replacement. This initial rollout wires the planted-failure self-test into t0;
production snapshot comparison requires a reviewed before/current pair. It
must be run for D17 review after both snapshots exist; the self-test alone does
not establish a production baseline or claim repository-wide compliance.

## Exceptions

`exceptions.json` begins empty. Each entry must contain exact `rule` and
`symbol`, a repository issue URL in `issue`, `rationale`, accountable `owner`,
named `reviewer`, ISO `expires`, `reviewCondition`, and integer `maximum`.
The maximum bounds the current measured value, not an unlimited delta.
Wildcard and duplicate entries fail validation. Expired exceptions remain in
the report but never suppress an increase. Re-review the entry when its review
condition occurs, even before expiry. No bypass flag or automatic acceptance
exists. An example entry (not an active allowance):

```json
{
  "rule": "structural.Cyclomatic",
  "symbol": "Excise.Core/Excise.Core.csproj::M:Example.Method",
  "issue": "https://github.com/marctjones/excise/issues/1945",
  "rationale": "Reviewed explicit malformed-input branches",
  "owner": "maintainer",
  "reviewer": "reviewing-maintainer",
  "expires": "2026-11-01",
  "reviewCondition": "Revisit on the next change to this method",
  "maximum": 12
}
```

## Baseline candidates and adoption

A baseline is a clean-checkout capture that a named human has reviewed. The
tooling never adopts one. `baselines/candidate-<commit>/` holds the manifest,
capture record and summary of a clean capture; its `PENDING-REVIEW.md` states
whether it is adopted. The raw reports are too large to commit, so the manifest
pins their hashes and the capture command recreates them from the recorded commit
and SDK. A candidate gates nothing until the issue records who adopted it.

Adoption checklist for the reviewer:

1. Recapture twice from the candidate commit; the manifests must be byte-identical.
2. Review `summary.json` rankings and the entry-point and duplicate candidates.
3. Record the decision and the reviewer's name on the issue, then capture again
   with that name as `--reviewer` and add the new directory. Keep the old one.

Release use (informational, no verdict): after the release commit exists, capture
it and run `review` against the adopted baseline, or the newest candidate if none
is adopted. Read `reviewRequired` by rule and symbol. A review request means a
person reads the symbol; it is not a release blocker by itself and never a score.
Exit 1 from `review` is expected when a change worsens a measurement.

Verified plant (2026-10-08, base `4421d66a`): nesting five `if` levels and adding a
defaulted parameter to `DocumentPermissionGuard.Require` made `review` exit 1 with
nine increases (Cognitive, Cyclomatic, ExecutableLines, MaxNesting, ParameterCount
on the method and the owning type); the source was then restored.
