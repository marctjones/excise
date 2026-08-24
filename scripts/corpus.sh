#!/usr/bin/env bash
# corpus.sh — one entry point for every external corpus excise can download.
#
# WHY THIS EXISTS
# ---------------
# There were ten download-*.sh scripts, each with its own name, destination
# and idea of what "already downloaded" means, and no single place that said
# which corpora exist or which ones you need. The answer to "what do I fetch
# to run the suite?" was to read ten scripts.
#
# This is a REGISTRY + DISPATCHER, not a rewrite. tests/corpora.tsv lists what
# exists; each row still delegates to the script that already knew how to
# fetch it. Adding a corpus means adding a row, not another entry point.
#
# The data lands under test-pdfs/ (and tools/vendor/, tessdata/), all
# gitignored — corpora are large, licensed variously, and have no business in
# git history. The SCRIPTS are tracked, so a clean checkout can rebuild every
# corpus it needs and store none of them.
#
#   scripts/corpus.sh list                 what exists, and what is here
#   scripts/corpus.sh fetch --tier core    everything the local suite needs
#   scripts/corpus.sh fetch pdfjs pdfium   named corpora
#   scripts/corpus.sh fetch --all          core + extended + tool
#   scripts/corpus.sh du                   disk used, per corpus
#   scripts/corpus.sh verify               registry sanity + gitignore check
#   scripts/corpus.sh remove <name>        delete a fetched corpus (guarded)

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(dirname "$SCRIPT_DIR")"
REGISTRY="$ROOT/tests/corpora.tsv"

GREEN=$'\033[32m'; RED=$'\033[31m'; YELLOW=$'\033[33m'
BLUE=$'\033[34m'; DIM=$'\033[2m'; BOLD=$'\033[1m'; RESET=$'\033[0m'

[ -f "$REGISTRY" ] || { echo "${RED}missing registry: $REGISTRY${RESET}" >&2; exit 1; }

# ── registry access ────────────────────────────────────────────────────────
# Every reader goes through this, so a malformed row fails once, loudly,
# rather than differently in each subcommand.
rows() { awk -F'\t' '!/^#/ && NF >= 7' "$REGISTRY"; }
field() { awk -F'\t' -v n="$1" -v f="$2" '!/^#/ && NF>=7 && $1==n {print $f; exit}' "$REGISTRY"; }
names() { rows | cut -f1; }

# A corpus counts as PRESENT when its directory holds at least one file.
# Not "the directory exists": a half-deleted or freshly-mkdir'd corpus must
# read as absent, or `fetch` would skip the thing you asked it to repair.
present() {
    local dir="$ROOT/$1"
    [ -d "$dir" ] && [ -n "$(find "$dir" -type f -print -quit 2>/dev/null)" ]
}

count_pdfs() { find "$ROOT/$1" -iname '*.pdf' 2>/dev/null | wc -l | tr -d ' '; }
disk_of() { du -sh "$ROOT/$1" 2>/dev/null | cut -f1; }

# ── list ───────────────────────────────────────────────────────────────────
cmd_list() {
    printf "%s%-22s %-9s %-8s %-10s %s%s\n" "$BOLD" "NAME" "TIER" "STATUS" "ON DISK" "PURPOSE" "$RESET"
    local tier dir script size purpose status disk
    while IFS=$'\t' read -r name tier dir script size _lic purpose; do
        # Pad on the PLAIN text, then colour. printf counts escape bytes as
        # width, so colouring first silently misaligns every column.
        if [ "$tier" = "planned" ]; then
            status="$(printf '%-8s' planned)"; status="${DIM}${status}${RESET}"
            disk="$(printf '%-10s' "$script")"; disk="${DIM}${disk}${RESET}"
        elif present "$dir"; then
            status="$(printf '%-8s' present)"; status="${GREEN}${status}${RESET}"
            disk="$(printf '%-10s' "$(disk_of "$dir")")"
        else
            status="$(printf '%-8s' absent)";  status="${YELLOW}${status}${RESET}"
            disk="$(printf '%-10s' "~$size")"; disk="${DIM}${disk}${RESET}"
        fi
        printf "%-22s %-9s %s %s %s%s%s\n" \
            "$name" "$tier" "$status" "$disk" "$DIM" "$purpose" "$RESET"
    done < <(rows)
    echo
    echo "${DIM}fetch with: scripts/corpus.sh fetch --tier core${RESET}"
}

