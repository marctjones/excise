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
RUN_PACKAGED_GUI=0
PACKAGED_GUI_FOCUS_INPUT=0
PACKAGED_GUI_MODE="direct-exec"
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
  --packaged-gui      Run packaged-app GUI smoke evidence after package build.
  --packaged-gui-direct-exec
                      Run packaged GUI smoke through the app executable so app-internal timing JSON is reliable.
  --packaged-gui-background-open
                      Run packaged GUI smoke through Launch Services/open for file-activation investigation.
  --packaged-gui-focus-input
                      Also run focus-taking native key/mouse smoke.
  --no-build          Skip the initial build gate.
  --only=a,b          Run only named gates: docs,build,redaction,signature,ui,tests,pdf20,visual,package,packaged-gui,diffcheck.
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
        --packaged-gui) RUN_PACKAGED_GUI=1; shift ;;
        --packaged-gui-direct-exec)
            RUN_PACKAGED_GUI=1
            PACKAGED_GUI_MODE="direct-exec"
            shift
            ;;
        --packaged-gui-background-open)
            RUN_PACKAGED_GUI=1
            PACKAGED_GUI_MODE="background-open"
            shift
            ;;
        --packaged-gui-focus-input)
            RUN_PACKAGED_GUI=1
            PACKAGED_GUI_FOCUS_INPUT=1
            shift
            ;;
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

