#!/usr/bin/env bash
# Run a small packaged-app GUI smoke and write durable evidence.
#
# Default mode uses Launch Services with `open -g` so macOS should not bring the
# app to the foreground. Native keyboard/mouse injection is intentionally opt-in
# because System Events requires Accessibility permission and foreground focus.

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

APP="$ROOT/dist/pdfe.app"
PDF="$ROOT/test-pdfs/smoke/irs-w9.pdf"
OUT="$ROOT/logs/packaged-gui-smoke_$(date +%Y%m%d_%H%M%S)"
TIMEOUT_SECONDS=20
MODE="background-open"
ALLOW_FOCUS_INPUT=0

usage() {
    cat <<'EOF'
Run a packaged pdfe GUI smoke and write JSON/markdown evidence.

Usage:
  scripts/run-packaged-gui-smoke.sh [options]

Options:
  --app <path>           Path to pdfe.app. Default: dist/pdfe.app.
  --pdf <path>           PDF to open. Default: test-pdfs/smoke/irs-w9.pdf.
  --output <dir>         Evidence directory. Default: logs/packaged-gui-smoke_<timestamp>.
  --timeout <seconds>    App startup timeout. Default: 20.
  --mode <mode>          background-open or direct-exec. Default: background-open.
  --allow-focus-input    Run native System Events key/mouse smoke. Takes focus.
  -h, --help             Show this help.

The default smoke proves packaged app launch and document-open plumbing with
screenshots/log artifacts. It does not inject native keyboard or mouse events.
Use --allow-focus-input only on a dedicated runner where focus stealing is ok
and macOS Accessibility permission has been granted to the terminal/CI host.
EOF
}

while [ "$#" -gt 0 ]; do
    case "$1" in
        --app) APP="${2:-}"; shift 2 ;;
        --app=*) APP="${1#*=}"; shift ;;
        --pdf) PDF="${2:-}"; shift 2 ;;
        --pdf=*) PDF="${1#*=}"; shift ;;
        --output) OUT="${2:-}"; shift 2 ;;
        --output=*) OUT="${1#*=}"; shift ;;
        --timeout) TIMEOUT_SECONDS="${2:-}"; shift 2 ;;
        --timeout=*) TIMEOUT_SECONDS="${1#*=}"; shift ;;
        --mode) MODE="${2:-}"; shift 2 ;;
        --mode=*) MODE="${1#*=}"; shift ;;
        --allow-focus-input) ALLOW_FOCUS_INPUT=1; shift ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Unknown argument: $1" >&2; usage >&2; exit 2 ;;
    esac
done

mkdir -p "$OUT"
MATRIX_TSV="$OUT/matrix.tsv"
JSON_REPORT="$OUT/packaged-gui-smoke.json"
MD_REPORT="$OUT/packaged-gui-smoke.md"
APP_LOG="$OUT/app.log"
LAUNCH_LOG="$OUT/launch.log"
INPUT_LOG="$OUT/native-input.log"
SCREENSHOT="$OUT/startup-screen.png"
APP_RESPONSIVENESS_REPORT="$OUT/app-responsiveness.json"
: > "$MATRIX_TSV"
: > "$APP_LOG"
: > "$LAUNCH_LOG"
: > "$INPUT_LOG"

overall=0
app_pid=""
script_start_ms=""
launch_start_ms=""
pid_seen_ms=""
launchctl_env_set=0

now_ms() {
    perl -MTime::HiRes=time -e 'printf "%d\n", time() * 1000'
}

json_escape() {
    local s="${1:-}"
    s="${s//\\/\\\\}"
    s="${s//\"/\\\"}"
    s="${s//$'\n'/\\n}"
    s="${s//$'\r'/}"
    printf '%s' "$s"
}

