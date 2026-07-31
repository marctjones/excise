#!/bin/bash
# check-test-prereqs.sh — Report which test-skip categories would unlock
# if the local environment had the optional tools / corpora installed.
#
# `dotnet test` reports tests as "Skipped" for many reasons; this script
# tells you which of those skips are environment-dependent (and how to fix
# them) versus genuinely intentional (e.g. headless-mode workarounds with
# coverage met by other tests).

set -u

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"
TEST_PDF_DIR="$PROJECT_ROOT/test-pdfs"

GREEN=$'\033[32m'
RED=$'\033[31m'
YELLOW=$'\033[33m'
DIM=$'\033[2m'
RESET=$'\033[0m'

ok=0
missing=0

check_bin() {
    local bin="$1"; local pkg="$2"; local unlocks="$3"
    if command -v "$bin" >/dev/null 2>&1; then
        printf "  %s✓%s %-12s %s(found: %s)%s\n" "$GREEN" "$RESET" "$bin" "$DIM" "$(command -v "$bin")" "$RESET"
        ok=$((ok+1))
    else
        printf "  %s✗%s %-12s %sinstall: %s%s\n" "$RED" "$RESET" "$bin" "$YELLOW" "$pkg" "$RESET"
        printf "                 unlocks: %s\n" "$unlocks"
        missing=$((missing+1))
    fi
}

check_dir() {
    local rel="$1"; local how="$2"; local unlocks="$3"
    local dir="$TEST_PDF_DIR/$rel"
    if [ -d "$dir" ] && [ -n "$(find "$dir" -maxdepth 3 -name '*.pdf' -print -quit 2>/dev/null)" ]; then
        local n
        n=$(find "$dir" -name '*.pdf' 2>/dev/null | wc -l)
        printf "  %s✓%s %-22s %s(%s PDFs)%s\n" "$GREEN" "$RESET" "$rel" "$DIM" "$n" "$RESET"
        ok=$((ok+1))
    else
        printf "  %s✗%s %-22s %sfetch: %s%s\n" "$RED" "$RESET" "$rel" "$YELLOW" "$how" "$RESET"
        printf "                           unlocks: %s\n" "$unlocks"
        missing=$((missing+1))
    fi
}

echo "================================================="
echo "Excise.App test prereq check"
echo "================================================="
echo
echo "External tools:"
check_bin tesseract  "apt install tesseract-ocr"  "Excise.Ocr.Tests + RevealRasterizedHidden"
check_bin mutool     "apt install mupdf-tools"    "Excise.Rendering differential vs MuPDF oracle"
check_bin pdftocairo "apt install poppler-utils"  "Excise.Rendering differential vs Poppler oracle"
check_bin gs         "brew install ghostscript"    "Optional third rendering oracle for unsettled corpus DIFFs"
VENDORED_PDFBOX="$(ls "$PROJECT_ROOT"/tools/vendor/pdfbox-app-*.jar 2>/dev/null | tail -1)"
if [[ -n "$VENDORED_PDFBOX" ]]; then
    printf "  %s✓%s %-12s %s(vendored: %s)%s\n" "$GREEN" "$RESET" "pdfbox" "$DIM" "$(basename "$VENDORED_PDFBOX")" "$RESET"
    ok=$((ok+1))
elif [[ -n "${EXCISE_PDFBOX_JAR:-}" && -f "${EXCISE_PDFBOX_JAR:-}" ]]; then
    printf "  %s✓%s %-12s %s(found: %s)%s\n" "$GREEN" "$RESET" "pdfbox" "$DIM" "$EXCISE_PDFBOX_JAR" "$RESET"
    ok=$((ok+1))
elif command -v pdfbox >/dev/null 2>&1; then
    printf "  %s✓%s %-12s %s(found: %s)%s\n" "$GREEN" "$RESET" "pdfbox" "$DIM" "$(command -v pdfbox)" "$RESET"
    ok=$((ok+1))
else
    printf "  %s✗%s %-12s %srun ./scripts/download-pdfbox.sh%s\n" "$RED" "$RESET" "pdfbox" "$YELLOW" "$RESET"
    printf "                 unlocks: Optional Apache PDFBox diagnostic oracle for corpus DIFF triage\n"
    missing=$((missing+1))
