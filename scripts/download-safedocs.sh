#!/usr/bin/env bash
# SafeDocs (CC-MAIN-2021-31-PDF) — a removal-ROBUSTNESS subset (#1112).
#
# The SafeDocs corpus is ~8M PDFs harvested from Common Crawl, distributed as
# ~1000 zips of ~1.3 GB EACH. We do not want the corpus; we want the coherent
# SUBSET that stresses the REDACTION REMOVAL path: malformed-but-tolerated PDFs.
# #1039 is the exact shape — an unterminated BT that every viewer accepts and
# excise's removal path did not, leaking text and destroying text at once. A
# clean PDF does not exercise that; GovDocs1 already covers clean-and-diverse.
#
# So this DOWNLOADS one zip, extracts the PDFs, KEEPS only those qpdf flags as
# malformed (warnings or recoverable errors — the file still opens) up to a cap,
# and DELETES the clean ones plus the zip. The kept subset is small, gitignored,
# and every file is a known parser stressor.
#
# Licence: Common Crawl content under Common Crawl's Terms of Use (research /
# non-commercial). Used here only as offline test fixtures, never redistributed.
#
# Output:
#   test-pdfs/safedocs/<file>.pdf
#   test-pdfs/safedocs/.excise-manifest.tsv   (sha256<TAB>relpath<TAB>size)
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
TARGET="$ROOT/test-pdfs/safedocs"
BASE_URL="${SAFEDOCS_BASE:-https://digitalcorpora.s3.amazonaws.com/corpora/files/CC-MAIN-2021-31-PDF-UNTRUNCATED/zipfiles/0000-0999}"

ZIP_COUNT=1
START_ZIP=0
KEEP_MAX=400           # stop once the subset is big enough for the bench
MAX_BYTES=$((8*1024*1024))
FORCE=0

usage() {
    cat <<'EOF'
Fetch a removal-robustness subset of SafeDocs (malformed web PDFs).
Usage: scripts/download-safedocs.sh [options]
  --count N     source zips to sample (default 1, ~1.3 GB each)
  --start N     first zip index (default 0)
  --max N       stop after keeping N malformed PDFs (default 400)
  --force       re-fetch even if the subset already exists
Requires: curl, unzip, qpdf (the malformation filter).
EOF
}
while [ $# -gt 0 ]; do
    case "$1" in
        --count) ZIP_COUNT="$2"; shift 2 ;;
        --start) START_ZIP="$2"; shift 2 ;;
        --max) KEEP_MAX="$2"; shift 2 ;;
        --force) FORCE=1; shift ;;
        -h|--help) usage; exit 0 ;;
        *) echo "unknown option: $1" >&2; usage; exit 2 ;;
    esac
done

if [ "$FORCE" = 0 ] && [ -d "$TARGET" ] && [ -n "$(find "$TARGET" -name '*.pdf' -print -quit 2>/dev/null)" ]; then
    echo "safedocs subset already present ($(find "$TARGET" -name '*.pdf' | wc -l | tr -d ' ') PDFs). --force to refetch."
    exit 0
fi

command -v curl >/dev/null || { echo "curl required" >&2; exit 1; }
command -v unzip >/dev/null || { echo "unzip required" >&2; exit 1; }
command -v qpdf >/dev/null || { echo "qpdf required — it is the malformation filter" >&2; exit 1; }

mkdir -p "$TARGET"
TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT

is_malformed() { # 0 if the PDF opens but qpdf reports problems (the target class)
    head -c 5 "$1" 2>/dev/null | grep -q '%PDF' || return 1   # not a PDF at all
    local out; out="$(qpdf --check "$1" 2>&1)"; local rc=$?
    # rc 0 = clean (skip). rc 2 = warnings, rc 3 = errors but often still
    # openable — exactly the malformed-but-tolerated class. Guard against
    # totally-unparseable files (no object structure) so we keep stressors, not
    # rubble.
    [ "$rc" -eq 0 ] && return 1
    echo "$out" | grep -qiE 'operation for file failed|unable to find|not a valid PDF' && return 1
    return 0
}

kept=0
i="$START_ZIP"
end=$(( START_ZIP + ZIP_COUNT ))
while [ "$i" -lt "$end" ] && [ "$kept" -lt "$KEEP_MAX" ]; do
    zip=$(printf '%04d.zip' "$i")
    echo "==> $BASE_URL/$zip"
    if ! curl -fL --retry 3 --max-time 7200 -o "$TMP/$zip" "$BASE_URL/$zip"; then
        echo "   skip: $zip did not download" >&2; i=$((i+1)); continue
    fi
    unzip -o -q -j "$TMP/$zip" '*.pdf' -d "$TMP/ex_$i" 2>/dev/null || true
    for pdf in "$TMP/ex_$i"/*.pdf; do
        [ -e "$pdf" ] || continue
        [ "$kept" -ge "$KEEP_MAX" ] && break
        sz=$(wc -c < "$pdf" | tr -d ' ')
        [ "$sz" -gt "$MAX_BYTES" ] && { rm -f "$pdf"; continue; }
        if is_malformed "$pdf"; then
            cp "$pdf" "$TARGET/s$(printf '%04d' "$i")_$(basename "$pdf")"
            kept=$((kept+1))
        fi
        rm -f "$pdf"
    done
    rm -rf "$TMP/$zip" "$TMP/ex_$i"
    i=$((i+1))
done

manifest="$TARGET/.excise-manifest.tsv"
: > "$manifest"
sha() { command -v sha256sum >/dev/null && sha256sum "$1" | awk '{print $1}' || shasum -a 256 "$1" | awk '{print $1}'; }
for pdf in "$TARGET"/*.pdf; do
    [ -e "$pdf" ] || continue
    printf '%s\t%s\t%s\n' "$(sha "$pdf")" "$(basename "$pdf")" "$(wc -c < "$pdf" | tr -d ' ')" >> "$manifest"
done
echo "kept $kept malformed SafeDocs PDFs in $TARGET (manifest: $manifest)"
