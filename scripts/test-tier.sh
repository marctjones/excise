#!/usr/bin/env bash
# Test tiers (#646): a single, defined answer to "what do I run before X?"
#
# The choice used to be "nothing" or "the full ~28-minute release smoke."
# Both are wrong most of the time. Tier is selected by BLAST RADIUS — who
# gets hurt if this is wrong — not by convenience:
#
#   t0  ~30s   "did I break it"      pre-push, no excuse not to run it
#   t1  ~10m   correctness gate      what CI blocks a PR on; nothing merges red
#   t2  ~30m   release candidate     today's release-smoke.sh
#   t3         third-party distribution  t2 on all three platforms + package
#
# pdfe-specific rule: YOU ARE YOUR OWN THIRD PARTY. A local build you redact
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

TIER="${1:-}"
INSTALL_HOOK=0
for arg in "$@"; do
    [ "$arg" = "--install-hook" ] && INSTALL_HOOK=1
done

usage() {
    cat <<'EOF'
Usage: scripts/test-tier.sh {t0|t1|t2|t3} [--install-hook]

  t0  ~30s   build + Core/Cli/Avalonia tests + doc-claims + gate-asymmetry
             + redaction-architecture guard. Pre-push, no excuse not to run it.
  t1  ~10m   t0 + full redaction test suites + Rendering (deterministic) +
             skip-budget. What CI blocks a PR on.
  t2  ~30m   release candidate — runs scripts/release-smoke.sh --release-tests.
  t3         t2, then prints the CI checks that must also be green on
             macOS/Windows before tagging (this script runs on one machine;
             it cannot itself execute another platform's job).

  --install-hook   install t0 as .git/hooks/pre-push and exit.
EOF
}

if [ "$INSTALL_HOOK" = "1" ]; then
    HOOK="$ROOT/.git/hooks/pre-push"
    cat > "$HOOK" <<'HOOKEOF'
#!/usr/bin/env bash
# Installed by scripts/test-tier.sh --install-hook (#646).
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

run_step() {
    local name="$1"
    shift
    local log="$LOG_DIR/$name.log"

    say "${B}[$name]${N} $*"
    local start
    start="$(date +%s)"
    "$@" > "$log" 2>&1
    local rc=$?
    local dur=$(( $(date +%s) - start ))

    if [ "$rc" = "0" ]; then
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
    run_step "build" dotnet build pdfe.sln -c Debug
    run_step "core-tests" dotnet test Pdfe.Core.Tests --no-build -c Debug --logger "console;verbosity=normal"
    run_step "cli-tests" dotnet test Pdfe.Cli.Tests --no-build -c Debug --logger "console;verbosity=normal"
    run_step "avalonia-tests" dotnet test Pdfe.Avalonia.Tests --no-build -c Debug --logger "console;verbosity=normal"
    run_step "doc-claims" scripts/verify-doc-claims.sh
    # origin/develop, not origin/main: this repo's git-flow lands feature
    # work on develop (release.yml/PR merges to main happen separately), so
    # that's the correct local diff base — matches ci.yml's own
    # github.base_ref-driven choice in a real PR targeting develop.
    run_step "gate-asymmetry" scripts/check-gate-asymmetry.sh "origin/develop"
    run_step "redaction-architecture" scripts/verify-true-redaction.sh
}

run_t1() {
    run_t0
    run_step "redaction-suites" dotnet test --no-build -c Debug --filter "FullyQualifiedName~Redaction" --logger "console;verbosity=normal"
    run_step "rendering-deterministic" dotnet test Pdfe.Rendering.Tests --no-build -c Debug \
        --filter "FullyQualifiedName!~Corpus&FullyQualifiedName!~Differential&FullyQualifiedName!~Benchmark&FullyQualifiedName!~Visual" \
        --logger "console;verbosity=normal"
    run_step "skip-budget-core" scripts/check-skip-budget.sh Pdfe.Core.Tests/Pdfe.Core.Tests.csproj
}

case "$TIER" in
    t0)
        run_t0
        ;;
    t1)
        run_t1
        ;;
    t2)
        say "${B}[t2]${N} delegating to scripts/release-smoke.sh --release-tests"
        exec scripts/release-smoke.sh --release-tests
        ;;
    t3)
        say "${B}[t3]${N} running t2 locally (this machine's platform only)"
        if ! scripts/release-smoke.sh --release-tests; then
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
