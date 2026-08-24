#!/usr/bin/env bash
# download-itext-corpus.sh — iText7's encrypted-PDF regression fixtures.
#
# WHY THIS SUBSET AND NOT ALL 4,000
# ---------------------------------
# iText7 ships just under 4,000 classified test PDFs. We take the ~29 under
# kernel/.../crypto, because encrypted redaction is where excise currently has
# open defects and thin coverage: #1048 (CryptographicException while
# redacting) and #1095 (the gate on redacted encrypted output) run against
# NINE fixtures from tests/corpus-passwords.tsv. This roughly quadruples that.
#
# The rest of the iText corpus is not obviously better than what we already
# hold; fetch it when something specific needs it (see RC16).
#
# LICENCE: iText7 is AGPL. These are downloaded as local test fixtures,
# gitignored, never redistributed and never linked — the same posture already
# recorded for the poppler fixtures and for mutool/Ghostscript as oracles.
#
# Output:
#   test-pdfs/itext/<name>.pdf
#   test-pdfs/itext/.manifest.tsv    sha256 <TAB> name <TAB> size
#   test-pdfs/itext/.passwords.tsv   name <TAB> user-password   (VERIFIED, see below)

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(dirname "$SCRIPT_DIR")"
DEST="$ROOT/test-pdfs/itext"
REPO="itext/itext7"
BRANCH="develop"
BASE="kernel/src/test/resources/com/itextpdf/kernel/crypto"
DIRS=(PdfEncryptingTest PdfEncryptionManuallyPortedTest EncryptionInApprovedModeTest UnencryptedWrapperTest)

GREEN=$'\033[32m'; YELLOW=$'\033[33m'; DIM=$'\033[2m'; RESET=$'\033[0m'

command -v gh >/dev/null 2>&1 || { echo "needs the gh CLI" >&2; exit 1; }
mkdir -p "$DEST"

echo "==> fetching iText encrypted fixtures from $REPO@$BRANCH"
fetched=0
: > "$DEST/.manifest.tsv.tmp"

for d in "${DIRS[@]}"; do
    listing="$(gh api "repos/$REPO/contents/$BASE/$d?ref=$BRANCH" 2>/dev/null || true)"
    [ -n "$listing" ] || { echo "  ${YELLOW}skip${RESET} $d (not found)"; continue; }

    # Prefix the directory onto the filename: several of these directories use
    # the same fixture names, and a flat destination would silently overwrite.
    while IFS=$'\t' read -r name url; do
        [ -n "$name" ] || continue
        local_name="${d}__${name}"
        if [ -f "$DEST/$local_name" ] && [ "${1:-}" != "--force" ]; then
            :
        else
            curl -sSL "$url" -o "$DEST/$local_name"
        fi
        sha=$(shasum -a 256 "$DEST/$local_name" | cut -d' ' -f1)
        size=$(wc -c < "$DEST/$local_name" | tr -d ' ')
        printf '%s\t%s\t%s\n' "$sha" "$local_name" "$size" >> "$DEST/.manifest.tsv.tmp"
        fetched=$((fetched+1))
    done < <(echo "$listing" | python3 -c "
import json,sys
for e in json.load(sys.stdin):
    if e['name'].lower().endswith('.pdf'):
        print(e['name'] + '\t' + e['download_url'])
")
done

sort -k2 "$DEST/.manifest.tsv.tmp" > "$DEST/.manifest.tsv"
rm -f "$DEST/.manifest.tsv.tmp"
echo "${GREEN}✓${RESET} $fetched PDF(s) → $DEST"

# ── passwords ──────────────────────────────────────────────────────────────
# Verified with qpdf, not guessed and not verified with excise: a fixture list
# excise vouches for is a fixture list that inherits excise's bugs.
#
# ⚠️ qpdf accepts EITHER the user or the owner password, so "qpdf opened it"
# does not mean "this is the user password" — and excise is user-password-only
# (#324). Each candidate is therefore checked with --show-encryption, and only
# a confirmed USER password is recorded.
if ! command -v qpdf >/dev/null 2>&1; then
    echo "${YELLOW}⚠${RESET} qpdf not found — skipping password discovery."
    echo "  Install qpdf and re-run to produce .passwords.tsv."
    exit 0
fi

echo "==> discovering user passwords with qpdf"
: > "$DEST/.passwords.tsv"
encrypted=0; solved=0
for f in "$DEST"/*.pdf; do
    [ -f "$f" ] || continue
    name="$(basename "$f")"
    qpdf --is-encrypted "$f" >/dev/null 2>&1 || continue
    encrypted=$((encrypted+1))
    for pw in "" user owner password 1234 test; do
        if qpdf --password="$pw" --show-encryption "$f" >/dev/null 2>&1; then
            # "user password" in --show-encryption's output names which one we
            # supplied; anything else (owner) is deliberately NOT recorded.
            if qpdf --password="$pw" --show-encryption "$f" 2>/dev/null | grep -qi "supplied password is user password"; then
                printf '%s\t%s\n' "$name" "$pw" >> "$DEST/.passwords.tsv"
                solved=$((solved+1))
                break
            fi
        fi
    done
done

echo "${GREEN}✓${RESET} $solved of $encrypted encrypted fixture(s) have a confirmed USER password"
echo "${DIM}  → $DEST/.passwords.tsv${RESET}"
echo
echo "To put these behind the #1095 gate, copy the confirmed rows into the"
echo "tracked manifest by hand — that file is curated, and a download script"
echo "should not mutate it:"
echo "  ${DIM}tests/corpus-passwords.tsv${RESET}"
