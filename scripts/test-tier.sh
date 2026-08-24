#!/usr/bin/env bash
# Test tiers (#646): a single, defined answer to "what do I run before X?"
#
# The choice used to be "nothing" or "the full ~28-minute release smoke."
# Both are wrong most of the time. Tier is selected by BLAST RADIUS — who
# gets hurt if this is wrong — not by convenience:
#
#   t0  ~30s   "did I break it"      pre-push, no excuse not to run it
#   t1  ~10m*  correctness gate      what CI blocks a PR on; nothing merges red
#   t2  ~30m   release candidate     today's release-smoke.sh
#   t3         third-party distribution  t2 on all three platforms + package
#
#   * t1's skip-budget checks (#655) run Excise.Rendering.Tests and
#     Excise.App.Tests standalone, with no corpus/tool-run to reuse locally
#     the way ci.yml's equivalent steps do. On a bare machine (no test-pdfs
#     corpus, no mutool/ghostscript/pdftocairo/tesseract) every skip site
#     gates fast and ~10m holds. On a machine with the corpus downloaded and
#     the reference tools installed, Rendering does real corpus/mutool work
#     and Excise.App.Tests' serial ~17-minute suite runs in full — t1 is
#     meaningfully longer there. See the comment on those two run_step calls.
#
# excise-specific rule: YOU ARE YOUR OWN THIRD PARTY. A local build you redact
# a real document with is a binary whose failure hurts someone, silently — no
# crash, no error, the name is just still in the file. The redaction gate is
# therefore non-negotiable at every tier that produces a binary anyone will
# redact with, including a purely local build. t0 includes the static
# redaction-architecture guard (verify-true-redaction.sh, near-free); t1
# includes the full redaction test suites (the ~361s the issue costs out) and
# does not accept a flag to skip them.
#
# Usage: scripts/test-tier.sh {t0|t1|t2|t3} [--install-hook]
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT" || exit 1

if [ -t 1 ]; then
    R='\033[0;31m'; G='\033[0;32m'; Y='\033[1;33m'; B='\033[0;36m'; N='\033[0m'
else
    R=''; G=''; Y=''; B=''; N=''
fi

say() { echo -e "$1"; }

source "$ROOT/scripts/lib-runner.sh"

TIER="${1:-}"
INSTALL_HOOK=0
# Opt-in crash-resumable mode. Default 0 keeps this script's behaviour
# unchanged for the pre-push hook and CI, which should never skip anything.
RESUME=0
for arg in "$@"; do
    [ "$arg" = "--install-hook" ] && INSTALL_HOOK=1
    [ "$arg" = "--resume" ] && RESUME=1
done

usage() {
    cat <<'EOF'
Usage: scripts/test-tier.sh {t0|t1|t2|t3} [--install-hook]

  t0  ~30s   build + Core/Cli/Avalonia tests + doc-claim-freshness + gate-asymmetry
             + redaction-architecture guard. Pre-push, no excuse not to run it.
  t1  ~10m   t0 + full redaction test suites + Rendering (deterministic) +
             skip-budget for Core/Rendering/Excise.App (#655). What CI blocks
             a PR on; can run longer on a machine with the full test-pdfs
             corpus and mutool/ghostscript/pdftocairo/tesseract installed.
  t2  ~30m   release candidate — runs scripts/release-smoke.sh --release-tests.
  t3         t2, then prints the CI checks that must also be green on
             macOS/Windows before tagging (this script runs on one machine;
             it cannot itself execute another platform's job).

  --install-hook   install t0 as .git/hooks/pre-push and exit.
  --resume         skip steps that already passed for this exact commit, so an
                   interrupted long run (crash, Ctrl-C, panic) picks up where it
                   stopped instead of restarting. Redaction steps always re-run.
                   For the whole suite chunked and memory-bounded, prefer
                   scripts/run-full-suite.sh.
EOF
}

if [ "$INSTALL_HOOK" = "1" ]; then
    HOOK="$ROOT/.git/hooks/pre-push"
    cat > "$HOOK" <<'HOOKEOF'
#!/usr/bin/env bash
# Installed by scripts/test-tier.sh --install-hook (#646).
#
# ONE job: run t0 before every push.
#
# It earns that. Today alone it blocked two pushes carrying unreviewed public
# API changes and one carrying a broken Excise.Avalonia test — each a real
# defect, caught before it left the machine.
#
# WHAT THIS HOOK USED TO ALSO DO, AND WHY IT NO LONGER DOES
#
# It refused any `v*` tag that was lightweight or lacked a Release-Evidence
# trailer, to force release tags through scripts/tag-release.sh. Removed
# because it guarded a path nothing had ever taken:
#
#   * v3.6.0, v3.7.0 and v3.8.0 all have ZERO Release-Evidence trailers —
#     every existing release tag was made the way the clause forbade.
#   * The clause never fired. No v* push has been attempted since it was
#     installed.
#   * It redirected to scripts/tag-release.sh, whose happy path has never
#     run (#968, closed as won't-do). So the only sanctioned route was an
#     unrehearsed script, and the guard's whole cost landed on someone
#     trying to tag a release.
#
# scripts/tag-release.sh has since been deleted outright, along with the
# Release-Evidence trailers it wrote. Tag by hand: `git tag -a vX.Y.Z`.
exec "$(git rev-parse --show-toplevel)/scripts/test-tier.sh" t0
HOOKEOF
    chmod +x "$HOOK"
    say "${G}Installed${N} $HOOK"
    [ -z "$TIER" ] && exit 0
fi

case "$TIER" in
    t0|t1|t2|t3) ;;
    *) usage; exit 2 ;;
