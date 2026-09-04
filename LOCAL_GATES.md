# Local gates for excise

**excise has no CI.** GitHub Actions was removed on 2026-09-04 (see the
"GitHub Actions removal" section below). Every gate runs on this machine, and
the only supported release artifact is a locally-built, unsigned macOS app.

That is a deliberate trade, not a gap. The Linux runner cost months of triage
for a platform nobody ships to today: coverage floors read off the wrong
environment, a skip allowlist calibrated for a corpus-less runner, and a
rendering-tools job that stayed red while macOS was green. The gates below are
the ones that actually protect the product; they run where the corpora and the
reference tools are.

## The tiers are the gate map

`scripts/test-tier.sh {t0|t1|t2|t3}` is the single answer to "what do I run
before X?". Do not hand-copy its step list here — that is how this document
drifted before (it once listed 20 steps and was missing three real gates).
Read the script, or run it.

| Tier | Cost | When |
|------|------|------|
| `t0` | ~30s | Before every commit. `scripts/test-tier.sh --install-hook` installs it as `.git/hooks/pre-push`. |
| `t1` | ~10m | Before merging anything to `develop`. This is what used to block a PR. |
| `t2` | ~30m | Release candidate: `scripts/release-smoke.sh --release-tests`. |
| `t3` | — | Was "t2 on macOS + Linux + Windows". **Currently macOS only.** |

Tier is selected by blast radius — who gets hurt if this is wrong — not by
convenience.

**The redaction gates are unskippable at every tier that produces a binary
anyone could redact with, including a purely local build.** `t0` runs the
static redaction-architecture guard; `t1`'s redaction suites run
unconditionally and there is no flag to skip them. You are your own third
party: a local build you redact a real document with is a binary whose failure
hurts someone, and the failure is silent.

## Long runs

```bash
caffeinate -i scripts/run-full-suite.sh --resume 2>&1 | tee -a logs/full-suite.log
scripts/run-full-suite.sh --status    # what's done / what's left
```

Checkpoints fail toward re-running, never toward skipping; the redaction gates
are never checkpointed. See CLAUDE.md, "Restartable full runs".

## Coverage

`tests/coverage-floors.tsv` now carries only the `full` profile — corpora and
reference tools present, unfiltered. The `ci` rows were deleted with the
runner they described; they measured a corpus-less environment that no longer
exists anywhere in this project.

## GitHub Actions removal

Removed at `a708774d` (the commit before the teardown). To recover a workflow:

```bash
git show a708774d:.github/workflows/ci.yml
```

Restoring GitHub Actions for **Linux and Windows packaging only** — no gates,
no cross-platform test matrix — is tracked as a separate issue. The gates stay
local.
