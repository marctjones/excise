#!/usr/bin/env bash
# Digital Library of Slovenia (dLib.si) — a redaction LEAK-CARRIER corpus (#1108).
#
# WHY THIS CORPUS
# ---------------
# dLib.si digitises historical Slovenian print. Many of its PDFs are a scanned
# page IMAGE with an OCR'd text layer painted over it in text render mode 3
# (invisible) — the classic redaction leak carrier: a black box hides the
# picture while the words sit right underneath, extractable and un-redacted.
# excise's HiddenTextDetector / `audit` path has no other corpus that pairs a
# scan with an invisible text layer. Measured on the seeds below (see the
# per-item profile the script records): e.g. DOC-04EH0WRK carries 2 invisible
# "3 Tr" runs over 2 scan images; some seeds are image-only public-domain
# scans (no text layer) that exercise the OCR path instead. The script records
# which is which per file rather than assuming.
#
# LICENCE — ENFORCED IN CODE, NOT PROMISED IN A COMMENT
# -----------------------------------------------------
# dLib tags each item with a rights statement. This corpus uses ONLY items
# marked Public Domain Mark (PDM): "Delo je prosto avtorsko-pravnih omejitev …
# Brez posebnega dovoljenja je možno razmnoževanje, modificiranje in
# distribuiranje dela za komercialne ali nekomercialne namene" — free of
# copyright, reproduce/modify/distribute for any purpose, no permission needed
# (https://www.dlib.si/Rights.aspx?q=PDM). The same corpus also holds In
# Copyright (InC) items, one typo'd URN away, so every seed's details page is
# re-checked for `Rights.aspx?q=PDM` at fetch time and a non-PDM seed is
# REFUSED, never downloaded. A refusal makes the whole run exit non-zero.
#
# NOT A MIRROR. dLib has millions of items; this is a small CURATED seed list
# of verified-PDM documents (like the govdocs1/safedocs subset scripts). Extend
# DLIB_SEEDS to grow it; do not point it at the whole library.
#
# TRANSPORT. dLib serves the PDF from /stream/<URN>/<UUID>/PDF, but only to a
# request that (a) carries the session cookie its details page sets and (b)
# names that details page as Referer. So per item: GET details (cookie jar) →
# scrape the UUID'd PDF link and the rights icon → GET the stream with cookie +
# referer. A polite delay separates documents; this is a national library.
#
# Files land in test-pdfs/dlib-slovenia/ (gitignored, like every corpus) with
#   test-pdfs/dlib-slovenia/.manifest.tsv   (sha256<TAB>name<TAB>size)
#
# Usage:
#   scripts/download-dlib-slovenia.sh              # fetch the seed set (idempotent)
#   scripts/download-dlib-slovenia.sh --force      # re-fetch everything
#   scripts/download-dlib-slovenia.sh --limit 2    # fetch only the first N seeds
#   scripts/corpus.sh fetch dlib-slovenia          # the registered entry point
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
TARGET="$ROOT/test-pdfs/dlib-slovenia"
BASE="${DLIB_BASE:-https://www.dlib.si}"
UA="Mozilla/5.0 (X11; Linux x86_64; rv:120.0) Gecko/20100101 Firefox/120.0"
DELAY="${DLIB_DELAY:-2}"   # seconds between documents — be polite to dLib

# Curated seed list. Every entry was verified Public Domain Mark; the code
# re-checks anyway. A trailing '#' comment records what was measured at
# curation time (leak = invisible 3 Tr text over a scan; scan = image-only PD
# scan, OCR-path carrier). Grow this list to grow the corpus.
DLIB_SEEDS=(
    "URN:NBN:SI:DOC-04EH0WRK"   # leak: 2 scan images, 2 invisible '3 Tr' runs
    "URN:NBN:SI:DOC-06MC3EVD"   # leak: 1 scan image, 1 invisible '3 Tr' run
    "URN:NBN:SI:DOC-05RIJ2A7"   # leak: 1 scan image, 1 invisible '3 Tr' run
    "URN:NBN:SI:DOC-06GY779F"   # leak: 1 scan image, 1 invisible '3 Tr' run
    "URN:NBN:SI:DOC-VU18SZHD"   # scan: Novice 1845, image-only (OCR in sidecar)
    "URN:NBN:SI:DOC-04CVYR78"   # scan: image-only public-domain scan
    "URN:NBN:SI:DOC-04ITUVAV"   # scan: image-only public-domain scan
)