esac

TS="$(date +%Y%m%d_%H%M%S)"
LOG_DIR="$ROOT/logs/test-tier_${TIER}_$TS"
mkdir -p "$LOG_DIR"

OVERALL=0
RESULTS=()

if [ "$RESUME" = "1" ]; then
    runner_state_init "test-tier-$TIER" "Debug"
    runner_export_lean_env
fi

run_step() {
    local name="$1"
    shift
    local log="$LOG_DIR/$name.log"

    # --resume: skip steps that already passed for this exact commit. The
    # redaction steps are never checkpointed (lib-runner.sh), so they re-run
    # even on a resume — t1 accepts no flag that skips them.
    if [ "$RESUME" = "1" ] && ! runner_step_should_run "$name"; then
        say "${B}[$name]${N} ${G}SKIP${N} - already passed for $(git rev-parse --short HEAD)"
        RESULTS+=("$name|SKIP|checkpointed")
        say ""
        return
    fi
    [ "$RESUME" = "1" ] && runner_mem_guard "$name"

    say "${B}[$name]${N} $*"
    local start
    start="$(date +%s)"
    runner_guard_no_build_command "$@" > "$log.freshness" 2>&1
    local freshness_rc=$?
    if [ "$freshness_rc" != "0" ]; then
        local dur=$(( $(date +%s) - start ))
        say "  ${R}FAIL${N} stale --no-build guard rc=$freshness_rc (${dur}s) -> $log.freshness"
        cat "$log.freshness" | sed 's/^/    /'
        RESULTS+=("$name|FAIL|stale --no-build rc=$freshness_rc ${dur}s")
        OVERALL=1
        say ""
        return
    fi
    "$@" > "$log" 2>&1
    local rc=$?
    local dur=$(( $(date +%s) - start ))

    if [ "$rc" = "0" ]; then
        [ "$RESUME" = "1" ] && runner_step_mark "$name" "$rc" "$dur"
        say "  ${G}PASS${N} (${dur}s) -> $log"
        RESULTS+=("$name|PASS|${dur}s")
    else
        say "  ${R}FAIL${N} rc=$rc (${dur}s) -> $log"
        tail -40 "$log" | sed 's/^/    /'
        RESULTS+=("$name|FAIL|rc=$rc ${dur}s")
        OVERALL=1
    fi
    say ""
}

