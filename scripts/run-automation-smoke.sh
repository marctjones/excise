#!/usr/bin/env bash
# Focused release gate for the stable excise CLI automation contract.

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$ROOT" || exit 1

CONFIG="Debug"
OUTPUT="$ROOT/logs/automation-smoke_$(date +%Y%m%d_%H%M%S)"
NO_BUILD=0

usage() {
    cat <<'EOF'
Run the stable excise automation smoke.

Usage:
  scripts/run-automation-smoke.sh [options]

Options:
  --config <Debug|Release>  Build/test configuration (default: Debug).
  --output <dir>            Directory for JSON/log artifacts.
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

overall=0
declare -a RESULTS

run_step() {
    local name="$1"
    shift
    local log="$OUTPUT/$name.log"
    echo "[$name] $*"
    local start
    start="$(date +%s)"
    "$@" > "$log" 2>&1
    local rc=$?
    local dur=$(( $(date +%s) - start ))
    if [ "$rc" = "0" ]; then
        echo "  PASS (${dur}s) -> $log"
        RESULTS+=("$name|PASS|${dur}s")
    else
        echo "  FAIL rc=$rc (${dur}s) -> $log"
        tail -80 "$log" | sed 's/^/    /'
        RESULTS+=("$name|FAIL|rc=$rc ${dur}s")
        overall=1
    fi
    echo ""
}

if [ "$NO_BUILD" != "1" ]; then
    run_step "build" dotnet build Excise.Cli.Tests/Excise.Cli.Tests.csproj -c "$CONFIG"
fi

run_step "cli-tests" dotnet test Excise.Cli.Tests/Excise.Cli.Tests.csproj --no-build -c "$CONFIG" \
    --filter "FullyQualifiedName~BatchAutomationCommandTests|FullyQualifiedName~CommandMetadataCommandTests" \
    --logger "console;verbosity=minimal"

SAMPLE_PDF="$ROOT/test-pdfs/smoke/irs-w9.pdf"
WORK_DIR="$OUTPUT/sample-workflow"
mkdir -p "$WORK_DIR"
WORKFLOW="$WORK_DIR/workflow.json"
cat > "$WORKFLOW" <<JSON
{
  "schemaVersion": 1,
  "steps": [
    { "id": "info", "command": "document.info", "input": "$SAMPLE_PDF" },
    { "id": "text", "command": "text.extract", "input": "$SAMPLE_PDF", "page": 1 },
    { "id": "render", "command": "render.page", "input": "$SAMPLE_PDF", "output": "w9-page-1.png", "page": 1, "dpi": 72 }
  ]
}
JSON

BATCH_STDOUT="$OUTPUT/batch-stdout.json"
BATCH_PROGRESS="$OUTPUT/batch-progress.ndjson"
BATCH_REPORT="$OUTPUT/batch-report.json"
echo "[batch-artifacts] dotnet run --project Excise.Cli/Excise.Cli.csproj -c $CONFIG -- batch $WORKFLOW --json --progress --output $BATCH_REPORT"
batch_start="$(date +%s)"
dotnet run --project Excise.Cli/Excise.Cli.csproj -c "$CONFIG" -- \
    batch "$WORKFLOW" --json --progress --output "$BATCH_REPORT" \
    > "$BATCH_STDOUT" 2> "$BATCH_PROGRESS"
batch_rc=$?
batch_dur=$(( $(date +%s) - batch_start ))
if [ "$batch_rc" != "0" ]; then
    echo "[batch-artifacts] FAIL rc=$batch_rc (${batch_dur}s)"
    tail -80 "$BATCH_STDOUT" "$BATCH_PROGRESS" | sed 's/^/    /'
    overall=1
    RESULTS+=("batch-artifacts|FAIL|rc=$batch_rc ${batch_dur}s")
elif ! grep -q '"overallStatus": "PASS"' "$BATCH_STDOUT"; then
    echo "[batch-artifacts] FAIL - final JSON did not report PASS (${batch_dur}s)"
    overall=1
    RESULTS+=("batch-artifacts|FAIL|missing PASS ${batch_dur}s")
elif ! grep -q '"type":"step-start"' "$BATCH_PROGRESS" || ! grep -q '"type":"step-complete"' "$BATCH_PROGRESS"; then
    echo "[batch-artifacts] FAIL - progress NDJSON missing start/complete events (${batch_dur}s)"
    overall=1
    RESULTS+=("batch-artifacts|FAIL|missing progress ${batch_dur}s")
else
    echo "[batch-artifacts] PASS (${batch_dur}s) -> $BATCH_STDOUT $BATCH_PROGRESS $BATCH_REPORT"
    RESULTS+=("batch-artifacts|PASS|${batch_dur}s json+progress")
fi
echo ""

STATUS="PASS"
[ "$overall" = "0" ] || STATUS="FAIL"

{
    echo "{"
    echo "  \"schemaVersion\": 1,"
    echo "  \"status\": \"$STATUS\","
    echo "  \"generatedUtc\": \"$(date -u +%Y-%m-%dT%H:%M:%SZ)\","
    echo "  \"config\": \"$CONFIG\","
    echo "  \"outputDirectory\": \"$OUTPUT\","
    echo "  \"steps\": ["
    for i in "${!RESULTS[@]}"; do
        IFS='|' read -r name status detail <<< "${RESULTS[$i]}"
        comma=","
        [ "$i" = "$((${#RESULTS[@]} - 1))" ] && comma=""
        echo "    { \"name\": \"$name\", \"status\": \"$status\", \"detail\": \"$detail\" }$comma"
    done
    echo "  ]"
    echo "}"
} > "$OUTPUT/automation-smoke.json"

echo "Automation smoke: $STATUS"
echo "Report: $OUTPUT/automation-smoke.json"
exit "$overall"