fi
# PdfiumNativeReferenceRenderer drives libpdfium directly; pdfium_test is not
# distributed anywhere (it needs a Chromium build), so the LIBRARY is what
# actually enables the oracle.
VENDORED_PDFIUM="$(ls "$PROJECT_ROOT"/tools/vendor/pdfium/lib/libpdfium.* 2>/dev/null | tail -1)"
if [[ -n "$VENDORED_PDFIUM" ]]; then
    printf "  %s✓%s %-12s %s(vendored: %s)%s\n" "$GREEN" "$RESET" "pdfium" "$DIM" "$(basename "$VENDORED_PDFIUM")" "$RESET"
    ok=$((ok+1))
elif [[ -n "${EXCISE_PDFIUM_LIB:-}" && -f "${EXCISE_PDFIUM_LIB:-}" ]]; then
    printf "  %s✓%s %-12s %s(found: %s)%s\n" "$GREEN" "$RESET" "pdfium" "$DIM" "$EXCISE_PDFIUM_LIB" "$RESET"
    ok=$((ok+1))
elif [[ -n "${EXCISE_PDFIUM_TEST:-}" && -x "${EXCISE_PDFIUM_TEST:-}" ]]; then
    printf "  %s✓%s %-12s %s(found: %s)%s\n" "$GREEN" "$RESET" "pdfium_test" "$DIM" "$EXCISE_PDFIUM_TEST" "$RESET"
    ok=$((ok+1))
elif command -v pdfium_test >/dev/null 2>&1; then
    printf "  %s✓%s %-12s %s(found: %s)%s\n" "$GREEN" "$RESET" "pdfium_test" "$DIM" "$(command -v pdfium_test)" "$RESET"
    ok=$((ok+1))
else
    printf "  %s✗%s %-12s %sset EXCISE_PDFIUM_TEST=/path/to/pdfium_test%s\n" "$RED" "$RESET" "pdfium_test" "$YELLOW" "$RESET"
    printf "                 run ./scripts/download-pdfium.sh (pdfium_test itself is not distributed)\n"
    missing=$((missing+1))
fi

echo
echo "Test corpora (under $TEST_PDF_DIR):"
check_dir "smoke"          "scripts/download-smoke-corpus.sh" "Excise.Rendering smoke + redaction round-trip"
check_dir "verapdf-corpus" "scripts/download-test-pdfs.sh"    "Excise.Core RealPdfTests + corpus rendering scan (2694 pages)"
check_dir "isartor"        "scripts/download-test-pdfs.sh"    "Isartor PDF/A round-trip + corpus rendering scan (205 pages)"
check_dir "pdfjs"          "scripts/download-pdfjs-corpus.sh" "corpus rendering scan (685 pages)"
check_dir "pdfium"         "scripts/download-pdfium-corpus.sh" "corpus rendering scan (331 pages) — the harshest of the four"
check_dir "poppler"        "scripts/download-poppler-corpus.sh" "Poppler regression corpus exploratory rendering"

echo
echo "================================================="
printf "Summary: %s%d ready%s, %s%d missing%s\n" "$GREEN" "$ok" "$RESET" "$RED" "$missing" "$RESET"
echo "================================================="
echo
echo "Unconditionally-skipped tests: NONE."
echo
echo "This section used to list four groups as permanently skipped by design"
echo "(arrow keys, redaction drag, the compound search flow, and the synthetic"
echo "search perf test). Verified 2026-07-31: the first three are plain"
echo "[FixedAvaloniaFact]s that RUN — they were fixed and this text was never"
echo "updated. The fourth was a [Fact(Skip=...)] that had never executed once;"
echo "it was deleted, since search perf is covered by the perf-budget \"search\""
echo "workflow on real PDFs and correctness by RealWorldSearchTests."
echo
echo "On a fully-provisioned machine the suite now skips ZERO tests. Skips you"
echo "see on CI are environment-gated and enumerated in tests/skip-allowlist/"
echo "with [requires: ...] markers (see #854) — those are expected: CI has"
echo "neither the gitignored corpora nor the optional external tools."

exit 0