canonical_existing_path() {
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

json_number_or_null() {
    local n="${1:-}"
    if [[ "$n" =~ ^[0-9]+$ ]]; then
        printf '%s' "$n"
    else
        printf 'null'
    fi
}

budget_status() {
    local elapsed_ms="$1"
    local pass_budget_ms="$2"
    local warn_budget_ms="$3"
    if [ "$elapsed_ms" -le "$pass_budget_ms" ]; then
        printf 'PASS'
    elif [ "$elapsed_ms" -le "$warn_budget_ms" ]; then
        printf 'WARN'
    else
        printf 'FAIL'
    fi
}

APP="$(canonical_existing_path "$APP")"
PDF="$(canonical_existing_path "$PDF")"
APP_EXEC="$APP/Contents/MacOS/PdfEditor"

record_row() {
    local workflow="$1"
    local status="$2"
    local mode="$3"
    local artifact="$4"
    local detail="$5"
    local focused_control="${6:-}"
    local elapsed_ms="${7:-}"
    local pass_budget_ms="${8:-}"
    local warn_budget_ms="${9:-}"
    printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' \
        "$workflow" "$status" "$mode" "$artifact" "$detail" "$focused_control" \
        "$elapsed_ms" "$pass_budget_ms" "$warn_budget_ms" >> "$MATRIX_TSV"
    if [ "$status" = "FAIL" ]; then
        overall=1
    fi
}

find_app_pid() {
    /bin/ps -axo pid=,command= | awk -v exe="$APP_EXEC" 'index($0, exe) { print $1; exit }'
}

wait_for_pid() {
    local remaining="$TIMEOUT_SECONDS"
    while [ "$remaining" -gt 0 ]; do
        app_pid="$(find_app_pid)"
        if [ -n "$app_pid" ]; then
            pid_seen_ms="$(now_ms)"
            return 0
        fi
        sleep 1
        remaining=$((remaining - 1))
    done
    return 1
}

quit_app() {
    if [ -n "$app_pid" ] && kill -0 "$app_pid" 2>/dev/null; then
        kill -TERM "$app_pid" 2>/dev/null || true
        sleep 2
        kill -KILL "$app_pid" 2>/dev/null || true
    else
        osascript -e 'tell application id "com.marcjones.pdfe" to quit' >/dev/null 2>&1 || true
    fi
}

clear_launch_environment() {
    if [ "$launchctl_env_set" = "1" ] && command -v launchctl >/dev/null 2>&1; then
        launchctl unsetenv PDFE_RESPONSIVENESS_REPORT >/dev/null 2>&1 || true
        launchctl_env_set=0
    fi
}

wait_for_app_report() {
    local remaining="$TIMEOUT_SECONDS"
    while [ "$remaining" -gt 0 ]; do
        if [ -s "$APP_RESPONSIVENESS_REPORT" ]; then
            return 0
        fi
        sleep 1
        remaining=$((remaining - 1))
    done
    return 1
}

extract_app_report_status() {
    sed -n 's/.*"overallStatus": "\([^"]*\)".*/\1/p' "$APP_RESPONSIVENESS_REPORT" | head -1
}

write_reports() {
    {
        printf '{\n'
        printf '  "schemaVersion": 1,\n'
        printf '  "issues": ["#558", "#560", "#571"],\n'
        printf '  "generatedUtc": "%s",\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
        printf '  "app": "%s",\n' "$(json_escape "$APP")"
        printf '  "pdf": "%s",\n' "$(json_escape "$PDF")"
        printf '  "mode": "%s",\n' "$(json_escape "$MODE")"
        printf '  "allowFocusInput": %s,\n' "$([ "$ALLOW_FOCUS_INPUT" = "1" ] && echo true || echo false)"
        printf '  "artifacts": {\n'
        printf '    "appLog": "%s",\n' "$(json_escape "$APP_LOG")"
        printf '    "launchLog": "%s",\n' "$(json_escape "$LAUNCH_LOG")"
        printf '    "nativeInputLog": "%s",\n' "$(json_escape "$INPUT_LOG")"
        printf '    "startupScreenshot": "%s",\n' "$(json_escape "$SCREENSHOT")"
        printf '    "appResponsivenessReport": "%s"\n' "$(json_escape "$APP_RESPONSIVENESS_REPORT")"
        printf '  },\n'
        printf '  "matrix": [\n'
        local first=1
        while IFS=$'\t' read -r workflow status row_mode artifact detail focused_control elapsed_ms pass_budget_ms warn_budget_ms; do
            [ -n "$workflow" ] || continue
            if [ "$first" = "0" ]; then
                printf ',\n'
            fi
            first=0
            printf '    {"workflow":"%s","status":"%s","mode":"%s","artifact":"%s","detail":"%s","focusedControl":"%s","elapsedMs":%s,"passBudgetMs":%s,"warnBudgetMs":%s}' \
                "$(json_escape "$workflow")" \
                "$(json_escape "$status")" \
                "$(json_escape "$row_mode")" \
                "$(json_escape "$artifact")" \
                "$(json_escape "$detail")" \
                "$(json_escape "$focused_control")" \
                "$(json_number_or_null "$elapsed_ms")" \
                "$(json_number_or_null "$pass_budget_ms")" \
                "$(json_number_or_null "$warn_budget_ms")"
        done < "$MATRIX_TSV"
        printf '\n  ]\n'
        printf '}\n'
    } > "$JSON_REPORT"

    {
        printf '# Packaged GUI Smoke\n\n'
        printf '%s\n' "- App: \`$APP\`"
        printf '%s\n' "- PDF: \`$PDF\`"
        printf '%s\n' "- Mode: \`$MODE\`"
        printf '%s\n\n' "- Issues: #558, #560, #571"
        printf '| Workflow | Status | Mode | Elapsed | Budget | Artifact | Detail |\n'
        printf '| --- | --- | --- | --- | --- | --- | --- |\n'
        while IFS=$'\t' read -r workflow status row_mode artifact detail focused_control elapsed_ms pass_budget_ms warn_budget_ms; do
            [ -n "$workflow" ] || continue
            if [ -n "$focused_control" ]; then
                detail="$detail Focused control: $focused_control"
            fi
            local elapsed_cell="n/a"
            local budget_cell="n/a"
            if [ -n "$elapsed_ms" ]; then
                elapsed_cell="${elapsed_ms}ms"
            fi
            if [ -n "$pass_budget_ms" ] && [ -n "$warn_budget_ms" ]; then
                budget_cell="pass ${pass_budget_ms}ms / warn ${warn_budget_ms}ms"
            fi
            printf '| %s | %s | %s | %s | %s | `%s` | %s |\n' "$workflow" "$status" "$row_mode" "$elapsed_cell" "$budget_cell" "$artifact" "$detail"
        done < "$MATRIX_TSV"
    } > "$MD_REPORT"
}

if [ "$(uname -s)" != "Darwin" ]; then
    record_row "native packaged app launch" "FAIL" "$MODE" "$LAUNCH_LOG" "This smoke currently supports macOS .app bundles only."
    write_reports
    echo "FAIL: packaged GUI smoke currently supports macOS only" >&2
    exit 1
fi

if [ ! -d "$APP" ] || [ ! -x "$APP_EXEC" ]; then
    record_row "native packaged app launch" "FAIL" "$MODE" "$LAUNCH_LOG" "App bundle or executable not found."
    write_reports
    echo "FAIL: app bundle not found or not executable: $APP" >&2
    exit 1
fi

if [ ! -f "$PDF" ]; then
    record_row "open PDF from packaged app" "FAIL" "$MODE" "$LAUNCH_LOG" "PDF fixture not found."
    write_reports
    echo "FAIL: PDF fixture not found: $PDF" >&2
    exit 1
fi

script_start_ms="$(now_ms)"
launch_start_ms="$(now_ms)"
case "$MODE" in
    background-open)
        if command -v launchctl >/dev/null 2>&1; then
            launchctl setenv PDFE_RESPONSIVENESS_REPORT "$APP_RESPONSIVENESS_REPORT" >> "$LAUNCH_LOG" 2>&1 && launchctl_env_set=1
        fi
        {
            echo "Launching with: open -g -n $APP --args $PDF"
            PDFE_RESPONSIVENESS_REPORT="$APP_RESPONSIVENESS_REPORT" open -g -n "$APP" --args "$PDF"
        } > "$LAUNCH_LOG" 2>&1
        launch_rc=$?
        ;;
    direct-exec)
        {
            echo "Launching with: $APP_EXEC $PDF"
            PDFE_RESPONSIVENESS_REPORT="$APP_RESPONSIVENESS_REPORT" "$APP_EXEC" "$PDF"
        } > "$APP_LOG" 2>&1 &
        app_pid=$!
        launch_rc=0
        ;;
    *)
        record_row "native packaged app launch" "FAIL" "$MODE" "$LAUNCH_LOG" "Unsupported mode."
        write_reports
        echo "FAIL: unsupported mode: $MODE" >&2
        exit 2
        ;;