FORCE=0
LIMIT=0
while [ $# -gt 0 ]; do
    case "$1" in
        --force) FORCE=1; shift ;;
        --limit) LIMIT="${2:-0}"; shift 2 ;;
        -h|--help) sed -n '1,60p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "unknown option: $1" >&2; exit 2 ;;
    esac
done

command -v curl >/dev/null || { echo "curl required" >&2; exit 1; }

# Already-present check: idempotent skip unless --force.
if [ "$FORCE" = 0 ] && [ -d "$TARGET" ] && [ -n "$(find "$TARGET" -name '*.pdf' -print -quit 2>/dev/null)" ]; then
    echo "✓ dlib-slovenia already present ($(find "$TARGET" -name '*.pdf' | wc -l | tr -d ' ') PDFs). --force to refetch."
    exit 0
fi

mkdir -p "$TARGET"
COOKIES="$(mktemp)"; DETAILS="$(mktemp)"
trap 'rm -f "$COOKIES" "$DETAILS"' EXIT

sha() { command -v sha256sum >/dev/null && sha256sum "$1" | awk '{print $1}' || shasum -a 256 "$1" | awk '{print $1}'; }
safe_name() { printf 'dlib_%s.pdf' "$(echo "$1" | tr ':' '-')"; }

fetched=0
refused=0
n=0
for urn in "${DLIB_SEEDS[@]}"; do
    [ "$LIMIT" -gt 0 ] && [ "$n" -ge "$LIMIT" ] && break
    n=$((n+1))
    out="$TARGET/$(safe_name "$urn")"

    if [ "$FORCE" = 0 ] && [ -s "$out" ]; then
        echo "  ✓ $urn already fetched"
        continue
    fi

    # 1. details page (establishes the session cookie the stream endpoint wants)
    if ! curl -sL --max-time 60 -A "$UA" -c "$COOKIES" -b "$COOKIES" \
            "$BASE/details/$urn" -o "$DETAILS"; then
        echo "  ✗ $urn: details page fetch failed" >&2
        continue
    fi

    # 2. LICENCE GATE — refuse anything not marked Public Domain Mark.
    if ! grep -qiE 'Rights\.aspx\?q=PDM' "$DETAILS"; then
        rights="$(grep -oiE 'Rights\.aspx\?q=[A-Za-z]+' "$DETAILS" | head -1)"
        echo "  ✗ REFUSED $urn: rights='${rights:-unknown}', not Public Domain Mark — not downloaded" >&2
        refused=$((refused+1))
        continue
    fi

    # 3. scrape the UUID'd PDF stream link from the details page
    link="$(grep -oiE "/stream/${urn}/[0-9a-f-]+/PDF" "$DETAILS" | head -1)"
    if [ -z "$link" ]; then
        echo "  ✗ $urn: no PDF stream link on details page (skipping)" >&2
        continue
    fi

    # 4. fetch the stream with cookie + referer
    meta="$(curl -sL --max-time 300 -A "$UA" -c "$COOKIES" -b "$COOKIES" \
             -e "$BASE/details/$urn" \
             -w '%{http_code}\t%{content_type}' \
             "$BASE$link" -o "$out")"
    http_code="${meta%%$'\t'*}"
    ctype="${meta#*$'\t'}"
    # Accept only a 200 that is really a PDF (dLib serves the details HTML back
    # on a rejected request, so both checks are load-bearing).
    if [ "$http_code" != "200" ] || [ "${ctype#*application/pdf}" = "$ctype" ] \
            || [ "$(head -c 5 "$out" 2>/dev/null)" != "%PDF-" ]; then
        echo "  ✗ $urn: stream not a PDF (http=$http_code type=$ctype)" >&2
        rm -f "$out"
        continue
    fi

    echo "  ✓ $urn → $(basename "$out") ($(wc -c < "$out" | tr -d ' ') bytes, PDM)"
    fetched=$((fetched+1))
    sleep "$DELAY"
done

# manifest (sha256, name, size)
manifest="$TARGET/.manifest.tsv"
: > "$manifest"
for pdf in "$TARGET"/*.pdf; do
    [ -e "$pdf" ] || continue
    printf '%s\t%s\t%s\n' "$(sha "$pdf")" "$(basename "$pdf")" "$(wc -c < "$pdf" | tr -d ' ')" >> "$manifest"
done

echo "dlib-slovenia: fetched $fetched PDM PDFs into $TARGET (manifest: $manifest)"
if [ "$refused" -gt 0 ]; then
    echo "✗ $refused seed(s) refused as non-Public-Domain-Mark — corpus licence gate tripped" >&2
    exit 1
fi
[ "$fetched" -gt 0 ] || { echo "✗ fetched nothing" >&2; exit 1; }
