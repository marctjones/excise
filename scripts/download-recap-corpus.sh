#!/usr/bin/env bash
# RECAP bad-redaction sweep — tier D (#1603 source 1, bench #1590).
#
# WHY RECAP. Bland et al. (PETS 2023) swept it and reported 6,541 non-excised
# redacted names across 710 US court documents. It is a free mirror of PACER,
# so this needs no PACER credentials and incurs NO PACER FEES — the constraint
# in #1591. x-ray was built by Free Law Project to sweep exactly this archive.
#
# WHAT THIS KEEPS. Not "court documents" — documents where SOMEBODY ELSE'S
# REDACTION LEAKED. Every fetched PDF is swept with the vendored x-ray and
# DELETED unless x-ray reports a bad redaction. A clean court filing is not a
# tier-D document; it is a file we made somebody serve us for nothing.
#
# ⚠️ WHAT IT NEVER WRITES (#1602). The manifest records path, sha256, page and
# leak CLASS. It has no column for recovered text and this script never prints
# one: x-ray's `text` is measured (length, character classes) and discarded.
# These are real court records about real people, and the bench's question —
# "did a mark leak" — never needs the answer.
#
# ⚠️ IT IDENTIFIES ITSELF. The User-Agent names the project and a contact URL,
# the rate limit is deliberately slower than necessary, and a 403 is a STOP,
# not a thing to route around by pretending to be a browser. If a host refuses
# a self-identifying client, that is a decision for a human to take up with the
# host — see the `blocked` rows in tests/unredaction-bench/manifest.tsv.
#
# Usage:
#   scripts/download-recap-corpus.sh --query "motion to seal"   sweep a search
#   scripts/download-recap-corpus.sh --urls FILE                sweep a URL list
#   scripts/download-recap-corpus.sh --status                   what is here
#   scripts/download-recap-corpus.sh --sweep-only               re-sweep, no fetch
#
# A free CourtListener API token (https://www.courtlistener.com/help/api/rest/)
# in COURTLISTENER_TOKEN enables --query. --urls needs no token: RECAP storage
# URLs are public, and that is the mode the Manafort rows already use.

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(dirname "$SCRIPT_DIR")"
DEST="$ROOT/test-pdfs/recap"
MANIFEST="$DEST/.excise-manifest.tsv"
XRAY_PY="$ROOT/tools/vendor/xray-venv/bin/python"
[ -x "$XRAY_PY" ] || XRAY_PY="$(dirname "$ROOT")/../tools/vendor/xray-venv/bin/python"

UA="excise-unredaction-bench/1.0 (+https://github.com/marctjones/excise; research use)"
DELAY="${RECAP_DELAY:-3}"
MAX_DOCS="${RECAP_MAX_DOCS:-200}"
MAX_MB="${RECAP_MAX_MB:-25}"

GREEN=$'\033[32m'; RED=$'\033[31m'; YELLOW=$'\033[33m'; DIM=$'\033[2m'; BOLD=$'\033[1m'; RESET=$'\033[0m'

die() { echo "${RED}$*${RESET}" >&2; exit 1; }

sha_of() {
    if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | cut -d' ' -f1
    else shasum -a 256 "$1" | cut -d' ' -f1; fi
}

find_xray() {
    for c in "$ROOT/tools/vendor/xray-venv/bin/python" \
             "$(git -C "$ROOT" rev-parse --path-format=absolute --git-common-dir 2>/dev/null | xargs -I{} dirname {} 2>/dev/null)/tools/vendor/xray-venv/bin/python"; do
        [ -x "$c" ] && "$c" -c "import xray" >/dev/null 2>&1 && { echo "$c"; return 0; }
    done
    return 1
}

# ---------------------------------------------------------------------------
# The sweep. Prints one of: CLEAN | LEAK <pages> <marks> <chars> <classes>
#
# `chars` is a COUNT and `classes` is a character-class summary (alpha/digit/
# punct). Neither can reconstruct a value, which is the point — see #1602.
# ---------------------------------------------------------------------------
sweep() {
    local pdf="$1" py="$2"
    "$py" - "$pdf" <<'PY' 2>/dev/null
import sys, xray
try:
    r = xray.inspect(sys.argv[1])
except Exception:
    print("ERROR"); raise SystemExit(0)
if not r:
    print("CLEAN"); raise SystemExit(0)
marks = chars = 0
alpha = digit = punct = 0
for hits in r.values():
    for h in hits:
        marks += 1
        t = h["text"]
        chars += len(t)
        alpha += sum(c.isalpha() for c in t)
        digit += sum(c.isdigit() for c in t)
        punct += sum((not c.isalnum()) and (not c.isspace()) for c in t)
# COUNTS ONLY. The text itself is never printed, logged or returned.
print(f"LEAK {len(r)} {marks} {chars} a{alpha}/d{digit}/p{punct}")
PY
}

record() {
    local f="$1" url="$2" verdict="$3"
    local sha; sha="$(sha_of "$f")"
    printf '%s\t%s\t%s\t%s\n' "$sha" "$(basename "$f")" "$url" "$verdict" >> "$MANIFEST"
}

