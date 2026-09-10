#!/usr/bin/env bash
# Native AOT CLI parity gate (#1390).
#
# scripts/run-aot-smoke.sh publishes and gates only Excise.App (the GUI). Since
# #1389 the CLI also builds and runs correctly under Native AOT -- verified by
# hand, once, across info/text/render/validate/audit/commands/batch -- but
# nothing re-checks that on an ongoing basis. A future change can silently
# reintroduce a reflection-based JSON site (which the AOT analyzer catches at
# build time) or a runtime-only divergence (which it cannot).
#
# This publishes both a JIT and a Native AOT build of Excise.Cli into ISOLATED
# artifact directories (never the shared Excise.Cli/bin/obj other --no-build
# test rows depend on) and diffs their --json output on a checked-in fixture,
# normalizing only the fields that are legitimately expected to differ:
# elapsed-time fields, and runtimeMode (checked explicitly to say jit/aot
# rather than just stripped).
#
# Deliberately NOT compared: save-size-report or any command that writes PDF
# bytes. The AOT binary links the system zlib, not CoreCLR's bundled zlib-ng,
# so a save produces different (still valid) compressed bytes under AOT --
# a known, accepted difference (#1389's investigation). Comparing saved bytes
# here would fail on that difference every time, for no defect. This gate
# compares STRUCTURE AND CONTENT (JSON shape), never file size or byte
# identity of written PDFs -- exactly the constraint #1389 recorded for #1390.

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$ROOT" || exit 1

RID=""
OUTPUT="$ROOT/logs/aot-cli-parity_$(date +%Y%m%d_%H%M%S)"
FIXTURE="$ROOT/test-pdfs/smoke/irs-w9.pdf"

usage() {
    cat <<'EOF'
Compare Native AOT Excise.Cli --json output against a JIT build on a checked-in
fixture. Fails if anything but timing fields and runtimeMode differs.

Usage:
  scripts/check-aot-cli-parity.sh [options]

Options:
  --rid <rid>        Runtime identifier. Default: this machine's.
  --output <dir>     Evidence directory. Default: logs/aot-cli-parity_<timestamp>.
  --fixture <path>   PDF fixture to run every command against. Default: test-pdfs/smoke/irs-w9.pdf.
  -h, --help         Show this help.
EOF
}

while [ "$#" -gt 0 ]; do
    case "$1" in
        --rid) RID="${2:-}"; shift 2 ;;
        --rid=*) RID="${1#*=}"; shift ;;
        --output) OUTPUT="${2:-}"; shift 2 ;;
        --output=*) OUTPUT="${1#*=}"; shift ;;
        --fixture) FIXTURE="${2:-}"; shift 2 ;;
        --fixture=*) FIXTURE="${1#*=}"; shift ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Unknown argument: $1" >&2; usage >&2; exit 2 ;;
    esac
done

[ -f "$FIXTURE" ] || { echo "fixture not found: $FIXTURE" >&2; exit 2; }
command -v jq >/dev/null 2>&1 || { echo "jq is required" >&2; exit 3; }

mkdir -p "$OUTPUT"
JIT_DIR="$OUTPUT/jit-publish"
AOT_DIR="$OUTPUT/aot-publish"
JIT_OUT="$OUTPUT/jit-json"
AOT_OUT="$OUTPUT/aot-json"
mkdir -p "$JIT_OUT" "$AOT_OUT"
BUILD_LOG="$OUTPUT/build.log"
: > "$BUILD_LOG"

rid_args=()
[ -n "$RID" ] && rid_args=(--rid "$RID")

echo "[aot-cli-parity] publishing JIT build (isolated) -> $JIT_DIR" | tee -a "$BUILD_LOG"
jit_rid="$RID"
if [ -z "$jit_rid" ]; then
    case "$(uname -s)/$(uname -m)" in
        Darwin/arm64) jit_rid="osx-arm64" ;;
        Darwin/x86_64) jit_rid="osx-x64" ;;
        Linux/aarch64) jit_rid="linux-arm64" ;;
        Linux/x86_64) jit_rid="linux-x64" ;;
        *) echo "cannot infer a RID for $(uname -s)/$(uname -m); pass --rid" >&2; exit 2 ;;
    esac
fi
if ! dotnet publish "$ROOT/Excise.Cli/Excise.Cli.csproj" \
        -c Release -r "$jit_rid" --self-contained false \
        -p:PublishAot=false -p:PublishReadyToRun=false -p:PublishSingleFile=false \
        -o "$JIT_DIR" >> "$BUILD_LOG" 2>&1; then
    echo "JIT publish FAILED. Last 30 lines:" >&2
    tail -30 "$BUILD_LOG" >&2
    exit 1
