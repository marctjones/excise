#!/usr/bin/env bash
# Run a small packaged-app GUI smoke and write durable evidence.
#
# Default mode uses Launch Services with `open -g` so macOS should not bring the
# app to the foreground. Native keyboard/mouse injection is intentionally opt-in
# because System Events requires Accessibility permission and foreground focus.

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

APP="$ROOT/dist/excise.app"
PDF="$ROOT/test-pdfs/smoke/irs-w9.pdf"
OUT="$ROOT/logs/packaged-gui-smoke_$(date +%Y%m%d_%H%M%S)"
TIMEOUT_SECONDS=20
MODE="background-open"
ALLOW_FOCUS_INPUT=0

usage() {
    cat <<'EOF'
Run a packaged excise GUI smoke and write JSON/markdown evidence.

Usage:
  scripts/run-packaged-gui-smoke.sh [options]

Options:
  --app <path>           Path to excise.app. Default: dist/excise.app.
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
RESPONSIVENESS_REQUEST_FILE="$HOME/Library/Application Support/Excise.App/responsiveness-report-request.txt"
: > "$MATRIX_TSV"
: > "$APP_LOG"
: > "$LAUNCH_LOG"
: > "$INPUT_LOG"

MATRIX_DELIMITER=$'\037'
overall=0
app_pid=""
script_start_ms=""
launch_start_ms=""
launch_start_epoch=""
pid_seen_ms=""
launchctl_env_set=0
caffeinate_pid=""

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
APP_EXEC="$APP/Contents/MacOS/Excise.App"

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
    printf '%s%s%s%s%s%s%s%s%s%s%s%s%s%s%s%s%s\n' \
        "$workflow" "$MATRIX_DELIMITER" \
        "$status" "$MATRIX_DELIMITER" \
        "$mode" "$MATRIX_DELIMITER" \
        "$artifact" "$MATRIX_DELIMITER" \
        "$detail" "$MATRIX_DELIMITER" \
        "$focused_control" "$MATRIX_DELIMITER" \
        "$elapsed_ms" "$MATRIX_DELIMITER" \
        "$pass_budget_ms" "$MATRIX_DELIMITER" \
        "$warn_budget_ms" >> "$MATRIX_TSV"
    if [ "$status" = "FAIL" ]; then
        overall=1
    fi
}

find_app_pid() {
    /bin/ps -axo pid=,command= | awk -v exe="$APP_EXEC" 'index($0, exe) { print $1; exit }'
}

pid_is_live() {
    local pid="$1"
    local state
    state="$(/bin/ps -p "$pid" -o stat= 2>/dev/null | tr -d '[:space:]')"
    [ -n "$state" ] && [[ "$state" != *Z* ]]
}

app_is_alive() {
    if [ -n "$app_pid" ] && pid_is_live "$app_pid"; then
        return 0
    fi

    local current_pid
    current_pid="$(find_app_pid)"
    if [ -n "$current_pid" ] && pid_is_live "$current_pid"; then
        app_pid="$current_pid"
        return 0
    fi

    return 1
}

wait_for_pid() {
    local remaining="$TIMEOUT_SECONDS"
    while [ "$remaining" -gt 0 ]; do
        app_pid="$(find_app_pid)"
        if [ -n "$app_pid" ] && pid_is_live "$app_pid"; then
            pid_seen_ms="$(now_ms)"
            return 0
        fi
        sleep 1
        remaining=$((remaining - 1))
    done
    return 1
}

latest_crash_report_since_launch() {
    local latest=""
    local latest_mtime=0
    local report
    for report in "$HOME/Library/Logs/DiagnosticReports"/Excise.App-*.ips; do
        [ -e "$report" ] || continue
        local mtime
        mtime="$(stat -f %m "$report" 2>/dev/null || printf '0')"
        if [ "$mtime" -ge "$launch_start_epoch" ] && [ "$mtime" -gt "$latest_mtime" ]; then
            latest="$report"
            latest_mtime="$mtime"
        fi
    done

    printf '%s' "$latest"
}

process_exit_detail() {
    local context="$1"
    local detail="App process exited before ${context}."
    local crash_report
    crash_report="$(latest_crash_report_since_launch)"
    if [ -n "$crash_report" ]; then
        detail="$detail Latest crash report: $crash_report."
    fi
    printf '%s' "$detail"
}

quit_app() {
    if [ -n "$app_pid" ] && kill -0 "$app_pid" 2>/dev/null; then
        kill -TERM "$app_pid" 2>/dev/null || true
        sleep 2
        kill -KILL "$app_pid" 2>/dev/null || true
    else
        osascript -e 'tell application id "cl.skpt.excise" to quit' >/dev/null 2>&1 || true
    fi
}

