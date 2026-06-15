#!/usr/bin/env bash
# Run repeatable release-candidate gates before tagging.
#
# This script is intentionally non-destructive: it does not create commits,
# tags, GitHub Releases, or upload artifacts. It records logs under logs/ and
# exits non-zero when a required gate fails. See issue #471.

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$ROOT" || exit 1

CONFIG="Debug"
RUN_FULL_TESTS=1
RUN_VISUAL=0
RUN_PACKAGE=0
NO_BUILD=0
VERSION=""
ONLY=""

usage() {
    cat <<'EOF'
Run repeatable release-candidate gates before tagging.

This script is intentionally non-destructive: it does not create commits, tags,
GitHub Releases, or upload artifacts. It records logs under logs/ and exits
non-zero when a required gate fails.

Usage:
  scripts/release-smoke.sh [options]

Options:
  --version <v>       Version to pass to local package builders.
  --release-tests     Run build/test gates in Release instead of Debug.
  --quick             Skip the full solution test pass.
  --visual            Run the local visual-regression runner.
  --package           Build local package artifacts for the current platform.
  --no-build          Skip the initial build gate.
  --only=a,b          Run only named gates: docs,build,redaction,signature,ui,tests,visual,package,diffcheck.
  -h, --help          Show this help.
EOF
}

while [ "$#" -gt 0 ]; do
    case "$1" in
        --version)
            VERSION="${2:-}"
            if [ -z "$VERSION" ]; then
                echo "--version requires a value" >&2
                exit 2
            fi
            shift 2
            ;;
        --version=*) VERSION="${1#*=}"; shift ;;
        --release-tests) CONFIG="Release"; shift ;;
        --quick) RUN_FULL_TESTS=0; shift ;;
        --visual) RUN_VISUAL=1; shift ;;
        --package) RUN_PACKAGE=1; shift ;;
        --no-build) NO_BUILD=1; shift ;;
        --only=*) ONLY="${1#*=}"; shift ;;
        --only)
            ONLY="${2:-}"
            if [ -z "$ONLY" ]; then
                echo "--only requires a value" >&2
                exit 2
            fi
            shift 2
            ;;
        -h|--help) usage; exit 0 ;;
        *)
            echo "Unknown argument: $1" >&2
            usage >&2
            exit 2
            ;;
    esac
done

TS="$(date +%Y%m%d_%H%M%S)"
LOG_DIR="$ROOT/logs/release-smoke_$TS"
mkdir -p "$LOG_DIR"
ln -sf "release-smoke_$TS" "$ROOT/logs/release-smoke_latest" 2>/dev/null

if [ -t 1 ]; then
    R='\033[0;31m'; G='\033[0;32m'; Y='\033[1;33m'; B='\033[0;36m'; N='\033[0m'
else
    R=''; G=''; Y=''; B=''; N=''
fi

say() { echo -e "$1"; }

should_run() {
    local name="$1"
    [ -z "$ONLY" ] && return 0
    echo ",$ONLY," | grep -qi ",$name,"
}

declare -a RESULTS
overall=0

run_gate() {
    local name="$1"
    shift
    local log="$LOG_DIR/$name.log"

    if ! should_run "$name"; then
        return
    fi

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
        overall=1
    fi
    say ""
}

run_package_gate() {
    if ! should_run "package"; then
        return
    fi

    if [ "$RUN_PACKAGE" != "1" ]; then
        say "${Y}[package] SKIP${N} - pass --package to build local artifacts."
        RESULTS+=("package|SKIP|pass --package")
        say ""
        return
    fi

    local version="${VERSION:-$(git describe --tags --abbrev=0 2>/dev/null | sed 's/^v//' || echo 0.0.0-local)}"
    case "$(uname -s)" in
        Darwin)
            run_gate "package" scripts/build-macos-app.sh --version "$version" --output dist
            ;;
        Linux)
            run_gate "package" scripts/build-deb.sh --version "$version" --output dist
            ;;
        *)
            say "${Y}[package] SKIP${N} - local package smoke is supported on macOS/Linux here; use release.yml for Windows."
            RESULTS+=("package|SKIP|unsupported local OS")
            say ""
            ;;
    esac
}

say "${B}=================================================${N}"
say "${B} pdfe release smoke${N}"
say "${B}=================================================${N}"
say "Started : $(date)"
say "Test config : $CONFIG"
say "Logs    : $LOG_DIR"
[ -n "$VERSION" ] && say "Version : $VERSION"
[ -n "$ONLY" ] && say "Only    : $ONLY"
say ""

run_gate "docs" scripts/verify-doc-claims.sh

if [ "$NO_BUILD" != "1" ]; then
    run_gate "build" dotnet build pdfe.sln -c "$CONFIG"
fi

run_gate "redaction" dotnet test --no-build -c "$CONFIG" --filter "FullyQualifiedName~Redaction" --logger "console;verbosity=normal"
run_gate "signature" dotnet test PdfEditor.Tests --no-build -c "$CONFIG" --filter "FullyQualifiedName~SignatureVerification" --logger "console;verbosity=normal"
run_gate "ui" dotnet test PdfEditor.Tests --no-build -c "$CONFIG" --filter "FullyQualifiedName~GuiWorkflowCoverageMatrix|FullyQualifiedName~GoldenPath|FullyQualifiedName~Workflow" --logger "console;verbosity=normal"

if should_run "tests"; then
    if [ "$RUN_FULL_TESTS" = "1" ]; then
        run_gate "tests" dotnet test pdfe.sln --no-build -c "$CONFIG" --logger "console;verbosity=minimal"
    else
        say "${Y}[tests] SKIP${N} - --quick selected."
        RESULTS+=("tests|SKIP|--quick")
        say ""
    fi
fi

if should_run "visual"; then
    if [ "$RUN_VISUAL" = "1" ]; then
        visual_args=("--no-build")
        [ "$CONFIG" = "Release" ] && visual_args=("--release" "--no-build")
        run_gate "visual" scripts/run-visual-regression-local.sh "${visual_args[@]}"
    else
        say "${Y}[visual] SKIP${N} - pass --visual for local visual/differential gates."
        RESULTS+=("visual|SKIP|pass --visual")
        say ""
    fi
fi

run_package_gate
run_gate "diffcheck" git diff --check

say "${B}Summary${N}"
for r in "${RESULTS[@]}"; do
    IFS='|' read -r name status detail <<< "$r"
    case "$status" in
        PASS) say "  ${G}PASS${N} $name $detail" ;;
        FAIL) say "  ${R}FAIL${N} $name $detail" ;;
        SKIP) say "  ${Y}SKIP${N} $name $detail" ;;
    esac
done

exit "$overall"
