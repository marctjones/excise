#!/usr/bin/env bash
# download-unredaction-bench.sh — fetch the unredaction bench's real-world
# documents (#1591) into the gitignored test-pdfs/unredaction-bench/.
#
# WHAT MAKES THIS DIFFERENT FROM THE OTHER CORPUS SCRIPTS
# -------------------------------------------------------
# Every other corpus here is a rendering or conformance suite: fetching the
# wrong file wastes a download. This corpus is redacted documents whose hidden
# text a court or an agency tried to withhold, so the script is deliberately
# NARROWER than a downloader has to be:
#
#   - it downloads ONLY rows the manifest marks `vetted`, and refuses to be
#     talked into anything else. There is no --all and no --force;
#   - it verifies sha256 and DELETES a mismatch rather than keeping it, because
#     the wrong bytes at a vetted URL means the document changed and the vetting
#     no longer describes what is on disk;
#   - it uses no credentials and never touches a paid PACER endpoint;
#   - it writes no recovered or published VALUES anywhere. Ground truth for
#     scoring is a gitignored local file you build by hand (see --ground-truth).
#
# It is resumable (an existing file with the right hash is left alone) and
# polite (one request at a time, a delay between them, and it identifies itself
# in the User-Agent so an operator can see who is asking and why).
#
# NEVER runs as part of t0/t1. It is not a gate and it is not on the merge path.
#
#   scripts/download-unredaction-bench.sh            fetch every vetted row
#   scripts/download-unredaction-bench.sh --list     what the manifest holds, runs nothing
#   scripts/download-unredaction-bench.sh --check    re-verify what is on disk
#   scripts/download-unredaction-bench.sh --hash URL print the sha256 of a candidate
#   scripts/download-unredaction-bench.sh --ground-truth   explain the local file

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(dirname "$SCRIPT_DIR")"
MANIFEST="$ROOT/tests/unredaction-bench/manifest.tsv"
DEST="$ROOT/test-pdfs/unredaction-bench"
TRUTH="$DEST/ground-truth.local.tsv"
UA="excise-unredaction-bench/1.0 (+https://github.com/marctjones/excise; research use)"
DELAY="${UNREDACTION_BENCH_DELAY:-2}"

GREEN=$'\033[32m'; RED=$'\033[31m'; YELLOW=$'\033[33m'; DIM=$'\033[2m'; BOLD=$'\033[1m'; RESET=$'\033[0m'

[ -f "$MANIFEST" ] || { echo "${RED}missing manifest: $MANIFEST${RESET}" >&2; exit 1; }

rows() { awk -F'\t' '!/^#/ && NF >= 9' "$MANIFEST"; }

sha_of() {
    if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | cut -d' ' -f1
    else shasum -a 256 "$1" | cut -d' ' -f1; fi
}

cmd_list() {
    printf "%s%-34s %-5s %-9s %-8s %s%s\n" "$BOLD" "ID" "TIER" "STATUS" "ON DISK" "SOURCE" "$RESET"
    local id tier status url sha source basis truth vet present
    while IFS=$'\t' read -r id tier status url sha source basis truth vet; do
        present="—"
        [ -f "$DEST/$id.pdf" ] && present="yes"
        case "$status" in
            vetted)   printf "%-34s %-5s ${GREEN}%-9s${RESET} %-8s %s\n" "$id" "$tier" "$status" "$present" "$source" ;;
            excluded) printf "%-34s %-5s ${RED}%-9s${RESET} %-8s %s\n"   "$id" "$tier" "$status" "$present" "$source" ;;
            *)        printf "%-34s %-5s ${YELLOW}%-9s${RESET} %-8s %s\n" "$id" "$tier" "$status" "$present" "$source" ;;
        esac
        printf "  ${DIM}%s${RESET}\n" "$vet"
    done < <(rows)
}