# ── fetch ──────────────────────────────────────────────────────────────────
fetch_one() {
    local name="$1"
    local tier dir script
    tier="$(field "$name" 2)"; dir="$(field "$name" 3)"; script="$(field "$name" 4)"

    if [ -z "$tier" ]; then
        echo "${RED}unknown corpus: $name${RESET}" >&2
        echo "  known: $(names | tr '\n' ' ')" >&2
        return 1
    fi
    if [ "$tier" = "planned" ]; then
        # Deliberately an ERROR, not a skip. A planned corpus is one we decided
        # we want and have not built yet; silently doing nothing would let a
        # benchmark run report coverage it does not have.
        echo "${YELLOW}$name is PLANNED — no download script yet (see $script)${RESET}" >&2
        return 2
    fi
    if [ "$FORCE" != "1" ] && present "$dir"; then
        echo "${GREEN}✓${RESET} $name already present ($(disk_of "$dir")) ${DIM}— --force to refetch${RESET}"
        return 0
    fi

    local path="$SCRIPT_DIR/$script"
    [ -x "$path" ] || [ -f "$path" ] || { echo "${RED}missing script: $path${RESET}" >&2; return 1; }

    echo "${BLUE}==>${RESET} fetching ${BOLD}$name${RESET} via $script"
    if bash "$path"; then
        if present "$dir"; then
            echo "${GREEN}✓${RESET} $name → $dir ($(disk_of "$dir"))"
        else
            # The script exited 0 and produced nothing. Reporting success here
            # is how a corpus-less machine ends up believing it has coverage.
            echo "${RED}✗ $name: $script exited 0 but $dir is empty${RESET}" >&2
            return 1
        fi
    else
        echo "${RED}✗ $name: $script failed${RESET}" >&2
        return 1
    fi
}

