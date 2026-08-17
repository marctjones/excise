#!/usr/bin/env bash
#
# tag-release.sh — the only sanctioned way to tag a release.
#
# WHY
# ---
# GitHub CI cannot run most of what this project calls "the tests": the
# gitignored corpora (3,915-page four-corpus scan), the macOS packaging and
# GUI-automation gates, the benchmark/perf-budget gates, the OCR path. Those
# run only on the local (macOS) box via run-full-suite.sh --everything. A bare
# `git tag` asks for none of that — so a release could ship from a commit
# whose local tiers never ran. This script makes the tag itself demand the
# evidence:
#
#   1. Clean tree, HEAD pushed, version tag not already taken.
#   2. run-full-suite.sh --assert-green: every checkpointable step of the
#      --everything plan has a valid checkpoint. Markers carry the commit they
#      ran at plus a torn-write sentinel, and a dirty tree still cannot satisfy
#      this — but markers from DIFFERENT commits are accepted and the span is
#      reported in the tag (#1027). Requiring one commit made the suite unable
#      to finish at all: fixing a failing step meant committing, and committing
#      discarded every passing marker. A span you can read beats a rule that
#      guarantees there is nothing to read.
#   3. The never-checkpointed redaction gates re-run LIVE, now, via
#      run-full-suite.sh --resume: with everything else checkpointed, resume
#      executes exactly the ALWAYS steps. "You are your own third party" —
#      the binary this tag describes is one someone will redact with.
#   4. The cross-platform CI leg (the part GitHub CAN run) is green for this
#      SHA — or explicitly waived with a reason that is recorded in the tag.
#   5. verify-doc-claims.sh passes, and the RELEASE_CHECKLIST is surfaced.
#   6. The annotated tag embeds Release-Evidence trailers; the pre-push hook
#      (scripts/test-tier.sh --install-hook) refuses to push v* tags that
#      lack them, closing the hand-made-tag bypass.
#
# Usage:
#   scripts/tag-release.sh v3.9.0
#   scripts/tag-release.sh v3.9.0 --waive-ci "Test (Linux) hang is #939, 5th occurrence"
#   scripts/tag-release.sh v3.9.0 --dry-run     # all checks, no tag
#   scripts/tag-release.sh v3.9.0 --no-push     # tag locally, do not push
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT" || exit 1

R='\033[0;31m'; G='\033[0;32m'; Y='\033[1;33m'; N='\033[0m'
[ -t 1 ] || { R=''; G=''; Y=''; N=''; }
say() { echo -e "$1"; }
die() { say "${R}✗ $1${N}"; exit 1; }

VERSION="${1:-}"; shift || true
WAIVE_CI=""
DRY_RUN=0
NO_PUSH=0
# Must match the configuration run-full-suite.sh --assert-green uses below, or
# the evidence trailer would describe a different run's markers than the one
# that was just checked. --assert-green is invoked with no --release, so Debug.
CONFIG="Debug"
while [ $# -gt 0 ]; do
    case "$1" in
        --waive-ci) WAIVE_CI="${2:-}"; [ -n "$WAIVE_CI" ] || die "--waive-ci needs a reason"; shift 2 ;;
        --dry-run) DRY_RUN=1; shift ;;
        --no-push) NO_PUSH=1; shift ;;
        *) die "unknown option: $1" ;;
    esac
done

printf '%s' "$VERSION" | grep -qE '^v[0-9]+\.[0-9]+\.[0-9]+$' \
    || die "usage: scripts/tag-release.sh vX.Y.Z [--waive-ci \"reason\"] [--dry-run] [--no-push]"
git rev-parse --verify --quiet "refs/tags/$VERSION" >/dev/null \
    && die "tag $VERSION already exists"

# --- 1. clean, committed, pushed -----------------------------------------
git diff --quiet && git diff --cached --quiet \
    || die "working tree is dirty — commit or stash first; evidence is commit-keyed"
SHA="$(git rev-parse HEAD)"
git fetch origin --quiet 2>/dev/null || true
if ! git merge-base --is-ancestor "$SHA" "origin/$(git rev-parse --abbrev-ref HEAD)" 2>/dev/null; then
    die "HEAD is not on its origin branch — push first, so the tag points at public history"
fi
say "${G}✓${N} clean tree at $SHA, pushed"

# --- 2. local evidence: every checkpointable step green at this commit ----
say "▶ checking local full-suite evidence (run-full-suite.sh --assert-green)"
scripts/run-full-suite.sh --assert-green || exit 1

# --- 3. re-run the never-checkpointed redaction gates, live, now ----------
# With every checkpointable step green, --resume executes exactly the ALWAYS
# steps (redaction suites + true-redaction + extraction-parity). ~10 minutes.
say "▶ re-running the redaction gates live (never checkpointed, by design)"
scripts/run-full-suite.sh --everything --resume \
    || die "redaction gates failed at tag time — a release cannot ship over this"

