#!/usr/bin/env bash
# Publish/package the PdfEditor GUI with Native AOT and write durable evidence.

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$ROOT" || exit 1

CONFIG="Release"
RID=""
VERSION=""
OUTPUT="$ROOT/logs/aot-smoke_$(date +%Y%m%d_%H%M%S)"
PACKAGE=1
RUN_GUI_SMOKE=0
GUI_MODE="direct-exec"
GUI_TIMEOUT=30

usage() {
    cat <<'EOF'
Publish/package PdfEditor with Native AOT and write JSON/markdown evidence.

Usage:
  scripts/run-aot-smoke.sh [options]

Options:
  --config <Debug|Release>  Build configuration. Default: Release.
  --rid <rid>               Runtime identifier. Default: host RID.
  --version <v>             Package version. Default: latest git tag or 0.0.0-local.
  --output <dir>            Evidence directory. Default: logs/aot-smoke_<timestamp>.
  --no-package              Raw dotnet publish only. Default packages on macOS.
  --gui-smoke               Run packaged GUI smoke after macOS .app build.
  --gui-mode <mode>         packaged GUI mode: direct-exec or background-open. Default: direct-exec.
  --gui-timeout <seconds>   Packaged GUI startup timeout. Default: 30.
  -h, --help                Show this help.
EOF
}

while [ "$#" -gt 0 ]; do
    case "$1" in
        --config) CONFIG="${2:-}"; shift 2 ;;
        --config=*) CONFIG="${1#*=}"; shift ;;
        --rid) RID="${2:-}"; shift 2 ;;
        --rid=*) RID="${1#*=}"; shift ;;
        --version) VERSION="${2:-}"; shift 2 ;;
        --version=*) VERSION="${1#*=}"; shift ;;
        --output) OUTPUT="${2:-}"; shift 2 ;;
        --output=*) OUTPUT="${1#*=}"; shift ;;
        --no-package) PACKAGE=0; shift ;;
        --gui-smoke) RUN_GUI_SMOKE=1; shift ;;
        --gui-mode) GUI_MODE="${2:-}"; shift 2 ;;
        --gui-mode=*) GUI_MODE="${1#*=}"; shift ;;
        --gui-timeout) GUI_TIMEOUT="${2:-}"; shift 2 ;;
        --gui-timeout=*) GUI_TIMEOUT="${1#*=}"; shift ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Unknown argument: $1" >&2; usage >&2; exit 2 ;;
    esac
done

host_rid() {
    case "$(uname -s)" in
        Darwin)
            case "$(uname -m)" in
                arm64) printf 'osx-arm64' ;;
                *) printf 'osx-x64' ;;
            esac
            ;;
        Linux)
            case "$(uname -m)" in
                aarch64|arm64) printf 'linux-arm64' ;;
                *) printf 'linux-x64' ;;
            esac
            ;;
        *)
            printf 'win-x64'
            ;;
    esac
}

json_escape() {
    local s="${1:-}"
    s="${s//\\/\\\\}"
    s="${s//\"/\\\"}"
    s="${s//$'\n'/\\n}"
    s="${s//$'\r'/}"
    printf '%s' "$s"
}

size_kb() {
    local path="$1"
    if [ -e "$path" ]; then
        du -sk "$path" 2>/dev/null | awk '{print $1}'
    else
        printf '0'
    fi
}

file_size_bytes() {
    local path="$1"
    if [ ! -e "$path" ]; then
        printf '0'
        return
    fi
    stat -f %z "$path" 2>/dev/null || stat -c %s "$path" 2>/dev/null || printf '0'
}

absolute_path() {
    local path="$1"
    if [ -e "$path" ]; then
        local dir
        local base
        dir="$(cd "$(dirname "$path")" && pwd)"
        base="$(basename "$path")"
        printf '%s/%s' "$dir" "$base"
    else
        printf '%s' "$path"
    fi
}

