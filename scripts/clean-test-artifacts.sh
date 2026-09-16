#!/usr/bin/env bash
#
# Prune generated test artifacts (#858).
#
# Nothing here is source. Every path this touches is gitignored build/test
# output that a subsequent run regenerates. What it does NOT touch is anything
# expensive to get back: test-pdfs/ corpora (gitignored but slow to refetch,
# and several gates pass VACUOUSLY without them), the NuGet cache, and bin/obj
# (deleting those just forces a full rebuild).
#
# Why this exists: on a maintainer machine Excise.App.Tests/TestResults had
# reached 36 GB across 79 run directories -- two of them alone held it all.
# `dotnet test --blame-hang-timeout` (release-smoke.sh, ci.yml) writes a hang
# dump of the test host, and a dump of a GUI test host is enormous. Nothing
# pruned them. That machine's data volume hit 98% full, and macOS grows dynamic
# swap on that volume -- starving it is how a memory spike becomes a kernel
# panic instead of an OOM kill, which is what happened on 2026-07-29.
#
# Usage:
#   scripts/clean-test-artifacts.sh              # prune, keeping recent runs
#   scripts/clean-test-artifacts.sh --dry-run    # show what would go
#   scripts/clean-test-artifacts.sh --keep 3     # keep the N newest run dirs
#   scripts/clean-test-artifacts.sh --all        # drop every run dir
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT" || exit 1

KEEP=5
DRY=0
ALL=0
while [ $# -gt 0 ]; do
    case "$1" in
        --dry-run|-n) DRY=1; shift ;;
        --keep) KEEP="${2:-5}"; shift 2 ;;
        --all) ALL=1; shift ;;
        -h|--help) sed -n '2,25p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "Unknown option: $1" >&2; exit 2 ;;
    esac
done
[ "$ALL" = "1" ] && KEEP=0

human() { du -sh "$1" 2>/dev/null | cut -f1; }

total_before="$(df -k /System/Volumes/Data 2>/dev/null | awk 'NR==2{print $4}')"
removed=0

prune_run_dirs() {
    local parent="$1" pattern="$2" label="$3"
    [ -d "$parent" ] || return 0
    local dirs
    dirs="$(ls -dt "$parent"/$pattern 2>/dev/null || true)"
    [ -n "$dirs" ] || return 0

    local i=0
    while IFS= read -r d; do
        [ -n "$d" ] || continue
        i=$(( i + 1 ))
        if [ "$i" -le "$KEEP" ]; then
            continue
        fi
        # A run whose row hit its wall-clock bound (#1283) keeps its evidence
        # regardless of age. These are the only artifact in the tree that
        # records a HANG, they are a few KB, and they cannot be regenerated —
        # the whole point of the bound is that the next run probably passes and
        # the stall is gone. Everything else here is reproducible by re-running.
        if ls "$d"/*.bound-diagnostics.txt >/dev/null 2>&1; then
            echo "  KEEPING       $(printf '%6s' "$(human "$d")")  $d  (holds bound-exceeded diagnostics, #1283)"
            continue
        fi
        if [ "$DRY" = "1" ]; then
            echo "  would remove  $(printf '%6s' "$(human "$d")")  $d"
        else
            echo "  removing      $(printf '%6s' "$(human "$d")")  $d"
            rm -rf "$d"
        fi
        removed=$(( removed + 1 ))
    done <<< "$dirs"
    echo "  ($label: kept newest $KEEP)"
}

echo "Pruning generated test artifacts (keep=$KEEP${DRY:+, dry-run})"
echo

# TestResults: trx files plus --blame hang dumps. The dumps are the bulk.
for proj in Excise.App.Tests Excise.Rendering.Tests Excise.Core.Tests \
            Excise.Cli.Tests Excise.Avalonia.Tests Excise.Ocr.Tests; do
    [ -d "$proj/TestResults" ] || continue
    echo "$proj/TestResults  ($(human "$proj/TestResults"))"
    prune_run_dirs "$proj/TestResults" '*' "TestResults"
done

# $TMPDIR hang dumps — the directory the 36 GB fix MISSED (#1283).
#
# The header above says blame "writes a hang dump of the test host" and prunes
# <project>/TestResults. Measured 2026-09-16 by fault injection: when blame
# fires on a STALLED worker it runs `createdump` into
# $TMPDIR/<guid>/dotnet_<pid>_<stamp>_hangdump.dmp — NOT into TestResults, and
# not into the log dir either. One SIGSTOP reproduction left a 5.8 GB file
# there. Nothing pruned it, on the same volume macOS grows swap on, which is
# the exact failure chain this script was written for. Blame only lands dumps
# under TestResults on the worker-DEATH path.
#
# Only our own dumps, only when they are not from a live run: `dotnet_*` /
# `<assembly>_*` named `*hangdump.dmp`, older than 60 minutes.
tmp_root="${TMPDIR:-/tmp}"
if [ -d "$tmp_root" ]; then
    dumps="$(find "$tmp_root" -maxdepth 2 -type f -name '*hangdump.dmp' -mmin +60 2>/dev/null)"
    if [ -n "$dumps" ]; then
        echo "$tmp_root  (stray blame hang dumps)"
        printf '%s\n' "$dumps" | while IFS= read -r d; do
            [ -n "$d" ] || continue
            echo "    $(human "$d")  $(basename "$d")"
            [ "$DRY" = "1" ] || rm -f "$d"
        done
        # The per-run <guid> directory blame made is then empty; take it too.
        [ "$DRY" = "1" ] || find "$tmp_root" -maxdepth 1 -type d -empty -name '*-*-*-*-*' -mmin +60 -exec rmdir {} \; 2>/dev/null
        echo "  (stray hang dumps: removed; see #1283 — blame dumps the RELAY, so these are ~6 GB and diagnostically worthless)"
    fi
fi

# Suite/gate logs. resources.tsv and the hotspot history live under logs/ too,
# so prune run DIRECTORIES only and leave loose files (history) alone.
if [ -d logs ]; then
    echo "logs/  ($(human logs))"
    prune_run_dirs logs 'full-suite_*' "full-suite runs"
    prune_run_dirs logs 'test-tier_*' "test-tier runs"
    prune_run_dirs logs 'release-smoke_2*' "release-smoke runs"
fi

echo
if [ "$DRY" = "1" ]; then
    echo "Dry run: $removed director(ies) would be removed. Re-run without --dry-run."
else
    total_after="$(df -k /System/Volumes/Data 2>/dev/null | awk 'NR==2{print $4}')"
    freed_gb="$(awk -v a="${total_after:-0}" -v b="${total_before:-0}" 'BEGIN{printf "%.1f",(a-b)/1048576}')"
    echo "Removed $removed director(ies); freed ~${freed_gb} GB."
fi
echo
echo "Not touched (expensive to regenerate): test-pdfs/ corpora, ~/.nuget, */bin, */obj."