run_t0() {
    run_step "build" dotnet build excise.sln -c Debug
    # The trx loggers are here so the #894 count gates below can reuse THIS run
    # instead of executing each suite a second time. Paths are absolute because
    # `dotnet test` resolves a relative LogFileName against the project's
    # TestResults directory, not the working directory.
    run_step "core-tests" dotnet test Excise.Core.Tests --no-build -c Debug \
        --logger "console;verbosity=normal" --logger "trx;LogFileName=$LOG_DIR/core-tests.trx"
    run_step "cli-tests" dotnet test Excise.Cli.Tests --no-build -c Debug \
        --logger "console;verbosity=normal" --logger "trx;LogFileName=$LOG_DIR/cli-tests.trx"
    run_step "avalonia-tests" dotnet test Excise.Avalonia.Tests --no-build -c Debug \
        --logger "console;verbosity=normal" --logger "trx;LogFileName=$LOG_DIR/avalonia-tests.trx"
    # #894: every discovered test must produce a result. `dotnet test` loses one
    # roughly half the time on Excise.Core.Tests — a different test each run, and
    # not xunit parallelism (forcing serial changes the wall clock by 4s and
    # still loses one). A vanished test reads exactly like a passing one, and it
    # defeats mutation testing: reverting a fix cannot redden a case that never
    # reports. The gate re-runs whatever went missing, so a transient loss is
    # reported and a genuine coverage hole is fatal.
    run_step "test-count-core" scripts/check-test-count.sh \
        Excise.Core.Tests/Excise.Core.Tests.csproj --trx "$LOG_DIR/core-tests.trx"
    run_step "test-count-cli" scripts/check-test-count.sh \
        Excise.Cli.Tests/Excise.Cli.Tests.csproj --trx "$LOG_DIR/cli-tests.trx"
    run_step "test-count-avalonia" scripts/check-test-count.sh \
        Excise.Avalonia.Tests/Excise.Avalonia.Tests.csproj --trx "$LOG_DIR/avalonia-tests.trx"
    # #936: this derives whether a NUMBER is still TRUE — reference-oracle
    # usage counts and milestone references are re-measured against the live
    # source, and numbers that can't be cheaply re-measured must carry a dated
    # marker. (verify-doc-claims.sh, which pinned that a STRING exists, was
    # deleted 2026-08-16.)
    # Self-test first so a broken checker can't report a false "passed".
    run_step "doc-claim-freshness-selftest" scripts/check-doc-claim-freshness.sh --self-test
    # A source file that .gitignore swallows is work that never reaches the
    # remote — silently, and in the direction of losing it. Pure git metadata,
    # no build, milliseconds.
    run_step "no-ignored-sources" scripts/check-no-ignored-sources.sh
    # Licence compliance (#1068). A STATIC file check, so it lives here rather
    # than in Excise.App.Tests — where it once wedged the host and left 1,310
    # correctness tests with no verdict. A compliance check must never be able
    # to stop correctness tests from reporting.
    run_step "license-compliance" scripts/check-license-compliance.sh
    # No redaction leak test may rest solely on excise reading its own output
    # (#1029). Pure grep over ~48 files; milliseconds.
    run_step "redaction-oracles" scripts/check-redaction-oracles.sh
    run_step "doc-claim-freshness" scripts/check-doc-claim-freshness.sh
    # origin/develop, not origin/main: this repo's git-flow lands feature
    # work on develop (release.yml/PR merges to main happen separately), so
    # that's the correct local diff base — matches ci.yml's own
    # github.base_ref-driven choice in a real PR targeting develop.
    run_step "gate-asymmetry" scripts/check-gate-asymmetry.sh "origin/develop"
    run_step "redaction-architecture" scripts/verify-true-redaction.sh
    # #678: project-authored test data (manifests) references source paths that
    # aren't compile-checked; catch drift (e.g. a rename) before it rots.
    run_step "testdata-sync" scripts/check-testdata-sync.sh
    # #663/#665/#668: the skip-budget --update self-test (justification,
    # inner-'#' reasons, and hand-written comment-block preservation).
    run_step "assert-fresh-selftest" scripts/test-assert-fresh.sh
    run_step "skip-budget-selftest" scripts/test-check-skip-budget.sh
    run_step "coverage-floor-selftest" scripts/test-check-coverage-floor.sh
    # Near-free, and the thing it guards is easy to get wrong silently: every
    # corpus destination must be gitignored. It caught one on its first run.
    run_step "corpus-registry" scripts/corpus.sh verify
    run_step "corpus-selftest" scripts/test-corpus.sh
    run_step "font-width-sync" scripts/check-font-width-sync.sh
    # #940: prove the Roslyn reachability pass reports a dead root and its
    # dead leaf in one closure, without paying the whole-solution cost in t0.
    run_step "reachability-selftest" scripts/check-reachability.sh --self-test
    # #1012: every gate must be falsifiable — breaking the property it guards
    # must make it fail. Each of these feeds its gate known-bad input and
    # requires a non-zero exit; each was watched going red against a gate whose
    # failure branch had been neutralized. They are hermetic (temp roots, fake
    # tools, synthetic reports) and run in milliseconds, except the
    # contract-manifest one which pays ~6s for four `dotnet run` invocations.
    #
    # An UNWIRED selftest is the very defect RC2 is about, so these live here
    # and not in a document that recommends running them.
    # #908: public API that nothing calls. The skip budget and test-count gates
    # catch a TEST that stopped running; this catches PRODUCTION CODE that never
    # started. ~10s (a text index over .cs/.axaml), ratcheted against
    # tests/unwired-api-baseline.tsv so only NEW entries fail.
    run_step "unwired-api" scripts/check-unwired-api.sh --quiet
    # #957: tests/format-compatibility-suite.json is a schema'd design tracker
    # (PDF versions, storage formats, feature workflows, oracles, known gaps).
    # Nothing referenced it before this gate, so a row could be hand-edited to
    # "implemented" without any real test existing. Static/textual (no tools,
    # seconds): every implemented/partial row's "evidence" entries must exist
    # and contain a runnable [Fact]/[Theory]-family test. Already runs as part
    # of "core-tests" above; this step gives it its own named, always-run
    # signal in t0 output, matching how the other self-contained gates here
    # are surfaced individually.
    run_step "format-compat-drift-gate" dotnet test Excise.Core.Tests --no-build -c Debug \
        --filter "FullyQualifiedName~FormatCompatibilitySuiteEvidenceGateTests" --logger "console;verbosity=normal"
}