cmd_fetch() {
    local targets=() failed=() planned=()
    # No mapfile/readarray: they are bash 4+, and macOS ships bash 3.2, where
    # this failed with "mapfile: command not found" and then reported the far
    # more misleading "no corpora in tier 'core'".
    if [ "${1:-}" = "--tier" ]; then
        [ -n "${2:-}" ] || { echo "${RED}--tier needs a value${RESET}" >&2; exit 2; }
        while IFS= read -r line; do targets+=("$line"); done < <(
            awk -F'\t' -v t="$2" '!/^#/ && NF>=7 && $2==t {print $1}' "$REGISTRY")
        [ ${#targets[@]} -gt 0 ] || { echo "${RED}no corpora in tier '$2'${RESET}" >&2; exit 2; }
    elif [ "${1:-}" = "--all" ]; then
        while IFS= read -r line; do targets+=("$line"); done < <(
            awk -F'\t' '!/^#/ && NF>=7 && $2!="planned" {print $1}' "$REGISTRY")
    elif [ $# -gt 0 ]; then
        targets=("$@")
    else
        echo "${RED}fetch needs names, --tier <t>, or --all${RESET}" >&2; exit 2
    fi

    for n in "${targets[@]}"; do
        fetch_one "$n"
        case $? in
            0) ;;
            2) planned+=("$n") ;;
            *) failed+=("$n") ;;
        esac
    done

    echo
    if [ ${#planned[@]} -gt 0 ]; then
        echo "${YELLOW}planned, not fetched:${RESET} ${planned[*]}"
        echo "${DIM}  a planned corpus has no download script yet; the row cites the issue${RESET}"
    fi
    if [ ${#failed[@]} -gt 0 ]; then
        echo "${RED}FAILED:${RESET} ${failed[*]}" >&2
        exit 1
    fi
    # Anything requested and not delivered is a non-zero exit, planned
    # included. "You asked for a corpus and got nothing" must never look like
    # success to a script, or a benchmark run proceeds on data it lacks.
    [ ${#planned[@]} -gt 0 ] && exit 3
    echo "${GREEN}done${RESET}"
}

# ── du ─────────────────────────────────────────────────────────────────────
cmd_du() {
    local total=0
    while IFS=$'\t' read -r name tier dir _s _l _lic _p; do
        [ "$tier" = "planned" ] && continue
        present "$dir" || continue
        local kb; kb=$(du -sk "$ROOT/$dir" 2>/dev/null | cut -f1)
        total=$((total + kb))
        printf "  %-22s %8s  %s%s PDFs%s\n" "$name" "$(disk_of "$dir")" "$DIM" "$(count_pdfs "$dir")" "$RESET"
    done < <(rows)
    printf "  %-22s %8s\n" "TOTAL" "$((total / 1024)) MB"
    # The dev box has run out of disk mid-suite before; state the headroom
    # rather than making someone go looking for it.
    echo "${DIM}  free on this volume: $(df -h "$ROOT" | awk 'NR==2 {print $4}')${RESET}"
}

# ── verify ─────────────────────────────────────────────────────────────────
# The load-bearing check: every destination must be gitignored. A corpus that
# is not would be committable, and some are hundreds of MB with licences we
# have not cleared for redistribution.
cmd_verify() {
    local problems=0
    while IFS=$'\t' read -r name tier dir script _s _lic _p; do
        if [ "$tier" = "planned" ]; then
            case "$script" in
                \#*) ;;
                *) echo "${RED}✗ $name: planned rows must cite an issue, got '$script'${RESET}"; problems=$((problems+1)) ;;
            esac
            continue
        fi

        if [ ! -f "$SCRIPT_DIR/$script" ]; then
            echo "${RED}✗ $name: script not found — scripts/$script${RESET}"; problems=$((problems+1))
        fi

        # `git check-ignore` is the authority; pattern-matching .gitignore by
        # hand is how you get a rule that looks right and does not apply.
        local probe="$ROOT/$dir/.corpus-ignore-probe"
        if ! git -C "$ROOT" check-ignore -q "$probe" 2>/dev/null; then
            echo "${RED}✗ $name: $dir is NOT gitignored — a fetch here is committable${RESET}"
            problems=$((problems+1))
        fi
    done < <(rows)

    local dupes
    dupes="$(names | sort | uniq -d)"
    [ -n "$dupes" ] && { echo "${RED}✗ duplicate names: $dupes${RESET}"; problems=$((problems+1)); }

    if [ "$problems" -eq 0 ]; then
        echo "${GREEN}✓ registry OK${RESET} ($(rows | wc -l | tr -d ' ') corpora, all destinations gitignored)"
    else
        echo "${RED}$problems problem(s)${RESET}" >&2; exit 1
    fi
}

# ── remove ─────────────────────────────────────────────────────────────────
cmd_remove() {
    [ -n "${1:-}" ] || { echo "${RED}remove needs a corpus name${RESET}" >&2; exit 2; }
    local dir; dir="$(field "$1" 3)"
    [ -n "$dir" ] || { echo "${RED}unknown corpus: $1${RESET}" >&2; exit 1; }
    present "$dir" || { echo "$1 is not present"; exit 0; }

    # Guarded on purpose. Deleting corpora to reclaim disk has already cost
    # this project a re-download it did not need; make it a decision, not a
    # reflex, and say what it costs to undo.
    if [ "${2:-}" != "--yes" ]; then
        echo "${YELLOW}would delete $dir ($(disk_of "$dir"), $(count_pdfs "$dir") PDFs)${RESET}"
        echo "re-run with --yes. Refetch: scripts/corpus.sh fetch $1"
        exit 2
    fi
    rm -rf "${ROOT:?}/$dir"
    echo "${GREEN}removed${RESET} $dir"
}

case "${1:-list}" in
    list|status) shift || true; cmd_list ;;
    fetch)  shift; FORCE=0
            args=(); for a in "$@"; do [ "$a" = "--force" ] && FORCE=1 || args+=("$a"); done
            # ${args[@]+...} — expanding an EMPTY array under `set -u` is an
            # unbound-variable error in bash 3.2, so `fetch` with no arguments
            # died with exit 1 and no message instead of reaching its own
            # usage error. Same 3.2-vs-4 trap as mapfile, one line further on.
            cmd_fetch ${args[@]+"${args[@]}"} ;;
    du)     cmd_du ;;
    verify) cmd_verify ;;
    remove) shift; cmd_remove "$@" ;;
    -h|--help|help) sed -n '2,30p' "$0" | sed 's/^# \{0,1\}//' ;;
    *) echo "${RED}unknown command: $1${RESET}" >&2
       sed -n '2,30p' "$0" | sed 's/^# \{0,1\}//' >&2; exit 2 ;;
esac