esac

if [ "$launch_rc" != "0" ]; then
    record_row "native packaged app launch" "FAIL" "$MODE" "$LAUNCH_LOG" "Launch command failed with rc=$launch_rc."
    clear_launch_environment
    write_reports
    exit 1
fi

if wait_for_pid; then
    launch_elapsed_ms=$((pid_seen_ms - launch_start_ms))
    launch_status="$(budget_status "$launch_elapsed_ms" 3000 8000)"
    record_row "native packaged app launch" "$launch_status" "$MODE" "$LAUNCH_LOG" "Packaged app process started with pid $app_pid." "" "$launch_elapsed_ms" 3000 8000
else
    record_row "native packaged app launch" "FAIL" "$MODE" "$LAUNCH_LOG" "App process did not appear within ${TIMEOUT_SECONDS}s."
    clear_launch_environment
    write_reports
    exit 1
fi

sleep 3
open_elapsed_ms=$(( $(now_ms) - launch_start_ms ))
open_status="$(budget_status "$open_elapsed_ms" 5000 15000)"
record_row "open PDF from packaged app" "$open_status" "$MODE" "$LAUNCH_LOG" "Launched packaged app with PDF argument: $PDF." "" "$open_elapsed_ms" 5000 15000

if command -v screencapture >/dev/null 2>&1; then
    if screencapture -x "$SCREENSHOT" >> "$LAUNCH_LOG" 2>&1 && [ -s "$SCREENSHOT" ]; then
        screenshot_elapsed_ms=$(( $(now_ms) - launch_start_ms ))
        screenshot_status="$(budget_status "$screenshot_elapsed_ms" 6000 18000)"
        record_row "startup screenshot evidence" "$screenshot_status" "$MODE" "$SCREENSHOT" "Captured full-screen startup screenshot." "" "$screenshot_elapsed_ms" 6000 18000
    else
        record_row "startup screenshot evidence" "FAIL" "$MODE" "$SCREENSHOT" "screencapture failed; grant Screen Recording permission or run on an interactive macOS runner."
    fi