run_t1() {
    run_t0
    # #341: the shipped Release package must not drag heavy optional
    # subsystems into startup — no Roslyn scripting assemblies, no bundled
    # tessdata, and toggling hidden text must not load Excise.Ocr.
    #
    # Wired here 2026-08-17 after being found running NOWHERE. It had already
    # been fixed once for the same reason (d5687a1b, "verify-lazy-startup.sh
    # was wired into nothing") and had come unwired again. t1 rather than t0
    # because it publishes a Release build; 16s, so it is cheap enough to run
    # on anything CI blocks a PR on.
    #
    # It is one of the few gates here that checks something a USER would
    # notice, and it was the only one that could not fire.
    run_step "lazy-startup" scripts/verify-lazy-startup.sh
    # The PDF 2.0 renderer conformance gate (ISO 32000-2:2020 + EC3): 95
    # requirements traced to the spec, audited against actual evidence.
    #
    # t1 rather than t2-only, added 2026-08-18. It ran ONLY in release-smoke, so
    # a PR could not fail it — and it takes 35 SECONDS. The 7 schema tests
    # inside Core.Tests already run at t0; what was missing here is the audit
    # that decides whether a requirement's claimed evidence actually exists.
    #
    # It earns the slot: asked to graduate the annotation rows to hard-gate
    # status, it refused two of them (MISSING_CORPUS) because they claimed
    # corpus evidence that was not registered. A gate that rejects an
    # unsupported claim from the person editing it is the kind worth running
    # early.
    run_step "pdf20-conformance" scripts/run-pdf20-renderer-conformance.sh
    run_step "redaction-suites" dotnet test --no-build -c Debug --filter "FullyQualifiedName~Redaction" --logger "console;verbosity=normal"
    # #644: the encryption interop gate. Neither of t1's other rendering
    # steps reaches it — the redaction filter doesn't match it and
    # rendering-deterministic excludes Differential — so it gets its own
    # step. It runs in seconds: tiny generated fixtures, and on a machine
    # without mutool/qpdf/ghostscript/pdftoppm every test skips loudly
    # (release evidence instead sets EXCISE_REQUIRE_ENCRYPTION_INTEROP_TOOLS=1
    # so a tool-less run cannot green the gate vacuously — see
    # docs/RELEASE_CHECKLIST.md "Encryption Evidence").
    run_step "encryption-interop-gate" dotnet test Excise.Rendering.Tests --no-build -c Debug \
        --filter "FullyQualifiedName~EncryptionInteropGateTests" --logger "console;verbosity=normal"
    run_step "rendering-deterministic" dotnet test Excise.Rendering.Tests --no-build -c Debug \
        --filter "FullyQualifiedName!~Corpus&FullyQualifiedName!~Differential&FullyQualifiedName!~Benchmark&FullyQualifiedName!~Visual" \
        --logger "console;verbosity=normal"
    # Copy-whitespace parity ratchet (#837): fails if copied-text word/line
    # agreement vs poppler pdftotext drops below tests/copy-whitespace/floors.json.
    # Skips loudly (exit 0) when pdftotext or the corpus is absent, like the
    # extraction-parity gate.
    run_step "copy-whitespace-parity" scripts/check-copy-whitespace-parity.sh
    run_step "skip-budget-core" scripts/check-skip-budget.sh Excise.Core.Tests/Excise.Core.Tests.csproj
    # #655: Excise.Core.Tests was the only project this gate watched — Excise.
    # Rendering.Tests (~114 Assert.SkipWhen/SkipUnless call sites) and
    # Excise.App.Tests (its own allowlist already existed but was never wired
    # to anything) had zero enumeration. Neither call below passes --trx:
    # test-tier.sh, unlike ci.yml, has no earlier full run of either project
    # to reuse (rendering-deterministic above deliberately excludes Corpus/
    # Differential/Benchmark/Visual, and Excise.App.Tests isn't run in t1 at
    # all), so each runs its own full `dotnet test`. On a machine without
    # the gitignored smoke/isartor/local-real-world corpus or mutool/
    # ghostscript/pdftocairo/tesseract installed, every skip site gates fast
    # and this is cheap. On a machine that DOES have them (this is common
    # for a maintainer box that ran scripts/download-test-pdfs.sh), Rendering
    # genuinely does real corpus/mutool work and Excise.App.Tests' serial
    # ~17-minute suite genuinely runs in full — t1 stops being "~10m" for
    # that machine. Accepted deliberately: the coverage guarantee (#619) is
    # worth more than keeping the estimate accurate everywhere, and #646's
    # original ~10m figure was already a rough one.
    run_step "skip-budget-rendering" scripts/check-skip-budget.sh Excise.Rendering.Tests/Excise.Rendering.Tests.csproj
    # Clear the GUI-coverage artifacts BEFORE the run that produces them. They
    # are append-only (so a killed run still leaves the partial truth), which
    # means a stale file would keep reporting an element as covered by a test
    # that has since been deleted — a green built out of last week's evidence.
    # Same direction as the runner's checkpoint rule: fail toward re-measuring.
    rm -f artifacts/gui-coverage/gui-interaction-observed.tsv \
          artifacts/gui-coverage/gui-interaction-inventory.tsv \
          artifacts/gui-coverage/gui-command-executed.tsv
    run_step "skip-budget-app" scripts/check-skip-budget.sh Excise.App.Tests/Excise.App.Tests.csproj
    # GUI interaction coverage. Reads the artifacts the FULL Excise.App.Tests run
    # immediately above already produced, so it adds no test time — it only
    # judges what that run recorded. It must stay AFTER skip-budget-app for that
    # reason: on its own it has nothing to read and fails loudly rather than
    # skipping.
    run_step "gui-interaction-coverage" scripts/check-gui-interaction-coverage.sh
}