fi
JIT_BIN="$JIT_DIR/excise"
[ -f "$JIT_BIN" ] || JIT_BIN="dotnet $JIT_DIR/excise.dll"

echo "[aot-cli-parity] publishing Native AOT build (isolated) -> $AOT_DIR" | tee -a "$BUILD_LOG"
if ! AOT_BIN="$(scripts/build-aot-cli.sh ${rid_args[@]+"${rid_args[@]}"} --output "$AOT_DIR" --quiet 2>>"$BUILD_LOG")"; then
    echo "AOT publish FAILED. Last 30 lines:" >&2
    tail -30 "$BUILD_LOG" >&2
    exit 1
fi
[ -x "$AOT_BIN" ] || { echo "AOT publish reported success but $AOT_BIN is not executable" >&2; exit 1; }

# Fields that legitimately differ between two separate invocations / codegen
# modes and carry no information about correctness.
VOLATILE_FIELDS='del(.. | .openMs?, .renderMs?, .writeMs?, .elapsedMs?, .durationMs?, .generatedUtc?, .timestampUtc?, .runtimeMode?)'

declare -a CASES=(
    "info|info \"\$FIXTURE\" --json"
    "text|text \"\$FIXTURE\" --json"
    "validate|validate \"\$FIXTURE\" --json"
    "audit|audit \"\$FIXTURE\" --json"
    "unredact|unredact \"\$FIXTURE\" --json"
)

overall=0
declare -a case_results=()

run_case() {
    local name="$1" args="$2" binkind="$3" outdir="$4"
    local raw="$outdir/$name.raw.json"
    local norm="$outdir/$name.norm.json"
    # A command's own exit code is not evidence of an invocation problem --
    # e.g. `validate` exits 1 when the DOCUMENT fails conformance, which is
    # the command working correctly. The only thing that matters here is
    # whether it wrote well-formed JSON to stdout.
    if [ "$binkind" = "jit" ]; then
        eval "$JIT_BIN $args" > "$raw" 2>"$outdir/$name.stderr"
    else
        eval "\"$AOT_BIN\" $args" > "$raw" 2>"$outdir/$name.stderr"
    fi
    if [ ! -s "$raw" ]; then
        echo "FAIL"
        return
    fi
    if ! jq "$VOLATILE_FIELDS" "$raw" > "$norm" 2>/dev/null; then
        echo "FAIL"
        return
    fi
    echo "OK"
}

for case_def in "${CASES[@]}"; do
    name="${case_def%%|*}"
    args="${case_def#*|}"
    jit_status="$(run_case "$name" "$args" jit "$JIT_OUT")"
    aot_status="$(run_case "$name" "$args" aot "$AOT_OUT")"

    if [ "$jit_status" != "OK" ] || [ "$aot_status" != "OK" ]; then
        echo "[$name] FAIL — jit=$jit_status aot=$aot_status (run produced no/invalid JSON)"
        case_results+=("$name:FAIL:invocation")
        overall=1
        continue
    fi

    if diff -q "$JIT_OUT/$name.norm.json" "$AOT_OUT/$name.norm.json" > /dev/null 2>&1; then
        echo "[$name] OK — JIT and AOT --json output match after normalizing timing/runtimeMode"
        case_results+=("$name:PASS:-")
    else
        echo "[$name] FAIL — output differs beyond timing/runtimeMode:"
        diff "$JIT_OUT/$name.norm.json" "$AOT_OUT/$name.norm.json" | head -20
        case_results+=("$name:FAIL:diff")
        overall=1
    fi
done

# render needs -o and writes a PNG; compared separately since its JSON has an
# extra outputPath field pointing at two different (still-comparable) paths.
render_jit_png="$JIT_OUT/render.png"
render_aot_png="$AOT_OUT/render.png"
render_jit_raw="$JIT_OUT/render.raw.json"
render_aot_raw="$AOT_OUT/render.raw.json"
render_rc=0
eval "$JIT_BIN render \"\$FIXTURE\" -o \"\$render_jit_png\" --json" > "$render_jit_raw" 2>"$JIT_OUT/render.stderr" || render_rc=$?
eval "\"$AOT_BIN\" render \"\$FIXTURE\" -o \"\$render_aot_png\" --json" > "$render_aot_raw" 2>"$AOT_OUT/render.stderr" || render_rc=$?
if [ "$render_rc" != 0 ] || [ ! -s "$render_jit_raw" ] || [ ! -s "$render_aot_raw" ]; then
    echo "[render] FAIL — invocation failed"
    case_results+=("render:FAIL:invocation")
    overall=1