cmd_ground_truth() {
    cat <<TXT
Ground truth for tier B lives ONLY here, and only on your machine:

  $TRUTH

It is gitignored with the rest of test-pdfs/. Build it by hand from the
truth_url column of the manifest — one tab-separated row per known value:

  <id>  <page>  <mark-index>  <the published value>

Nothing writes it for you on purpose. Transcribing a value is the moment to
re-read the vetting line for that row and decide again whether this document
belongs in the bench at all.

The bench skips tier-B scoring when the file is absent and says so as its skip
reason (#1172). It never writes the file, never echoes a value it read from it,
and never copies one into a log that is not under logs/.
TXT
}

cmd_hash() {
    local url="$1" tmp
    tmp="$(mktemp)"
    if curl -fsSL --max-time 120 -A "$UA" -o "$tmp" "$url"; then
        echo "$(sha_of "$tmp")  $url"
    else
        echo "${RED}fetch failed: $url${RESET}" >&2; rm -f "$tmp"; exit 1
    fi
    rm -f "$tmp"
}

cmd_check() {
    local id tier status url sha source basis truth vet problems=0 checked=0
    while IFS=$'\t' read -r id tier status url sha source basis truth vet; do
        [ "$status" = "vetted" ] || continue
        local f="$DEST/$id.pdf"
        [ -f "$f" ] || continue
        checked=$((checked+1))
        local got; got="$(sha_of "$f")"
        if [ "$got" = "$sha" ]; then
            echo "${GREEN}✓${RESET} $id"
        else
            echo "${RED}✗ $id: sha256 mismatch${RESET}"
            echo "    expected $sha"
            echo "    got      $got"
            problems=$((problems+1))
        fi
    done < <(rows)
    echo "$checked file(s) checked, $problems problem(s)"
    [ "$problems" -eq 0 ] || exit 1
}

cmd_fetch() {
    mkdir -p "$DEST"
    local id tier status url sha source basis truth vet
    local got=0 skipped=0 failed=0 refused=0
    while IFS=$'\t' read -r id tier status url sha source basis truth vet; do
        if [ "$status" != "vetted" ]; then
            refused=$((refused+1))
            continue
        fi
        local f="$DEST/$id.pdf"
        if [ -f "$f" ] && [ "$(sha_of "$f")" = "$sha" ]; then
            echo "${DIM}· $id already present${RESET}"
            skipped=$((skipped+1))
            continue
        fi

        echo "↓ $id"
        echo "  ${DIM}$url${RESET}"
        local tmp="$f.part"
        if ! curl -fsSL --max-time 300 --retry 2 --retry-delay 3 -A "$UA" -o "$tmp" "$url"; then
            echo "  ${RED}fetch failed${RESET}"
            rm -f "$tmp"; failed=$((failed+1)); sleep "$DELAY"; continue
        fi

        # ⚠️ Is it a PDF AT ALL? A server that answers with an interstitial
        # rather than an error returns 200 and a body, and curl -f is happy.
        # Measured on justice.gov's Epstein files: twelve requests returned
        # twelve byte-identical 54 KB copies of an AGE-VERIFICATION page, each
        # saved as <id>.pdf. The sha check below catches that only because a
        # hash was already recorded — for a row whose hash is being established
        # for the first time, an interstitial would be hashed AS the document
        # and pinned. Check the magic bytes before anything else.
        if [ "$(head -c 5 "$tmp")" != "%PDF-" ]; then
            echo "  ${RED}not a PDF${RESET} — the server returned something else"
            echo "  ${DIM}first bytes: $(head -c 40 "$tmp" | tr -d '\0' | tr '\n' ' ')${RESET}"
            echo "  ${DIM}an age gate, a login wall or a consent page answers 200 with a body${RESET}"
            rm -f "$tmp"; failed=$((failed+1)); sleep "$DELAY"; continue
        fi

        local actual; actual="$(sha_of "$tmp")"
        if [ "$actual" != "$sha" ]; then
            # The document at a vetted URL changed. The vetting record describes
            # bytes we no longer have, so keeping the new ones would quietly put
            # an unvetted document in the bench.
            echo "  ${RED}✗ sha256 mismatch — discarding${RESET}"
            echo "    manifest $sha"
            echo "    fetched  $actual"
            echo "    ${YELLOW}re-vet the document, then update the manifest row${RESET}"
            rm -f "$tmp"; failed=$((failed+1)); sleep "$DELAY"; continue
        fi

        mv "$tmp" "$f"
        echo "  ${GREEN}✓ verified${RESET}"
        got=$((got+1))
        sleep "$DELAY"
    done < <(rows)

    echo ""
    echo "${BOLD}$got fetched, $skipped already present, $failed failed, $refused not vetted (not downloaded)${RESET}"
    if [ ! -f "$TRUTH" ]; then
        echo "${YELLOW}no local ground truth at $TRUTH — tier-B scoring will skip.${RESET}"
        echo "${DIM}  scripts/download-unredaction-bench.sh --ground-truth${RESET}"
    fi
    [ "$failed" -eq 0 ] || exit 1
}

case "${1:---fetch}" in
    --list)         cmd_list ;;
    --check)        cmd_check ;;
    --ground-truth) cmd_ground_truth ;;
    --hash)         [ $# -ge 2 ] || { echo "usage: $0 --hash <url>" >&2; exit 2; }; cmd_hash "$2" ;;
    --fetch|"")     cmd_fetch ;;
    -h|--help)      sed -n '2,32p' "$0" | sed 's/^# \{0,1\}//' ;;
    *)              echo "${RED}unknown option: $1${RESET}" >&2; exit 2 ;;
esac
