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
# Each entry: "local-filename|source-url|sha256".
#
# URLS ARE ARCHIVAL WHERE ONE EXISTS (#844). The IRS entries previously used
# /pub/irs-pdf/<form>.pdf, which is the CURRENT-year pointer and silently
# changes when a form is reissued; they now use /pub/irs-prior/<form>--<year>.pdf,
# which is year-pinned. Each archival URL was verified BYTE-IDENTICAL to the
# fixture already in test-pdfs/smoke before being swapped in, so no extraction,
# copy-whitespace, perf or redaction-collateral baseline measured against these
# files moves. (Note fw9--2024 and f1040--2025: the pinned edition is whatever
# the corpus was actually built on, not the newest.)
#
# CDC has no demonstrably archival URL — /vis-statements/ and /current-vis/
# serve identical bytes today but both are "current" pointers. That is exactly
# why every entry also carries a SHA-256: content drift is caught even when the
# URL keeps working, which URL pinning alone cannot do. State Dept and SCOTUS
# URLs are stable-by-construction (form code / docket number).
CORPUS=(
    # IRS — AcroForm-heavy tax forms, pinned to the prior-year archive.
    "irs-w4.pdf|https://www.irs.gov/pub/irs-prior/fw4--2026.pdf|92444d8856ce55d9e25dca8b6d1420634fc68b11e1ab1f760916ea29ddd312b2"
    "irs-w9.pdf|https://www.irs.gov/pub/irs-prior/fw9--2024.pdf|2d420cbb4123dcf1fb82595b2359cfbb5d81f00b9df9d359fcc7af361d093f53"
    "irs-1040.pdf|https://www.irs.gov/pub/irs-prior/f1040--2025.pdf|3d31c226df0d189ced80e039d01cf0f8820c1019681a0f0ca6264de277b7e982"
    "irs-1040-instructions.pdf|https://www.irs.gov/pub/irs-prior/i1040gi--2025.pdf|482e9c487c608f1bbeaceef35bc3c0933e8b35443cfff447e4279d590468364a"
    "irs-pub509-2026.pdf|https://www.irs.gov/pub/irs-prior/p509--2026.pdf|d7d7e3f816bb3e08782d4628efeee1505ae9740fa0a4633aa8746f02f3f4a0cb"

    # US State Dept — passport application (fillable form).
    "state-ds11-passport.pdf|https://eforms.state.gov/Forms/ds11_pdf.PDF|6b30860f0b54cba9df1a54d4eb007dc93a6c785b5253516604530b1c1898e2f6"
    "state-ds82-passport-renewal.pdf|https://eforms.state.gov/Forms/ds82_pdf.PDF|ac3331a0c71a8c9280c82271618ceb3ba950a085d760812d3f4f5229f62849bb"

    # CDC — public-health fact sheet (short, image+text mix). No archival URL;
    # the checksum is the pin.
    "cdc-vis-covid-19.pdf|https://www.cdc.gov/vaccines/hcp/current-vis/downloads/covid-19.pdf|908952ae744418c227903ec556ee1ca1893c24ddb482991222259fb8dfaf667b"

    # US Supreme Court — slip opinions (long-form body text, simple layout).
    "scotus-trump-v-anderson.pdf|https://www.supremecourt.gov/opinions/23pdf/23-719_19m2.pdf|f3015ab4890996a0cb1f1cb3e943cf27d2d6a58ced20f8dbdc3d06e79c15d07a"
    "scotus-trump-v-us.pdf|https://www.supremecourt.gov/opinions/23pdf/23-939_e2pg.pdf|4cbb9bd0c0f023cd0273826e9481f16edd2ca942e5b378123d2478b66ef31746"
)

ok=0
fail=0
drift=0
failed_names=""
drifted_names=""
skip=0

for entry in "${CORPUS[@]}"; do
    name="${entry%%|*}"
    rest="${entry#*|}"
    url="${rest%%|*}"
    want_sha="${rest#*|}"
    [ "$want_sha" = "$url" ] && want_sha=""   # entry carried no checksum
    dest="$SMOKE_DIR/$name"

    if [ -f "$dest" ]; then
        size=$(stat -c%s "$dest" 2>/dev/null || stat -f%z "$dest")
        if [ -n "$want_sha" ] && [ "$(shasum -a 256 "$dest" | cut -d' ' -f1)" != "$want_sha" ]; then
            # An already-present file is verified too: otherwise a corpus that
            # drifted before the pin existed, or a locally-edited fixture,
            # stays wrong forever because the script never re-downloads it.
            echo "✗ $name is present but does NOT match its pinned sha256"
            echo "      expected $want_sha"
            echo "      actual   $(shasum -a 256 "$dest" | cut -d' ' -f1)"
            drift=$((drift + 1))
            drifted_names="$drifted_names  $name (already on disk)\n"
            continue
        fi
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
        elif [ -n "$want_sha" ] && [ "$(shasum -a 256 "$dest" | cut -d' ' -f1)" != "$want_sha" ]; then
            # Content drift with a working URL (#844). Reported as its own
            # failure kind, NOT as a missing file: download-federal-corpus.sh
            # deletes on mismatch and the downstream gate then says
            # "missing federal/..." — which sends the reader hunting for a
            # network problem that does not exist. Keep the file so it can be
            # diffed, and say exactly what changed.
            echo "  ✗ CONTENT DRIFTED — the URL still works but the bytes changed"
            echo "      expected sha256 $want_sha"
            echo "      actual   sha256 $(shasum -a 256 "$dest" | cut -d' ' -f1)"
            echo "      Upstream reissued this document. Diff it, decide whether the"
            echo "      new edition is acceptable, then update the pin AND re-baseline"
            echo "      anything measured against it (extraction parity, copy-whitespace,"
            echo "      perf budgets, redaction collateral)."
            drift=$((drift + 1))
            drifted_names="$drifted_names  $name ($url)\n"
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
echo "Downloaded: $ok   Skipped (already present): $skip   Failed: $fail   Drifted: $drift"
echo "================================================="

if [ "$ok" -eq 0 ] && [ "$skip" -eq 0 ]; then
    echo "No PDFs in corpus. SmokeCorpusTests will skip."
    exit 1
fi

if [ "$drift" -gt 0 ]; then
    echo ""
    echo "FAIL: $drift corpus file(s) do not match their pinned sha256 (#844):"
    printf "%b" "$drifted_names"
    echo "This is ALWAYS an error, with or without --require-all: a silently"
    echo "different fixture invalidates every baseline measured against it."
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