clear_launch_environment() {
    if [ "$launchctl_env_set" = "1" ] && command -v launchctl >/dev/null 2>&1; then
        launchctl unsetenv EXCISE_RESPONSIVENESS_REPORT >/dev/null 2>&1 || true
        launchctl_env_set=0
    fi
    if [ -n "$caffeinate_pid" ] && kill -0 "$caffeinate_pid" 2>/dev/null; then
        kill "$caffeinate_pid" 2>/dev/null || true
        caffeinate_pid=""
    fi
    rm -f "$RESPONSIVENESS_REQUEST_FILE" 2>/dev/null || true
}

start_display_wake_assertion() {
    local duration=$((TIMEOUT_SECONDS + 30))
    if ! command -v caffeinate >/dev/null 2>&1; then
        record_row "display wake assertion" "WARN" "$MODE" "$LAUNCH_LOG" "caffeinate is unavailable; native render timer may fail if all displays are asleep."
        return
    fi

    caffeinate -u -t "$duration" >> "$LAUNCH_LOG" 2>&1 &
    caffeinate_pid=$!
    sleep 1

    if [ -n "$caffeinate_pid" ] && kill -0 "$caffeinate_pid" 2>/dev/null; then
        record_row "display wake assertion" "PASS" "$MODE" "$LAUNCH_LOG" "Started bounded caffeinate -u assertion for ${duration}s so macOS exposes an active display link without focus input."
    else
        record_row "display wake assertion" "WARN" "$MODE" "$LAUNCH_LOG" "caffeinate exited before launch; native render timer may fail if all displays are asleep."
        caffeinate_pid=""
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

run_focus_applescript_workflow() {
    local workflow="$1"
    local detail="$2"
    local focused_control="$3"
    local pass_budget_ms="$4"
    local warn_budget_ms="$5"
    local script_path="$OUT/${workflow//[^A-Za-z0-9]/-}.applescript"
    cat > "$script_path"

    local start_ms
    start_ms="$(now_ms)"
    {
        echo
        echo "===== $workflow ====="
        osascript "$script_path"
    } >> "$INPUT_LOG" 2>&1
    local rc=$?
    local elapsed_ms=$(( $(now_ms) - start_ms ))
    local status
    if [ "$rc" = "0" ]; then
        status="$(budget_status "$elapsed_ms" "$pass_budget_ms" "$warn_budget_ms")"
    else
        status="FAIL"
        detail="$detail System Events failed with rc=$rc; check Accessibility permission and focus state."
    fi

    record_row "$workflow" "$status" "focus-input" "$INPUT_LOG" "$detail" "$focused_control" "$elapsed_ms" "$pass_budget_ms" "$warn_budget_ms"
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
        while IFS="$MATRIX_DELIMITER" read -r workflow status row_mode artifact detail focused_control elapsed_ms pass_budget_ms warn_budget_ms; do
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
        while IFS="$MATRIX_DELIMITER" read -r workflow status row_mode artifact detail focused_control elapsed_ms pass_budget_ms warn_budget_ms; do
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
start_display_wake_assertion
launch_start_ms="$(now_ms)"
launch_start_epoch="$(date +%s)"
mkdir -p "$(dirname "$RESPONSIVENESS_REQUEST_FILE")"
printf '%s\n' "$APP_RESPONSIVENESS_REPORT" > "$RESPONSIVENESS_REQUEST_FILE"
case "$MODE" in
    background-open)
        if command -v launchctl >/dev/null 2>&1; then
            launchctl setenv EXCISE_RESPONSIVENESS_REPORT "$APP_RESPONSIVENESS_REPORT" >> "$LAUNCH_LOG" 2>&1 && launchctl_env_set=1
        fi
        {
            echo "Launching with: open -g -n -a $APP $PDF --args --responsiveness-report $APP_RESPONSIVENESS_REPORT"
            EXCISE_RESPONSIVENESS_REPORT="$APP_RESPONSIVENESS_REPORT" \
                open -g -n -a "$APP" "$PDF" --args --responsiveness-report "$APP_RESPONSIVENESS_REPORT"
        } > "$LAUNCH_LOG" 2>&1
        launch_rc=$?
        ;;
    direct-exec)
        {
            echo "Launching with: $APP_EXEC --responsiveness-report $APP_RESPONSIVENESS_REPORT $PDF"
            EXCISE_RESPONSIVENESS_REPORT="$APP_RESPONSIVENESS_REPORT" \
                "$APP_EXEC" --responsiveness-report "$APP_RESPONSIVENESS_REPORT" "$PDF"
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
stable_elapsed_ms=$(( $(now_ms) - launch_start_ms ))
if app_is_alive; then
    stable_status="$(budget_status "$stable_elapsed_ms" 5000 15000)"
    record_row "packaged app stayed alive" "$stable_status" "$MODE" "$LAUNCH_LOG" "Packaged app remained alive after startup stabilization with pid $app_pid." "" "$stable_elapsed_ms" 5000 15000
else
    record_row "packaged app stayed alive" "FAIL" "$MODE" "$LAUNCH_LOG" "$(process_exit_detail "startup stabilization")" "" "$stable_elapsed_ms" 5000 15000
    clear_launch_environment
    write_reports
    exit 1
fi

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
else
    app_report_elapsed_ms=$(( $(now_ms) - launch_start_ms ))
    report_detail="App did not emit app-responsiveness.json within ${TIMEOUT_SECONDS}s from startup args, launchctl environment, or one-shot request file."
    if ! app_is_alive; then
        report_detail="$(process_exit_detail "app-internal responsiveness report")"
    fi
    record_row "app first-page responsiveness report" "FAIL" "$MODE" "$APP_RESPONSIVENESS_REPORT" "$report_detail" "" "$app_report_elapsed_ms" 8000 25000
fi

if [ "$ALLOW_FOCUS_INPUT" = "1" ]; then
    run_focus_applescript_workflow "native search typing latency" \
        "System Events focused the search box, typed a query, and dismissed search." \
        "SearchTextBox" 1000 3000 <<'APPLESCRIPT'
tell application "System Events"
    set targetName to ""
    if exists process "excise" then
        set targetName to "excise"
    else if exists process "Excise.App" then
        set targetName to "Excise.App"
    else
        error "excise/Excise.App process is not visible to System Events"
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

    run_focus_applescript_workflow "native page navigation latency" \
        "System Events sent Page Down and Page Up through the packaged window." \
        "front window" 1000 3000 <<'APPLESCRIPT'
tell application "System Events"
    set targetName to ""
    if exists process "excise" then
        set targetName to "excise"
    else if exists process "Excise.App" then
        set targetName to "Excise.App"
    else
        error "excise/Excise.App process is not visible to System Events"
    end if

    tell process targetName
        set frontmost to true
        delay 0.3
        key code 121
        delay 0.2
        key code 116
    end tell
end tell
APPLESCRIPT

    run_focus_applescript_workflow "native zoom latency" \
        "System Events sent zoom in and zoom out shortcuts through the packaged window." \
        "front window" 1000 3000 <<'APPLESCRIPT'
tell application "System Events"
    set targetName to ""
    if exists process "excise" then
        set targetName to "excise"
    else if exists process "Excise.App" then
        set targetName to "Excise.App"
    else
        error "excise/Excise.App process is not visible to System Events"
    end if

    tell process targetName
        set frontmost to true
        delay 0.3
        keystroke "+" using command down
        delay 0.2
        keystroke "-" using command down
    end tell
end tell
APPLESCRIPT

    run_focus_applescript_workflow "native redaction preview latency" \
        "System Events toggled redaction mode and clicked the page surface." \
        "front window" 1500 5000 <<'APPLESCRIPT'
tell application "System Events"
    set targetName to ""
    if exists process "excise" then
        set targetName to "excise"
    else if exists process "Excise.App" then
        set targetName to "Excise.App"
    else
        error "excise/Excise.App process is not visible to System Events"
    end if

    tell process targetName
        set frontmost to true
        delay 0.3
        keystroke "r"
        delay 0.2
        try
            set p to position of front window
            click at {item 1 of p + 180, item 2 of p + 180}
        end try
        delay 0.2
        keystroke "r"
    end tell
end tell
APPLESCRIPT
else
    record_row "native search typing latency" "MANUAL_REQUIRED" "background-safe" "$INPUT_LOG" "Not run by default. macOS System Events key/mouse injection requires Accessibility permission and foreground focus; rerun with --allow-focus-input on a dedicated runner."
    record_row "native page navigation latency" "MANUAL_REQUIRED" "background-safe" "$INPUT_LOG" "Not run by default. Page-key delivery requires foreground focus; rerun with --allow-focus-input on a dedicated runner."
    record_row "native zoom latency" "MANUAL_REQUIRED" "background-safe" "$INPUT_LOG" "Not run by default. Zoom shortcut delivery requires foreground focus; rerun with --allow-focus-input on a dedicated runner."
    record_row "native redaction preview latency" "MANUAL_REQUIRED" "background-safe" "$INPUT_LOG" "Not run by default. Redaction-mode click delivery requires foreground focus; rerun with --allow-focus-input on a dedicated runner."
fi

record_row "packaged interaction matrix" "PASS" "$MODE" "$JSON_REPORT" "JSON and markdown reports enumerate packaged-app workflows and evidence artifacts."

quit_app
clear_launch_environment
write_reports

echo "Packaged GUI smoke report: $JSON_REPORT"
echo "Packaged GUI smoke summary: $MD_REPORT"
exit "$overall"
