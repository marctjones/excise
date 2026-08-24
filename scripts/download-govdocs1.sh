#!/usr/bin/env bash
# GovDocs1 — a redaction-TARGET-diversity subset (#1113).
#
# GovDocs1 is ~1M real files harvested from .gov/.mil web servers, public
# domain, distributed as ~1000 zips of ~487 MB EACH holding MIXED file types
# (doc/xls/ppt/html/jpg/pdf/...). We do not want the corpus; we want a coherent
# SUBSET for the redaction bench: real text-bearing PDFs from many distinct
# producers, so redaction is exercised across the messy real world (every
# producer lays glyphs out differently) and the x-ray bad-redaction sweep has
# real documents to run on.
#
# So this DOWNLOADS one zip, extracts only the PDFs, KEEPS those that (a) have
# real extractable text, (b) are under a size cap, (c) add producer diversity
# (at most KEEP_PER_PRODUCER per /Producer), and DELETES everything else plus
# the zip. The kept subset is small and gitignored.
#
# Output:
#   test-pdfs/govdocs1/<zip>_<file>.pdf
#   test-pdfs/govdocs1/.excise-manifest.tsv   (sha256<TAB>relpath<TAB>size)
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
TARGET="$ROOT/test-pdfs/govdocs1"
BASE_URL="${GOVDOCS1_BASE:-https://digitalcorpora.s3.amazonaws.com/corpora/files/govdocs1/zipfiles}"

ZIP_COUNT=1              # how many source zips to sample (each ~487 MB)
START_ZIP=0             # first zip index (000, 001, ...)
MAX_BYTES=$((5*1024*1024))   # skip PDFs larger than 5 MB — bench, not archive
MIN_CHARS=200          # a PDF with no extractable text is nothing to redact
KEEP_PER_PRODUCER=8    # cap per /Producer so one producer cannot dominate
FORCE=0

usage() {
    cat <<'EOF'
Fetch a redaction-target subset of GovDocs1 (real .gov PDFs, public domain).
Usage: scripts/download-govdocs1.sh [options]
  --count N     number of source zips to sample (default 1, ~487 MB each)
  --start N     first zip index (default 0)
  --force       re-fetch even if the subset already exists
Requires: curl, unzip, and one of mutool/pdftotext (text filter) + qpdf optional.
EOF
}
while [ $# -gt 0 ]; do
    case "$1" in
        --count) ZIP_COUNT="$2"; shift 2 ;;
        --start) START_ZIP="$2"; shift 2 ;;
        --force) FORCE=1; shift ;;
        -h|--help) usage; exit 0 ;;
        *) echo "unknown option: $1" >&2; usage; exit 2 ;;
    esac
done

if [ "$FORCE" = 0 ] && [ -d "$TARGET" ] && [ -n "$(find "$TARGET" -name '*.pdf' -print -quit 2>/dev/null)" ]; then
    echo "govdocs1 subset already present ($(find "$TARGET" -name '*.pdf' | wc -l | tr -d ' ') PDFs). --force to refetch."
    exit 0
fi

command -v curl >/dev/null || { echo "curl required" >&2; exit 1; }
command -v unzip >/dev/null || { echo "unzip required" >&2; exit 1; }
TEXT_TOOL=""
command -v mutool >/dev/null && TEXT_TOOL="mutool"
[ -z "$TEXT_TOOL" ] && command -v pdftotext >/dev/null && TEXT_TOOL="pdftotext"
[ -z "$TEXT_TOOL" ] && { echo "need mutool or pdftotext to filter for text-bearing PDFs" >&2; exit 1; }

mkdir -p "$TARGET"
TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT

text_chars() { # count extractable alnum chars, cheaply
    if [ "$TEXT_TOOL" = mutool ]; then
        mutool draw -F txt -o - "$1" 1 2>/dev/null | tr -cd '[:alnum:]' | wc -c
    else
        pdftotext -f 1 -l 1 "$1" - 2>/dev/null | tr -cd '[:alnum:]' | wc -c
    fi
}
producer_of() { # a normalised /Producer key, or "unknown"
    LC_ALL=C grep -aoE '/Producer ?\([^)]{0,60}' "$1" 2>/dev/null | head -1 \
        | tr -cd '[:alnum:]' | tr 'A-Z' 'a-z' | cut -c1-24 || true
}

declare -a producer_seen_keys=()
declare -a producer_seen_counts=()
producer_bump() { # returns 0 (keep) if under cap for this producer, else 1
    local key="${1:-unknown}"; [ -z "$key" ] && key=unknown
    local i
    for i in "${!producer_seen_keys[@]}"; do
        if [ "${producer_seen_keys[$i]}" = "$key" ]; then
            if [ "${producer_seen_counts[$i]}" -ge "$KEEP_PER_PRODUCER" ]; then return 1; fi
            producer_seen_counts[$i]=$(( producer_seen_counts[i] + 1 )); return 0
        fi
    done
    producer_seen_keys+=("$key"); producer_seen_counts+=(1); return 0
}

kept=0
i="$START_ZIP"
end=$(( START_ZIP + ZIP_COUNT ))
while [ "$i" -lt "$end" ]; do
    zip=$(printf '%03d.zip' "$i")
    echo "==> $BASE_URL/$zip"
    if ! curl -fL --retry 3 --max-time 3600 -o "$TMP/$zip" "$BASE_URL/$zip"; then
        echo "   skip: $zip did not download" >&2; i=$((i+1)); continue
    fi
    unzip -o -q -j "$TMP/$zip" '*.pdf' -d "$TMP/ex_$i" 2>/dev/null || true
    for pdf in "$TMP/ex_$i"/*.pdf; do
        [ -e "$pdf" ] || continue
        sz=$(wc -c < "$pdf" | tr -d ' ')
        [ "$sz" -gt "$MAX_BYTES" ] && { rm -f "$pdf"; continue; }
        [ "$(text_chars "$pdf")" -lt "$MIN_CHARS" ] && { rm -f "$pdf"; continue; }
        producer_bump "$(producer_of "$pdf")" || { rm -f "$pdf"; continue; }
        cp "$pdf" "$TARGET/g$(printf '%03d' "$i")_$(basename "$pdf")"
        kept=$((kept+1))
    done
    rm -rf "$TMP/$zip" "$TMP/ex_$i"
    i=$((i+1))
done

# manifest
manifest="$TARGET/.excise-manifest.tsv"
: > "$manifest"
sha() { command -v sha256sum >/dev/null && sha256sum "$1" | awk '{print $1}' || shasum -a 256 "$1" | awk '{print $1}'; }
for pdf in "$TARGET"/*.pdf; do
    [ -e "$pdf" ] || continue
    printf '%s\t%s\t%s\n' "$(sha "$pdf")" "$(basename "$pdf")" "$(wc -c < "$pdf" | tr -d ' ')" >> "$manifest"
done
echo "kept $kept text-bearing GovDocs1 PDFs in $TARGET (manifest: $manifest)"
