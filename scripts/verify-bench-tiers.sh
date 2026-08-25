#!/usr/bin/env bash
# Verify the benchmark tier manifests (#1120) are REPRODUCIBLE: every file a
# tier names, IF its corpus is present, must exist and its sha256 must match.
# A file whose corpus is absent is skipped (it will be checked when fetched) —
# so this runs in t0 without needing the gitignored corpora, and still catches
# a manifest that drifted from the bytes it claims.
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
TIERS="$ROOT/tests/bench-tiers"
CORPORA="$ROOT/test-pdfs"

sha() { command -v sha256sum >/dev/null && sha256sum "$1" | awk '{print $1}' || shasum -a 256 "$1" | awk '{print $1}'; }

problems=0 checked=0 skipped=0
for manifest in "$TIERS"/tier-*.tsv; do
    [ -f "$manifest" ] || continue
    while IFS=$'\t' read -r corpus rel expected _note; do
        case "$corpus" in \#*|"") continue ;; esac
        file="$CORPORA/$corpus/$rel"
        if [ ! -e "$file" ]; then
            # Corpus absent -> skip. Present-but-missing IS a problem.
            if [ -d "$CORPORA/$corpus" ]; then
                echo "✗ $corpus: named file missing though corpus is present: $rel"
                problems=$((problems+1))
            else
                skipped=$((skipped+1))
            fi
            continue
        fi
        actual="$(sha "$file")"
        if [ "$actual" != "$expected" ]; then
            echo "✗ $corpus/$rel: sha256 mismatch (manifest $expected, actual $actual)"
            problems=$((problems+1))
        fi
        checked=$((checked+1))
    done < "$manifest"
done

echo "bench-tiers: $checked verified, $skipped skipped (corpus absent), $problems problem(s)"
[ "$problems" -eq 0 ] || { echo "FAIL: a tier manifest no longer matches the bytes it names — rebuild with scripts/build-bench-tiers.py"; exit 1; }