RID="${RID:-$(host_rid)}"
VERSION="${VERSION:-$(git describe --tags --abbrev=0 2>/dev/null | sed 's/^v//' || echo 0.0.0-local)}"
mkdir -p "$OUTPUT"

BUILD_LOG="$OUTPUT/aot-publish.log"
WARNINGS_TXT="$OUTPUT/aot-warnings.txt"
JSON_REPORT="$OUTPUT/aot-smoke.json"
MD_REPORT="$OUTPUT/aot-smoke.md"
PUBLISH_DIR="$OUTPUT/publish"
DIST_DIR="$OUTPUT/dist"
SYMBOLS_DIR="$OUTPUT/symbols"
APP_PATH="$DIST_DIR/pdfe.app"
GUI_OUTPUT="$OUTPUT/gui-smoke"
overall=0
build_rc=0
gui_rc=0
package_path=""
binary_path=""
publish_kind="raw-publish"

: > "$BUILD_LOG"
: > "$WARNINGS_TXT"

if [ "$PACKAGE" = "1" ] && [ "$(uname -s)" = "Darwin" ]; then
    publish_kind="macos-app-aot"
    echo "[aot] packaging macOS app for $RID" | tee -a "$BUILD_LOG"
    scripts/build-macos-app.sh \
        --version "$VERSION" \
        --rid "$RID" \
        --output "$DIST_DIR" \
        --aot \
        --symbols-output "$SYMBOLS_DIR" >> "$BUILD_LOG" 2>&1 || build_rc=$?
    arch="${RID#osx-}"
    package_path="$DIST_DIR/pdfe-${VERSION}-macos-${arch}.zip"
    binary_path="$APP_PATH/Contents/MacOS/PdfEditor"
else
    PACKAGE=0
    echo "[aot] raw publish for $RID" | tee -a "$BUILD_LOG"
    rm -rf "$PUBLISH_DIR"
    dotnet publish PdfEditor/PdfEditor.csproj \
        -c "$CONFIG" \
        -r "$RID" \
        --self-contained true \
        -p:PublishAot=true \
        -p:PublishReadyToRun=false \
        -p:EnableScripting=false \
        -p:IncludeTessdataInApp=false \
        -o "$PUBLISH_DIR" >> "$BUILD_LOG" 2>&1 || build_rc=$?
    binary_path="$PUBLISH_DIR/PdfEditor"
    [ -f "$PUBLISH_DIR/PdfEditor.exe" ] && binary_path="$PUBLISH_DIR/PdfEditor.exe"
fi

grep -E 'warning (IL[0-9]+|[A-Z]+[0-9]+):' "$BUILD_LOG" > "$WARNINGS_TXT" 2>/dev/null || true
warning_count="$(wc -l < "$WARNINGS_TXT" | tr -d ' ')"

if [ "$build_rc" != "0" ] || [ ! -x "$binary_path" ]; then
    overall=1
fi

if [ "$RUN_GUI_SMOKE" = "1" ]; then
    if [ "$PACKAGE" = "1" ] && [ -d "$APP_PATH" ]; then
        scripts/run-packaged-gui-smoke.sh \
            --app "$APP_PATH" \
            --pdf "$ROOT/test-pdfs/smoke/irs-w9.pdf" \
            --output "$GUI_OUTPUT" \
            --mode "$GUI_MODE" \
            --timeout "$GUI_TIMEOUT" >> "$BUILD_LOG" 2>&1 || gui_rc=$?
        [ "$gui_rc" = "0" ] || overall=1
    else
        echo "[aot] GUI smoke requested, but no macOS .app package was built." >> "$BUILD_LOG"
        gui_rc=2
        overall=1
    fi
fi

binary_path="$(absolute_path "$binary_path")"
[ -n "$package_path" ] && package_path="$(absolute_path "$package_path")"
symbols_dir_abs="$(absolute_path "$SYMBOLS_DIR")"
app_path_abs="$(absolute_path "$APP_PATH")"

runtime_kb="$(size_kb "$PUBLISH_DIR")"
if [ "$PACKAGE" = "1" ]; then
    runtime_kb="$(size_kb "$APP_PATH")"