# --- 4. the CI leg GitHub CAN run ----------------------------------------
CI_LINE=""
if [ -n "$WAIVE_CI" ]; then
    CI_LINE="WAIVED: $WAIVE_CI"
    say "${Y}⚠ CI check waived:${N} $WAIVE_CI"
else
    say "▶ checking GitHub CI for $SHA"
    CI_JSON="$(gh run list --commit "$SHA" --json databaseId,conclusion,status,workflowName 2>/dev/null)"
    # Empty output is an ERROR, not "pending" — an unmatched filter once
    # reported "queued" for an hour while CI had finished red.
    [ -n "$CI_JSON" ] && [ "$CI_JSON" != "[]" ] \
        || die "no CI runs found for $SHA — wait for CI to start, or --waive-ci with a reason"
    BAD="$(printf '%s' "$CI_JSON" | python3 -c '
import json,sys
runs=json.load(sys.stdin)
bad=[f"{r[\"workflowName\"]} #{r[\"databaseId\"]}: {r[\"status\"]}/{r.get(\"conclusion\")}"
     for r in runs if r["status"]!="completed" or r["conclusion"]!="success"]
print("\n".join(bad))')"
    if [ -n "$BAD" ]; then
        say "${R}CI is not green for $SHA:${N}"
        printf '%s\n' "$BAD"
        die "wait for green, fix, or --waive-ci \"reason (#issue)\" to record the waiver in the tag"
    fi
    CI_LINE="green ($(printf '%s' "$CI_JSON" | python3 -c 'import json,sys; print(len(json.load(sys.stdin)))') run(s) for $SHA)"
    say "${G}✓${N} CI $CI_LINE"
fi

# --- 5. docs -------------------------------------------------------------
say "▶ verify-doc-claims.sh"
scripts/verify-doc-claims.sh || die "doc claims out of sync — see docs/RELEASE_CHECKLIST.md"
say "${Y}Reminder:${N} docs/RELEASE_CHECKLIST.md has the manual steps (release notes, CHANGELOG)."

# --- 6. compose + tag ----------------------------------------------------
# Ask lib-runner for the key rather than re-deriving it. This line used to
# hard-code "full-suite_Debug_<sha>", which is right only for a Debug run: a
# --release run keys as full-suite_Release_<sha>, the glob below then matched
# nothing, `ls` failed into /dev/null, and wc printed 0 — so the tag would have
# recorded "0 checkpointed step(s)" as its evidence while --assert-green had
# just passed. An evidence trailer that can silently say zero is worse than no
# trailer, because it looks like a measurement.
source "$ROOT/scripts/lib-runner.sh"
runner_state_init "full-suite" "$CONFIG" >/dev/null
STATE_DIR="$(runner_state_dir)"
STATE_KEY="$(basename "$STATE_DIR")"
STEPS="$(ls "$STATE_DIR/"*.ckpt 2>/dev/null | wc -l | tr -d ' ')"
[ "${STEPS:-0}" -gt 0 ] \
    || die "no checkpoint markers under $STATE_DIR, yet --assert-green passed — refusing to write an evidence trailer that says 0"
# #1027: the evidence is a SPAN of commits, not necessarily one. Requiring one
# made the suite unable to finish (fix a failing step, commit, lose every
# passing marker), so the tag records what actually happened instead.
SPAN="$(runner_marker_span | wc -l | tr -d ' ')"
SPAN_LINE="all steps at $SHA"
if [ "${SPAN:-0}" -gt 1 ]; then
    SPAN_LINE="$SPAN commits: $(runner_marker_span | while read -r c; do \
        git -C "$ROOT" rev-parse --short "$c" 2>/dev/null || echo "${c:0:8}"; done | tr '\n' ' ')"
fi

MSG="$(cat <<EOF
excise $VERSION

Release-Evidence: run-full-suite --everything at $SHA
Release-Evidence-Steps: $STEPS checkpointed step(s), state $STATE_KEY
Release-Evidence-Span: $SPAN_LINE
Release-Evidence-Redaction: gates re-run live at tag time $(date -u +%Y-%m-%dT%H:%M:%SZ)
Release-Evidence-CI: $CI_LINE
EOF
)"

say ""
say "Tag message:"
printf '%s\n' "$MSG" | sed 's/^/  /'

if [ "$DRY_RUN" = "1" ]; then
    say "${Y}--dry-run: all checks passed; no tag created.${N}"
    exit 0
fi

git tag -a "$VERSION" -m "$MSG" || die "git tag failed"
say "${G}✓ tagged $VERSION${N}"

if [ "$NO_PUSH" = "1" ]; then
    say "${Y}--no-push: push later with:${N} git push origin $VERSION"
else
    git push origin "$VERSION" || die "tag push failed"
    say "${G}✓ pushed $VERSION${N}"
fi