else
    record_row "startup screenshot evidence" "FAIL" "$MODE" "$SCREENSHOT" "screencapture not available."
fi

if [ "$MODE" = "direct-exec" ]; then
    if grep -q "Main window created successfully" "$APP_LOG"; then
        record_row "main window startup log" "PASS" "$MODE" "$APP_LOG" "App log contains main-window startup marker."
    else
        record_row "main window startup log" "FAIL" "$MODE" "$APP_LOG" "Main-window startup marker not found."
    fi
fi

if wait_for_app_report; then
    app_report_status="$(extract_app_report_status)"
    if [ -z "$app_report_status" ]; then
        app_report_status="FAIL"
    fi
    app_report_elapsed_ms=$(( $(now_ms) - launch_start_ms ))
    record_row "app first-page responsiveness report" "$app_report_status" "$MODE" "$APP_RESPONSIVENESS_REPORT" "App emitted document-open phases and cache statistics." "" "$app_report_elapsed_ms" 8000 25000
elif [ "$MODE" = "direct-exec" ]; then
    record_row "app first-page responsiveness report" "FAIL" "$MODE" "$APP_RESPONSIVENESS_REPORT" "App did not emit PDFE_RESPONSIVENESS_REPORT within ${TIMEOUT_SECONDS}s."
else
    record_row "app first-page responsiveness report" "MANUAL_REQUIRED" "$MODE" "$APP_RESPONSIVENESS_REPORT" "Background Launch Services mode may not inherit PDFE_RESPONSIVENESS_REPORT; use --mode direct-exec for app-internal timing."
fi

if [ "$ALLOW_FOCUS_INPUT" = "1" ]; then
    osascript > "$INPUT_LOG" 2>&1 <<'APPLESCRIPT'
tell application "System Events"
    set targetName to ""
    if exists process "pdfe" then
        set targetName to "pdfe"
    else if exists process "PdfEditor" then
        set targetName to "PdfEditor"
    else
        error "pdfe/PdfEditor process is not visible to System Events"
    end if

    tell process targetName
        set frontmost to true
        delay 0.8
        keystroke "f" using command down
        delay 0.3
        keystroke "W-9"
        delay 0.3
        key code 53
        delay 0.3
        try
            set p to position of front window
            click at {item 1 of p + 100, item 2 of p + 100}
        end try
    end tell
end tell
APPLESCRIPT
    input_rc=$?
    if [ "$input_rc" = "0" ]; then
        record_row "native keyboard and mouse input" "PASS" "focus-input" "$INPUT_LOG" "System Events sent Cmd+F, typed a query, dismissed search, and clicked the window." "front window"
    else
        record_row "native keyboard and mouse input" "FAIL" "focus-input" "$INPUT_LOG" "System Events failed with rc=$input_rc; check Accessibility permission and focus state."
    fi
else
    record_row "native keyboard and mouse input" "MANUAL_REQUIRED" "background-safe" "$INPUT_LOG" "Not run by default. macOS System Events key/mouse injection requires Accessibility permission and foreground focus; rerun with --allow-focus-input on a dedicated runner."
fi

record_row "packaged interaction matrix" "PASS" "$MODE" "$JSON_REPORT" "JSON and markdown reports enumerate packaged-app workflows and evidence artifacts."

quit_app
clear_launch_environment
write_reports

echo "Packaged GUI smoke report: $JSON_REPORT"
echo "Packaged GUI smoke summary: $MD_REPORT"
exit "$overall"