fetch_one() {
    local url="$1" py="$2" name tmp
    name="$(printf '%s' "$url" | sha_of_stdin)"
    name="recap_${name:0:16}.pdf"
    local f="$DEST/$name"

    if [ -f "$f" ]; then echo "${DIM}· $name already present${RESET}"; return 0; fi

    tmp="$f.part"
    if ! curl -fsSL --max-time 120 --retry 2 --retry-delay 3 \
              --max-filesize $((MAX_MB * 1024 * 1024)) \
              -A "$UA" -o "$tmp" "$url"; then
        echo "  ${YELLOW}fetch refused or failed${RESET} ${DIM}$url${RESET}"
        rm -f "$tmp"; sleep "$DELAY"; return 1
    fi

    # The age-gate lesson (#1603): a 200 with a body is not a PDF. An
    # interstitial, a login wall or a consent page all satisfy curl -f.
    if [ "$(head -c 5 "$tmp")" != "%PDF-" ]; then
        echo "  ${YELLOW}not a PDF${RESET} — the server returned something else"
        rm -f "$tmp"; sleep "$DELAY"; return 1
    fi

    mv "$tmp" "$f"
    local verdict; verdict="$(sweep "$f" "$py")"
    case "$verdict" in
        LEAK*)
            echo "  ${GREEN}✓ LEAK${RESET} $name  ${DIM}${verdict#LEAK }${RESET}"
            record "$f" "$url" "$verdict"
            ;;
        *)
            # A clean filing is not a tier-D document. Keeping it would grow the
            # corpus without growing what it can measure.
            rm -f "$f"
            echo "  ${DIM}· clean, discarded${RESET}"
            ;;
    esac
    sleep "$DELAY"
}

sha_of_stdin() {
    if command -v sha256sum >/dev/null 2>&1; then sha256sum | cut -d' ' -f1
    else shasum -a 256 | cut -d' ' -f1; fi
}

cmd_status() {
    if [ ! -f "$MANIFEST" ]; then echo "no RECAP corpus yet — $DEST"; return 0; fi
    local n; n=$(grep -c . "$MANIFEST" 2>/dev/null || echo 0)
    echo "${BOLD}$n leaking document(s)${RESET} in $DEST"
    awk -F'\t' '{print "  "$2"  "$4}' "$MANIFEST"
}

cmd_sweep_only() {
    local py; py="$(find_xray)" || die "x-ray not installed — run scripts/download-xray.sh"
    : > "$MANIFEST"
    local kept=0 dropped=0
    for f in "$DEST"/*.pdf; do
        [ -e "$f" ] || continue
        local v; v="$(sweep "$f" "$py")"
        case "$v" in
            LEAK*) record "$f" "-" "$v"; kept=$((kept+1)); echo "${GREEN}✓${RESET} $(basename "$f") ${DIM}${v#LEAK }${RESET}" ;;
            *)     dropped=$((dropped+1)) ;;
        esac
    done
    echo "$kept leaking, $dropped clean"
}

cmd_urls() {
    local list="$1"
    [ -f "$list" ] || die "no such file: $list"
    local py; py="$(find_xray)" || die "x-ray not installed — run scripts/download-xray.sh"
    mkdir -p "$DEST"; touch "$MANIFEST"
    local n=0
    while read -r url; do
        case "$url" in ''|\#*) continue ;; esac
        n=$((n+1)); [ "$n" -gt "$MAX_DOCS" ] && { echo "${YELLOW}stopping at RECAP_MAX_DOCS=$MAX_DOCS${RESET}"; break; }
        echo "↓ $url"
        fetch_one "$url" "$py"
    done < "$list"
    cmd_status
}

cmd_query() {
    local q="$1"
    [ -n "${COURTLISTENER_TOKEN:-}" ] || die \
"--query needs a free CourtListener API token.

  1. Sign up:  https://www.courtlistener.com/sign-in/
  2. Token:    https://www.courtlistener.com/profile/api-token/
  3. export COURTLISTENER_TOKEN=...

--urls FILE needs no token: RECAP storage URLs are public, and that is what the
Manafort rows in tests/unredaction-bench/manifest.tsv already use."

    local py; py="$(find_xray)" || die "x-ray not installed — run scripts/download-xray.sh"
    mkdir -p "$DEST"; touch "$MANIFEST"

    echo "${BOLD}searching RECAP${RESET} ${DIM}$q${RESET}"
    local api="https://www.courtlistener.com/api/rest/v4/search/"
    local page; page="$(curl -fsSL --max-time 60 -A "$UA" \
        -H "Authorization: Token $COURTLISTENER_TOKEN" \
        --get --data-urlencode "q=$q" --data-urlencode "type=r" \
        "$api")" || die "search failed — check the token and the rate limit"

    # filepath_local is the RECAP storage path; storage.courtlistener.com serves
    # it without credentials.
    local urls; urls="$(printf '%s' "$page" | python3 -c '
import json,sys
d=json.load(sys.stdin)
seen=set()
for r in d.get("results", []):
    for doc in r.get("recap_documents", []) or [r]:
        p = doc.get("filepath_local")
        if p and p not in seen:
            seen.add(p)
            print("https://storage.courtlistener.com/" + p.lstrip("/"))
')"
    [ -n "$urls" ] || { echo "${YELLOW}no documents with files in that result page${RESET}"; return 0; }

    local tmp; tmp="$(mktemp)"; printf '%s\n' "$urls" > "$tmp"
    echo "${DIM}$(grep -c . "$tmp") candidate document(s)${RESET}"
    cmd_urls "$tmp"
    rm -f "$tmp"
}

case "${1:---status}" in
    --status)     cmd_status ;;
    --sweep-only) cmd_sweep_only ;;
    --urls)       shift; cmd_urls "${1:?--urls needs a file}" ;;
    --query)      shift; cmd_query "${1:?--query needs a search string}" ;;
    -h|--help)    sed -n '2,32p' "$0" | sed 's/^# \{0,1\}//' ;;
    *)            die "unknown option: $1 (try --help)" ;;
esac