case "$TIER" in
    t0)
        run_t0
        ;;
    t1)
        run_t1
        ;;
    t2)
        # Branch explicitly rather than expanding a possibly-empty array: under
        # bash 3.2 (macOS /bin/bash) "${arr[@]:-}" on an empty array yields one
        # empty-string argument, which release-smoke.sh rejects as unknown.
        say "${B}[t2]${N} delegating to scripts/release-smoke.sh --release-tests"
        if [ "$RESUME" = "1" ]; then
            exec scripts/release-smoke.sh --release-tests --resume
        else
            exec scripts/release-smoke.sh --release-tests
        fi
        ;;
    t3)
        say "${B}[t3]${N} running t2 locally (this machine's platform only)"
        if [ "$RESUME" = "1" ]; then
            RS_OK=0; scripts/release-smoke.sh --release-tests --resume || RS_OK=1
        else
            RS_OK=0; scripts/release-smoke.sh --release-tests || RS_OK=1
        fi
        if [ "$RS_OK" != "0" ]; then
            OVERALL=1
        fi
        say ""
        say "${Y}t3 also requires these to be green before tagging:${N}"
        say "  - CI 'test-linux', 'test-macos', 'test-windows' checks (#647) on this commit"
        say "  - release.yml's linux-deb / windows-exe / macos-app package builds"
        say "test-tier.sh runs on one machine and cannot execute another platform's job."
        exit $OVERALL
        ;;
esac

say "========================================="
say "Summary ($TIER)"
say "========================================="
for r in "${RESULTS[@]}"; do
    IFS='|' read -r name status detail <<< "$r"
    if [ "$status" = "PASS" ]; then
        say "  ${G}PASS${N}  $name ($detail)"
    else
        say "  ${R}FAIL${N}  $name ($detail)"
    fi
done
say ""
say "Logs: $LOG_DIR"

exit $OVERALL