run_packaged_gui_gate() {
    if ! should_run "packaged-gui"; then
        return
    fi

    if [ "$RUN_PACKAGED_GUI" != "1" ]; then
        say "${Y}[packaged-gui] SKIP${N} - pass --packaged-gui to run packaged app GUI evidence."
        RESULTS+=("packaged-gui|SKIP|pass --packaged-gui")
        say ""
        return
    fi

    local app="$ROOT/dist/pdfe.app"
    local pdf="$ROOT/test-pdfs/smoke/irs-w9.pdf"
    local out="$LOG_DIR/packaged-gui"
    local -a args=("--app" "$app" "--pdf" "$pdf" "--output" "$out" "--mode" "$PACKAGED_GUI_MODE")
    if [ "$PACKAGED_GUI_FOCUS_INPUT" = "1" ]; then
        args+=("--allow-focus-input")
    fi
    run_gate "packaged-gui" scripts/run-packaged-gui-smoke.sh "${args[@]}"
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

run_dotnet_test_step() {
    local log="$1"
    local label="$2"
    local project="$3"
    local filter="${4:-}"
    local hang_timeout="${5:-}"
    local -a args=("test" "$project" "--no-build" "-c" "$CONFIG" "--logger" "console;verbosity=minimal")

    if [ -n "$filter" ]; then
        args+=("--filter" "$filter")
    fi
    if [ -n "$hang_timeout" ]; then
        args+=("--blame-hang-timeout" "$hang_timeout")
    fi

    say "  -> $label"
    {
        echo "================================================="
        echo "$label"
        echo "================================================="
    } >> "$log"

    local rc=0
    dotnet "${args[@]}" >> "$log" 2>&1 || rc=$?
    if [ "$rc" = "0" ]; then
        say "     PASS"
        return 0
    fi

    say "     ${R}FAIL${N} rc=$rc -> $log"
    tail -80 "$log" | sed 's/^/    /'
    return "$rc"
}

run_pdfeditor_gui_display_step() {
    local log="$1"
    local label="PdfEditor.Tests GUI display sweep"
    local project="PdfEditor.Tests/PdfEditor.Tests.csproj"
    local filter="FullyQualifiedName~PdfViewerHeadlessRenderTests.PdfViewer_RenderingQualitySuite_DisplayBitmapsMatchRenderer"
    local report="PdfEditor.Tests/bin/$CONFIG/net10.0/UI/test-output/gui-display-suite-renderer-contracts-representative-pages.json"
    local last_progress=""

    say "  -> $label"
    {
        echo "================================================="
        echo "$label"
        echo "================================================="
    } >> "$log"
    rm -f "$report" 2>/dev/null || true

    dotnet test "$project" --no-build -c "$CONFIG" --filter "$filter" --logger "console;verbosity=minimal" >> "$log" 2>&1 &
    local pid=$!
    while kill -0 "$pid" 2>/dev/null; do
        sleep 30
        if [ -f "$report" ] && command -v jq >/dev/null 2>&1; then
            local progress
            progress="$(jq -r '
                def failures: ([.results[]? | select(.status == "FAIL")] | length);
                def nonpass: ([.results[]? | select(.status != "PASS" and .status != "NON_RENDERABLE_ACCEPTED")] | length);
                if .current then
                    "\(.current.ordinal)/\(.current.total) \(.current.path) page \(.current.page), failures \(failures), non-pass \(nonpass)"
                else
                    "\(.results | length) result(s), failures \(failures), non-pass \(nonpass)"
                end
            ' "$report" 2>/dev/null || true)"
            if [ -n "$progress" ] && [ "$progress" != "$last_progress" ]; then
                say "     progress: $progress"
                last_progress="$progress"
            fi
        fi
    done

    local rc=0
    wait "$pid" || rc=$?
    if [ "$rc" = "0" ]; then
        say "     PASS"
        return 0
    fi

    say "     ${R}FAIL${N} rc=$rc -> $log"
    tail -80 "$log" | sed 's/^/    /'
    return "$rc"
}

run_full_tests_gate() {
    if ! should_run "tests"; then
        return
    fi

    if [ "$RUN_FULL_TESTS" != "1" ]; then
        say "${Y}[tests] SKIP${N} - --quick selected."
        RESULTS+=("tests|SKIP|--quick")
        say ""
        return
    fi

    local log="$LOG_DIR/tests.log"
    local start
    start="$(date +%s)"
    local rc=0
    local project
    local -a test_projects=(
        "Pdfe.Avalonia.Tests/Pdfe.Avalonia.Tests.csproj"
        "Pdfe.Cli.Tests/Pdfe.Cli.Tests.csproj"
        "Pdfe.Core.Tests/Pdfe.Core.Tests.csproj"
        "Pdfe.Ocr.Tests/Pdfe.Ocr.Tests.csproj"
        "Pdfe.Rendering.Tests/Pdfe.Rendering.Tests.csproj"
    )

    say "${B}[tests]${N} sequential project tests with hang diagnostics"
    : > "$log"
    for project in "${test_projects[@]}"; do
        run_dotnet_test_step "$log" "$project" "$project" "" "5m" || { rc=$?; break; }
    done

    if [ "$rc" = "0" ]; then
        run_dotnet_test_step "$log" "PdfEditor.Tests ordinary slice" \
            "PdfEditor.Tests/PdfEditor.Tests.csproj" \
            "FullyQualifiedName!~KeyboardShortcutTests.CtrlW_ClosesDocument&FullyQualifiedName!~PdfViewerHeadlessRenderTests.PdfViewer_RenderingQualitySuite_DisplayBitmapsMatchRenderer" \
            "5m" || rc=$?
    fi
    if [ "$rc" = "0" ]; then
        run_dotnet_test_step "$log" "PdfEditor.Tests Ctrl+W shortcut" \
            "PdfEditor.Tests/PdfEditor.Tests.csproj" \
            "FullyQualifiedName~KeyboardShortcutTests.CtrlW_ClosesDocument" \
            "2m" || rc=$?
    fi
    if [ "$rc" = "0" ]; then
        run_pdfeditor_gui_display_step "$log" || rc=$?
    fi

    local dur=$(( $(date +%s) - start ))
    if [ "$rc" = "0" ]; then
        say "  ${G}PASS${N} (${dur}s) -> $log"
        RESULTS+=("tests|PASS|${dur}s")
    else
        RESULTS+=("tests|FAIL|rc=$rc ${dur}s")
        overall=1
    fi
    say ""
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
run_gate "pdf20" scripts/run-pdf20-renderer-conformance.sh --run-tests

run_full_tests_gate

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
run_packaged_gui_gate
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