fi
package_bytes="$(file_size_bytes "$package_path")"
binary_bytes="$(file_size_bytes "$binary_path")"
symbols_kb="$(size_kb "$SYMBOLS_DIR")"

{
    printf '{\n'
    printf '  "schemaVersion": 1,\n'
    printf '  "issues": ["#590", "#591", "#593", "#594", "#595"%s],\n' "$([ "$RUN_GUI_SMOKE" = "1" ] && printf ', "#592"')"
    printf '  "generatedUtc": "%s",\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    printf '  "status": "%s",\n' "$([ "$overall" = "0" ] && printf PASS || printf FAIL)"
    printf '  "configuration": "%s",\n' "$(json_escape "$CONFIG")"
    printf '  "rid": "%s",\n' "$(json_escape "$RID")"
    printf '  "version": "%s",\n' "$(json_escape "$VERSION")"
    printf '  "publishKind": "%s",\n' "$(json_escape "$publish_kind")"
    printf '  "buildExitCode": %s,\n' "$build_rc"
    printf '  "warningCount": %s,\n' "$warning_count"
    printf '  "guiSmoke": {"requested": %s, "exitCode": %s, "output": "%s"},\n' \
        "$([ "$RUN_GUI_SMOKE" = "1" ] && printf true || printf false)" \
        "$gui_rc" \
        "$(json_escape "$GUI_OUTPUT")"
    printf '  "artifacts": {\n'
    printf '    "buildLog": "%s",\n' "$(json_escape "$BUILD_LOG")"
    printf '    "warnings": "%s",\n' "$(json_escape "$WARNINGS_TXT")"
    printf '    "app": "%s",\n' "$(json_escape "$app_path_abs")"
    printf '    "binary": "%s",\n' "$(json_escape "$binary_path")"
    printf '    "package": "%s",\n' "$(json_escape "$package_path")"
    printf '    "symbols": "%s"\n' "$(json_escape "$symbols_dir_abs")"
    printf '  },\n'
    printf '  "sizes": {\n'
    printf '    "runtimeKb": %s,\n' "$runtime_kb"
    printf '    "symbolsKb": %s,\n' "$symbols_kb"
    printf '    "binaryBytes": %s,\n' "$binary_bytes"
    printf '    "packageBytes": %s\n' "$package_bytes"
    printf '  }\n'
    printf '}\n'
} > "$JSON_REPORT"

{
    printf '# Native AOT Smoke\n\n'
    printf -- '- Status: `%s`\n' "$([ "$overall" = "0" ] && printf PASS || printf FAIL)"
    printf -- '- RID: `%s`\n' "$RID"
    printf -- '- Version: `%s`\n' "$VERSION"
    printf -- '- Publish kind: `%s`\n' "$publish_kind"
    printf -- '- Build exit code: `%s`\n' "$build_rc"
    printf -- '- Warning count: `%s`\n' "$warning_count"
    printf -- '- Runtime size: `%s KB`\n' "$runtime_kb"
    printf -- '- Symbol size: `%s KB`\n' "$symbols_kb"
    printf -- '- Binary bytes: `%s`\n' "$binary_bytes"
    printf -- '- Package bytes: `%s`\n\n' "$package_bytes"
    printf '## Artifacts\n\n'
    printf -- '- Build log: `%s`\n' "$BUILD_LOG"
    printf -- '- Warnings: `%s`\n' "$WARNINGS_TXT"
    printf -- '- JSON: `%s`\n' "$JSON_REPORT"
    [ -n "$package_path" ] && printf -- '- Package: `%s`\n' "$package_path"
    [ "$RUN_GUI_SMOKE" = "1" ] && printf -- '- GUI smoke: `%s`\n' "$GUI_OUTPUT"
} > "$MD_REPORT"

echo "AOT smoke: $([ "$overall" = "0" ] && printf PASS || printf FAIL)"
echo "Report: $JSON_REPORT"
exit "$overall"
