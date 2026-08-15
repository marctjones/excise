#!/usr/bin/env bash
# Profile peak RSS for one dotnet test filter, optionally split on OR terms.
#
# Intended for #861-style investigations: run each class in a large chunk as
# its own testhost, record peak RSS, and identify whether one class dominates
# or memory only spikes when classes share a process.
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT" || exit 1

CONFIG="Debug"
PROJECT=""
FILTER=""
OUTPUT=""
SPLIT_OR=0

usage() {
    cat <<'EOF'
Usage: scripts/profile-test-rss-by-filter.sh --project <csproj> --filter <expr> [options]

Options:
  --configuration <cfg>   Build configuration (default: Debug)
  --output <dir>          Output directory (default: logs/test-rss-profile_<timestamp>)
  --split-or              Split FILTER on | and run each term in its own testhost
  -h, --help              Show this help

The script writes:
  results.tsv             filter, status, duration, peak RSS, CPU, test count, log path
  *.log                   dotnet test output per filter
  *.rusage                /usr/bin/time output per filter
EOF
}

while [ $# -gt 0 ]; do
    case "$1" in
        --project) PROJECT="${2:-}"; shift 2 ;;
        --filter) FILTER="${2:-}"; shift 2 ;;
        --configuration|-c) CONFIG="${2:-Debug}"; shift 2 ;;
        --output|-o) OUTPUT="${2:-}"; shift 2 ;;
        --split-or) SPLIT_OR=1; shift ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Unknown option: $1" >&2; usage; exit 2 ;;
    esac
done

if [ -z "$PROJECT" ] || [ -z "$FILTER" ]; then
    usage >&2
    exit 2
fi

if [ ! -f "$PROJECT" ]; then
    echo "Project not found: $PROJECT" >&2
    exit 2
fi

OUTPUT="${OUTPUT:-$ROOT/logs/test-rss-profile_$(date +%Y%m%d_%H%M%S)}"
mkdir -p "$OUTPUT"

case "$(uname -s)" in
    Darwin|FreeBSD|OpenBSD|NetBSD) TIME_FLAG="-l" ;;
    *) TIME_FLAG="-v" ;;
esac

RESULTS="$OUTPUT/results.tsv"
printf 'filter\tstatus\tduration_s\tpeak_rss_mb\tcpu_s\ttotal_tests\tlog\n' > "$RESULTS"

filters=()
if [ "$SPLIT_OR" = "1" ]; then
    IFS='|' read -r -a filters <<< "$FILTER"
else
    filters=("$FILTER")
fi

slugify() {
    printf '%s' "$1" | tr -c 'A-Za-z0-9._-' '_' | cut -c1-180
}

extract_total_tests() {
    local log="$1"
    local total
    total="$(grep -oE 'Total: *[0-9]+' "$log" 2>/dev/null | grep -oE '[0-9]+' \
        | awk '{s+=$1} END {print s+0}')"
    if [ "${total:-0}" = "0" ]; then
        total="$(grep -oE 'Total tests: *[0-9]+' "$log" 2>/dev/null | grep -oE '[0-9]+' \
            | awk '{s+=$1} END {print s+0}')"
    fi
    printf '%s' "${total:-0}"
}

extract_rusage() {
    local rusage="$1"
    local rss_mb="" cpu_s="" bsd_bytes="" gnu_kb=""
    if [ -s "$rusage" ]; then
        bsd_bytes="$(awk '/maximum resident set size/ { print $1; exit }' "$rusage" 2>/dev/null)"
        gnu_kb="$(awk -F: '/Maximum resident set size/ { gsub(/ /,"",$2); print $2; exit }' "$rusage" 2>/dev/null)"
        if [ -n "$bsd_bytes" ]; then
            rss_mb="$(awk -v b="$bsd_bytes" 'BEGIN { printf "%.0f", b/1048576 }')"
        elif [ -n "$gnu_kb" ]; then
            rss_mb="$(awk -v k="$gnu_kb" 'BEGIN { printf "%.0f", k/1024 }')"
        fi
        cpu_s="$(awk '/ user / && / sys/ { for(i=1;i<=NF;i++) if($i=="user") u=$(i-1); for(i=1;i<=NF;i++) if($i=="sys") s=$(i-1); printf "%.0f", u+s; exit }' "$rusage" 2>/dev/null)"
    fi
    printf '%s\t%s' "${rss_mb:-}" "${cpu_s:-}"
}

overall=0
index=0
for raw_filter in "${filters[@]}"; do
    filter="$(printf '%s' "$raw_filter" | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"
    [ -n "$filter" ] || continue
    index=$((index + 1))

    slug="$(printf '%02d_%s' "$index" "$(slugify "$filter")")"
    log="$OUTPUT/$slug.log"
    rusage="$OUTPUT/$slug.rusage"
    exit_file="$OUTPUT/$slug.exit"
    start="$(date +%s)"
    rc=0
    time_rc=0

    echo "[$index/${#filters[@]}] $filter"
    if [ -x /usr/bin/time ]; then
        /usr/bin/time $TIME_FLAG sh -c '
            exit_file="$1"; log="$2"; shift 2
            "$@" > "$log" 2>&1
            printf "%s" "$?" > "$exit_file"
        ' test-rss "$exit_file" "$log" \
            dotnet test "$PROJECT" --no-build -c "$CONFIG" \
                --filter "$filter" --logger "console;verbosity=minimal" \
            2>"$rusage" || time_rc=$?
    else
        dotnet test "$PROJECT" --no-build -c "$CONFIG" \
            --filter "$filter" --logger "console;verbosity=minimal" \
            > "$log" 2>&1 || rc=$?
        printf "%s" "$rc" > "$exit_file"
    fi
    if [ -s "$exit_file" ]; then
        rc="$(cat "$exit_file")"
    elif [ "$time_rc" != "0" ]; then
        rc="$time_rc"
    fi

    duration=$(( $(date +%s) - start ))
    total="$(extract_total_tests "$log")"
    IFS=$'\t' read -r rss_mb cpu_s <<< "$(extract_rusage "$rusage")"

    status="PASS"
    if [ "$rc" != "0" ]; then
        status="FAIL"
        overall=1
    elif [ "${total:-0}" = "0" ]; then
        status="ZERO_TESTS"
        overall=1
    fi

    printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\n' \
        "$filter" "$status" "$duration" "${rss_mb:-}" "${cpu_s:-}" "$total" "$log" >> "$RESULTS"

    echo "  $status duration=${duration}s rss=${rss_mb:-?}MB tests=$total"
done

echo
echo "Results: $RESULTS"
echo "Top peak RSS:"
tail -n +2 "$RESULTS" | sort -t"$(printf '\t')" -k4,4nr | head -10 \
    | awk -F'\t' '{ printf "  %6s MB  %s (%s tests, %ss)\n", $4, $1, $6, $3 }'

exit "$overall"
