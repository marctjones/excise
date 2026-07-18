#!/usr/bin/env bash
# Run the accessibility regression gate without taking keyboard/mouse focus.

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$ROOT" || exit 1

CONFIG="Debug"
OUTPUT_DIR=""

usage() {
    cat <<'EOF'
Run excise accessibility smoke checks.

Usage:
  scripts/run-accessibility-smoke.sh [--config Debug|Release] [--output dir]

The default gate is background-safe: it runs headless command metadata,
accessible-name/help-text, status-announcement, and keyboard reachability tests.
Platform accessibility-tree probes are recorded in JSON as PASS, SKIP, or
MANUAL_REQUIRED. Set EXCISE_ACCESSIBILITY_ALLOW_PLATFORM_PROBE=1 on a dedicated
runner to allow focus/permission-sensitive platform probes.
EOF
}

while [ "$#" -gt 0 ]; do
    case "$1" in
        --config)
            CONFIG="${2:-}"
            [ -n "$CONFIG" ] || { echo "--config requires a value" >&2; exit 2; }
            shift 2
            ;;
        --output)
            OUTPUT_DIR="${2:-}"
            [ -n "$OUTPUT_DIR" ] || { echo "--output requires a value" >&2; exit 2; }
            shift 2
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            echo "Unknown argument: $1" >&2
            usage >&2
            exit 2
            ;;
    esac
done

TS="$(date +%Y%m%d_%H%M%S)"
if [ -z "$OUTPUT_DIR" ]; then
    OUTPUT_DIR="$ROOT/logs/accessibility/accessibility-smoke_$TS"
fi
mkdir -p "$OUTPUT_DIR"

REPORT="$OUTPUT_DIR/accessibility-smoke.json"
LOG="$OUTPUT_DIR/accessibility-tests.log"
: > "$LOG"

json_escape() {
    python3 -c 'import json,sys; print(json.dumps(sys.stdin.read())[1:-1])'
}

run_test() {
    local label="$1"
    local project="$2"
    local filter="$3"

    {
        echo "================================================="
        echo "$label"
        echo "================================================="
    } >> "$LOG"
    dotnet test "$project" -c "$CONFIG" --filter "$filter" --logger "console;verbosity=minimal" >> "$LOG" 2>&1
}

overall="PASS"
if ! run_test "Core semantic command registry" \
    "Excise.Core.Tests/Excise.Core.Tests.csproj" \
    "FullyQualifiedName~PdfCommandRegistryTests"; then
    overall="FAIL"
fi

if ! run_test "CLI command metadata" \
    "Excise.Cli.Tests/Excise.Cli.Tests.csproj" \
    "FullyQualifiedName~CommandMetadataCommandTests"; then
    overall="FAIL"
fi

if ! run_test "GUI accessibility regression checks" \
    "Excise.App.Tests/Excise.App.Tests.csproj" \
    "FullyQualifiedName~AccessibilityRegressionTests|FullyQualifiedName~GuiWorkflowCoverageMatrixTests"; then
    overall="FAIL"
fi

platform="$(uname -s 2>/dev/null || echo unknown)"
probe_status="MANUAL_REQUIRED"
probe_detail="Platform accessibility-tree inspection requires a dedicated runner with assistive-technology permissions."

case "$platform" in
    Darwin)
        if [ "${EXCISE_ACCESSIBILITY_ALLOW_PLATFORM_PROBE:-0}" = "1" ] && command -v osascript >/dev/null 2>&1; then
            if osascript -e 'tell application "System Events" to get UI elements enabled' >/dev/null 2>&1; then
                probe_status="PASS"
                probe_detail="macOS System Events accessibility probe is available on this runner."
            else
                probe_status="MANUAL_REQUIRED"
                probe_detail="macOS Accessibility permission is not granted to this terminal/session."
            fi
        else
            probe_detail="macOS AX/VoiceOver tree inspection is documented but not run by default because System Events needs Accessibility permission and may require foreground app focus."
        fi
        ;;
    Linux)
        if command -v busctl >/dev/null 2>&1 && busctl --user list >/dev/null 2>&1; then
            probe_status="PASS"
            probe_detail="User D-Bus is available for an AT-SPI/GNOME accessibility probe on a dedicated runner."
        else
            probe_status="MANUAL_REQUIRED"
            probe_detail="AT-SPI/GNOME accessibility-tree inspection needs a desktop session with user D-Bus/AT-SPI services."
        fi
        ;;
    MINGW*|MSYS*|CYGWIN*|Windows_NT)
        probe_detail="Windows UI Automation tree inspection should run on a Windows desktop runner."
        ;;
esac

escaped_log="$(printf '%s' "$LOG" | json_escape)"
escaped_platform="$(printf '%s' "$platform" | json_escape)"
escaped_probe_detail="$(printf '%s' "$probe_detail" | json_escape)"

cat > "$REPORT" <<EOF
{
  "schemaVersion": 1,
  "generatedUtc": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
  "overallStatus": "$overall",
  "config": "$CONFIG",
  "testLog": "$escaped_log",
  "automatedChecks": [
    "Excise.Core semantic command registry",
    "Excise.Cli command metadata surface",
    "GUI accessible names/help text/tooltips/status",
    "GUI keyboard-only reachability"
  ],
  "platformProbe": {
    "platform": "$escaped_platform",
    "status": "$probe_status",
    "detail": "$escaped_probe_detail"
  }
}
EOF

echo "Accessibility smoke report: $REPORT"
echo "Accessibility test log: $LOG"

[ "$overall" = "PASS" ]
