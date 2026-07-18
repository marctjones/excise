#!/usr/bin/env bash
# Capture screenshot-backed UX/icon audit evidence for the desktop shell.

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$ROOT" || exit 1

CONFIG="Debug"
OUTPUT="$ROOT/logs/ux-icon-audit_$(date +%Y%m%d_%H%M%S)"
NO_BUILD=0

usage() {
    cat <<'EOF'
Run the excise UX/icon audit.

Usage:
  scripts/run-ux-icon-audit.sh [options]

Options:
  --config <Debug|Release>  Test configuration (default: Debug).
  --output <dir>            Directory for screenshots, JSON, and logs.
  --no-build                Skip the build step.
  -h, --help                Show this help.
EOF
}

while [ "$#" -gt 0 ]; do
    case "$1" in
        --config)
            CONFIG="${2:-}"
            [ -n "$CONFIG" ] || { echo "--config requires a value" >&2; exit 2; }
            shift 2
            ;;
        --config=*) CONFIG="${1#*=}"; shift ;;
        --output)
            OUTPUT="${2:-}"
            [ -n "$OUTPUT" ] || { echo "--output requires a value" >&2; exit 2; }
            shift 2
            ;;
        --output=*) OUTPUT="${1#*=}"; shift ;;
        --no-build) NO_BUILD=1; shift ;;
        -h|--help) usage; exit 0 ;;
        *)
            echo "Unknown argument: $1" >&2
            usage >&2
            exit 2
            ;;
    esac
done

mkdir -p "$OUTPUT"
OUTPUT="$(cd "$OUTPUT" && pwd)"
overall=0

if [ "$NO_BUILD" != "1" ]; then
    echo "[build] dotnet build Excise.App.Tests/Excise.App.Tests.csproj -c $CONFIG"
    dotnet build Excise.App.Tests/Excise.App.Tests.csproj -c "$CONFIG" > "$OUTPUT/build.log" 2>&1
    rc=$?
    if [ "$rc" != "0" ]; then
        echo "  FAIL rc=$rc -> $OUTPUT/build.log"
        tail -80 "$OUTPUT/build.log" | sed 's/^/    /'
        exit "$rc"
    fi
    echo "  PASS -> $OUTPUT/build.log"
fi

echo "[ux-icon-audit] VisualPolishAuditTests"
EXCISE_UX_AUDIT_OUTPUT="$OUTPUT" dotnet test Excise.App.Tests/Excise.App.Tests.csproj \
    --no-build -c "$CONFIG" \
    --filter "FullyQualifiedName~VisualPolishAuditTests" \
    --logger "console;verbosity=normal" \
    > "$OUTPUT/ux-icon-audit-tests.log" 2>&1
rc=$?
if [ "$rc" != "0" ]; then
    echo "  FAIL rc=$rc -> $OUTPUT/ux-icon-audit-tests.log"
    tail -120 "$OUTPUT/ux-icon-audit-tests.log" | sed 's/^/    /'
    overall=1
else
    echo "  PASS -> $OUTPUT/ux-icon-audit-tests.log"
fi

if [ ! -f "$OUTPUT/ux-icon-audit.json" ]; then
    echo "  FAIL missing $OUTPUT/ux-icon-audit.json"
    overall=1
fi

cat > "$OUTPUT/ux-icon-audit.md" <<EOF
# excise UX/Icon Audit

- Status: $([ "$overall" = "0" ] && echo "PASS" || echo "FAIL")
- Generated: $(date -u +%Y-%m-%dT%H:%M:%SZ)
- Manifest: $OUTPUT/ux-icon-audit.json
- Test log: $OUTPUT/ux-icon-audit-tests.log

Screenshots:

$(find "$OUTPUT" -maxdepth 1 -name '*.png' -print | sort | sed 's#^#- #')
EOF

echo "UX/icon audit: $([ "$overall" = "0" ] && echo "PASS" || echo "FAIL")"
echo "Report: $OUTPUT/ux-icon-audit.md"
exit "$overall"
