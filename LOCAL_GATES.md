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
| `t1` | ~20m | Before merging anything to `develop`. This is what used to block a PR, plus the independent-oracle subsets that used to run only on Linux. |
| `t2` | ~30m | Release candidate: `scripts/release-smoke.sh --release-tests`. |
| `t3` | — | Was "t2 on macOS + Linux + Windows". **Currently macOS only.** |

Tier is selected by blast radius — who gets hurt if this is wrong — not by
convenience.

## The independent-oracle subsets

`rendering-linux.yml` was the only place the Differential + Corpus tests ran —
the ones that check excise against mutool, Ghostscript, pdftocairo, pdftoppm,
qpdf, PDFBox and PDFium. Nothing in it was Linux-specific; it needed reference
tools and corpora, which this machine has. It is now part of `t1`:

| step | what |
|---|---|
| `oracle-tools` | every reference tool, the PDFBox jar, the PDFium library and both corpora resolve — or FAIL |
| `rendering-oracles` | `Differential` + `Corpus`, floor 60 passed |
| `app-oracles` | the #929 GUI oracle family, floor 13 |
| `core-oracles` | `_WhenAvailable` writer round-trips, floor 14 |
| `extraction-parity` | coverage vs mutool (#645) |

⚠️ **`rendering-deterministic` deliberately EXCLUDES `Corpus` and
`Differential`.** Before this, t1 ran the exact inverse of the oracle job, so
the tests that exist because excise must not be its own oracle ran in no tier
at all.

**Why the floors.** `dotnet test` exits 0 when every test skipped and 0 when
every test passed. These tests are gated on `Assert.SkipUnless(IsAvailable)`,
so a vanished tool turns the whole subset into skips and the gate goes green
having verified nothing. The floors make that loud. Do not lower one to make a
run pass.

`t1` exports `EXCISE_PDFBOX_JAR` itself when the jar is vendored — PDFBox is
gated on the variable, not on the file, so a jar sitting in `tools/vendor`
bought nothing until this was wired (#935).

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