else
    jit_mode="$(jq -r '.runtimeMode' "$render_jit_raw" 2>/dev/null)"
    aot_mode="$(jq -r '.runtimeMode' "$render_aot_raw" 2>/dev/null)"
    if [ "$jit_mode" != "jit" ] || [ "$aot_mode" != "aot" ]; then
        echo "[render] FAIL — runtimeMode reads jit=\"$jit_mode\" aot=\"$aot_mode\", expected jit/aot"
        case_results+=("render:FAIL:runtimeMode")
        overall=1
    else
        jq "$VOLATILE_FIELDS | del(.outputPath)" "$render_jit_raw" > "$JIT_OUT/render.norm.json" 2>/dev/null
        jq "$VOLATILE_FIELDS | del(.outputPath)" "$render_aot_raw" > "$AOT_OUT/render.norm.json" 2>/dev/null
        if diff -q "$JIT_OUT/render.norm.json" "$AOT_OUT/render.norm.json" > /dev/null 2>&1; then
            echo "[render] OK — JIT and AOT --json output match (runtimeMode correctly jit/aot)"
            case_results+=("render:PASS:-")
        else
            echo "[render] FAIL — output differs beyond timing/runtimeMode/outputPath:"
            diff "$JIT_OUT/render.norm.json" "$AOT_OUT/render.norm.json" | head -20
            case_results+=("render:FAIL:diff")
            overall=1
        fi
    fi
fi

jit_size="$(stat -f %z "$JIT_DIR/excise.dll" 2>/dev/null || stat -c %s "$JIT_DIR/excise.dll" 2>/dev/null || echo 0)"
aot_size="$(stat -f %z "$AOT_BIN" 2>/dev/null || stat -c %s "$AOT_BIN" 2>/dev/null || echo 0)"

jit_start_ms="-"
aot_start_ms="-"
if command -v python3 >/dev/null 2>&1; then
    jit_start_ms="$(python3 - "$JIT_BIN" <<'PY'
import subprocess, sys, time
cmd = sys.argv[1].split()
best = None
for _ in range(5):
    t0 = time.perf_counter()
    subprocess.run(cmd + ["--version"], capture_output=True)
    dt = (time.perf_counter() - t0) * 1000
    best = dt if best is None else min(best, dt)
print(f"{best:.1f}")
PY
)"
    aot_start_ms="$(python3 - "$AOT_BIN" <<'PY'
import subprocess, sys, time
best = None
for _ in range(5):
    t0 = time.perf_counter()
    subprocess.run([sys.argv[1], "--version"], capture_output=True)
    dt = (time.perf_counter() - t0) * 1000
    best = dt if best is None else min(best, dt)
print(f"{best:.1f}")
PY
)"
fi

JSON_REPORT="$OUTPUT/aot-cli-parity.json"
{
    printf '{\n'
    printf '  "schemaVersion": 1,\n'
    printf '  "issue": "#1390",\n'
    printf '  "generatedUtc": "%s",\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    printf '  "status": "%s",\n' "$([ "$overall" = 0 ] && printf PASS || printf FAIL)"
    printf '  "fixture": "%s",\n' "$FIXTURE"
    printf '  "jitBinaryBytes": %s,\n' "$jit_size"
    printf '  "aotBinaryBytes": %s,\n' "$aot_size"
    printf '  "jitBestStartupMs": "%s",\n' "$jit_start_ms"
    printf '  "aotBestStartupMs": "%s",\n' "$aot_start_ms"
    printf '  "cases": [\n'
    for i in "${!case_results[@]}"; do
        IFS=':' read -r cname cstatus creason <<< "${case_results[$i]}"
        printf '    {"name": "%s", "status": "%s", "reason": "%s"}%s\n' \
            "$cname" "$cstatus" "$creason" "$([ "$i" -lt $((${#case_results[@]} - 1)) ] && printf ,)"
    done
    printf '  ]\n'
    printf '}\n'
} > "$JSON_REPORT"

echo ""
echo "AOT CLI parity: $([ "$overall" = 0 ] && printf PASS || printf FAIL)"
echo "Report: $JSON_REPORT"
exit "$overall"
