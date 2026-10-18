#!/usr/bin/env bash
# Prints a 16-hex key for the EXACT working tree: HEAD, plus the tracked diff, plus the
# content of every untracked, non-ignored file. Two trees share a key only if a build
# of either sees the same bytes.
#
# Used by scripts/test-tier.sh (records a t0/t1 pass under logs/t0-pass/<key>) and by
# scripts/pre-push-hook.sh (accepts a push only when that record exists, #2079).
set -uo pipefail
cd "$(git rev-parse --show-toplevel)" || exit 1
{
    printf 'HEAD=%s\n' "$(git rev-parse --verify HEAD 2>/dev/null)"
    git diff HEAD --binary 2>/dev/null
    git ls-files -z --others --exclude-standard 2>/dev/null \
        | while IFS= read -r -d '' _f; do
            printf 'untracked %s %s\n' "$_f" "$(git hash-object -- "$_f" 2>/dev/null)"
        done
} | git hash-object --stdin | cut -c1-16
