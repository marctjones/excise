#!/bin/bash
# Download a small curated corpus of public-domain US government PDFs for
# smoke-testing the renderer. These files are NOT checked into the repository;
# the corresponding SmokeCorpusTests will skip if this directory is empty.
#
# Source justification: all US government works are public domain per
# 17 USC § 105. URLs point at official .gov sites for stability and
# provenance. If a URL rots, the script logs it and continues — the
# smoke test runs against whatever made it in.

# --require-all: exit non-zero if ANY entry failed to download (#967).
#
# Two of the URLs below are upstream-refreshed "latest" pointers, not archival
# ones (irs p509, cdc covid-19 VIS). Fail-soft is right for a developer filling
# a gitignored corpus — you get nine of ten files and carry on. It is WRONG on
# CI, where this script now runs on every Linux job: a rotted URL would leave a
# silently smaller corpus, which shifts [Theory] row counts and makes the
# skip-budget gate (#854/#937) fire or stop firing for reasons unrelated to the
# change under test — surfacing as an unrelated-looking failure on someone
# else's PR. So CI passes --require-all and gets an honest red naming the URL.
# The durable fix is archival URLs (#844); this makes the drift loud meanwhile.
REQUIRE_ALL=0
for arg in "$@"; do
    case "$arg" in
        --require-all) REQUIRE_ALL=1 ;;
        -h|--help)
            echo "Usage: $0 [--require-all]"
            echo "  --require-all  exit 1 if any corpus entry failed to download (#967)"
            exit 0
            ;;
        *) echo "Unknown option: $arg" >&2; exit 2 ;;
    esac
done

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"
SMOKE_DIR="$PROJECT_ROOT/test-pdfs/smoke"

mkdir -p "$SMOKE_DIR"

echo "================================================="
echo "Smoke Corpus Downloader"
echo "================================================="
echo "Target: $SMOKE_DIR"
echo ""

# Each entry: "local-filename|source-url". Pipe separator tolerates URLs with
# query strings. Keep filenames descriptive — they show up in test output.
CORPUS=(
    # IRS — AcroForm-heavy tax forms. The f1040.pdf pattern is the IRS's
    # documented stable URL scheme.
    "irs-w4.pdf|https://www.irs.gov/pub/irs-pdf/fw4.pdf"
    "irs-w9.pdf|https://www.irs.gov/pub/irs-pdf/fw9.pdf"
    "irs-1040.pdf|https://www.irs.gov/pub/irs-pdf/f1040.pdf"
    "irs-1040-instructions.pdf|https://www.irs.gov/pub/irs-pdf/i1040gi.pdf"
    "irs-pub509-2026.pdf|https://www.irs.gov/pub/irs-pdf/p509.pdf"

    # US State Dept — passport application (fillable form).
    "state-ds11-passport.pdf|https://eforms.state.gov/Forms/ds11_pdf.PDF"
    "state-ds82-passport-renewal.pdf|https://eforms.state.gov/Forms/ds82_pdf.PDF"

    # CDC — public-health fact sheets (short, image+text mix).
    "cdc-vis-covid-19.pdf|https://www.cdc.gov/vaccines/hcp/current-vis/downloads/covid-19.pdf"

    # US Supreme Court — slip opinions (long-form body text, simple layout).
    "scotus-trump-v-anderson.pdf|https://www.supremecourt.gov/opinions/23pdf/23-719_19m2.pdf"
    "scotus-trump-v-us.pdf|https://www.supremecourt.gov/opinions/23pdf/23-939_e2pg.pdf"
)

ok=0
fail=0
failed_names=""
skip=0

for entry in "${CORPUS[@]}"; do
    name="${entry%%|*}"
    url="${entry#*|}"
    dest="$SMOKE_DIR/$name"

    if [ -f "$dest" ]; then
        size=$(stat -c%s "$dest" 2>/dev/null || stat -f%z "$dest")
        echo "✓ $name already downloaded (${size} bytes) — skipping"
        skip=$((skip + 1))
        continue
    fi

    echo "→ $name"
    echo "  $url"

    # -L follows redirects (most gov sites do), -f fails on 4xx/5xx, -sS shows
    # errors but not progress per-file (progress clutters output for 10 files).
    if curl -L -f -sS -o "$dest" --connect-timeout 15 --max-time 120 "$url"; then
        size=$(stat -c%s "$dest" 2>/dev/null || stat -f%z "$dest")
        if [ "$size" -lt 1000 ]; then
            echo "  ✗ downloaded file is suspiciously small ($size bytes) — removing"
            rm -f "$dest"
            fail=$((fail + 1))
            failed_names="$failed_names  $name ($url) — suspiciously small\n"
        else
            echo "  ✓ $size bytes"
            ok=$((ok + 1))
        fi
    else
        rc=$?
        echo "  ✗ curl exit $rc — URL may have rotted"
        rm -f "$dest"
        fail=$((fail + 1))
        failed_names="$failed_names  $name ($url) — curl exit $rc\n"
    fi
done

echo ""
echo "================================================="
echo "Downloaded: $ok   Skipped (already present): $skip   Failed: $fail"
echo "================================================="

if [ "$ok" -eq 0 ] && [ "$skip" -eq 0 ]; then
    echo "No PDFs in corpus. SmokeCorpusTests will skip."
    exit 1
fi

if [ "$fail" -gt 0 ] && [ "$REQUIRE_ALL" = "1" ]; then
    echo ""
    echo "FAIL: --require-all and $fail entr(ies) did not download (#967):"
    printf "%b" "$failed_names"
    echo "A partial corpus is not a smaller test run — it silently changes"
    echo "[Theory] row counts and makes the skip-budget gate fire for reasons"
    echo "unrelated to the change under test. Fix or re-point the URL above."
    exit 1
fi
