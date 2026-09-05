#!/usr/bin/env bash
# check-oracle-tools.sh: fail loudly when a reference oracle is missing.
#
# WHY THIS EXISTS
#
# A differential test whose reference tool is absent does not fail — it
# Assert.SkipUnless(IsAvailable) and the run goes GREEN while covering
# nothing. Green-because-everything-skipped is indistinguishable from
# green-because-everything-passed to `dotnet test`.
#
# That is precisely the trap CLAUDE.md's rule exists for:
#
#     A tool must not be its own oracle for the property it exists to
#     guarantee.
#
# Three shipped redaction leaks (#636, #608, #637) passed a green suite. This
# gate is what makes a missing oracle loud instead of invisible.
#
# Ported from .github/workflows/rendering-linux.yml's "Assert tools are on
# PATH (no silent self-oracle)" step when GitHub Actions was removed (#1360).
# xvfb-run is deliberately NOT checked: it is the one genuinely Linux-specific
# entry, and Avalonia headless needs no display server on macOS.
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

missing=0
note() { printf '  \033[32m✓\033[0m %-12s %s\n' "$1" "$2"; }
fail() { printf '  \033[31m✗\033[0m %-12s %s\n' "$1" "$2"; missing=1; }

echo "reference oracles:"
for t in mutool gs pdftocairo pdftoppm pdftotext pdfsig qpdf tesseract; do
    p="$(command -v "$t" 2>/dev/null)" && note "$t" "$p" || fail "$t" "NOT FOUND on PATH"
done

# Optional-but-wired oracles. These are not required to be on PATH, but if the
# artifact is present the env var must point at it, or the tests silently skip
# while the file sits right there — the #935 "changes nothing" trap.
jar="$(ls tools/vendor/pdfbox-app-*.jar 2>/dev/null | sort | tail -1)"
if [ -n "$jar" ]; then
    note "pdfbox" "$jar"
    [ -n "${EXCISE_PDFBOX_JAR:-}" ] || printf '      \033[33mhint\033[0m export EXCISE_PDFBOX_JAR=%s/%s\n' "$ROOT" "$jar"
else
    fail "pdfbox" "no tools/vendor/pdfbox-app-*.jar — run scripts/download-pdfbox.sh"
fi

case "$(uname -s)" in
    Darwin) pdfium_lib="tools/vendor/pdfium/lib/libpdfium.dylib" ;;
    *)      pdfium_lib="tools/vendor/pdfium/lib/libpdfium.so" ;;
esac
if [ -f "$pdfium_lib" ]; then
    # PdfiumNativeReferenceRenderer — THE pdfium oracle — walks up from
    # AppContext.BaseDirectory to tools/vendor/pdfium/lib and needs no env var
    # (EXCISE_PDFIUM_LIB only overrides it). Do NOT confuse this with
    # EXCISE_PDFIUM_TEST, which points at the separate `pdfium_test` BINARY
    # used by PdfiumReferenceRenderer — a class whose only caller is a unit
    # test for its argument builder. CLAUDE.md calls out this exact name
    # collision; setting the wrong one buys nothing and looks like it worked.
    note "pdfium" "$pdfium_lib"
else
    fail "pdfium" "$pdfium_lib missing — run scripts/download-pdfium.sh"
fi

echo "corpora:"
[ -f test-pdfs/smoke/irs-w9.pdf ] && note "smoke" "test-pdfs/smoke" \
    || fail "smoke" "test-pdfs/smoke/irs-w9.pdf missing — run scripts/download-smoke-corpus.sh"
[ -n "$(ls test-pdfs/federal/*.pdf 2>/dev/null)" ] && note "federal" "test-pdfs/federal" \
    || fail "federal" "test-pdfs/federal empty — run scripts/download-federal-corpus.sh"

if [ "$missing" -ne 0 ]; then
    cat >&2 <<'MSG'

FAIL: a reference oracle is missing.

  This is NOT a "skip the differential tests" condition. Every test gated on a
  missing tool would skip, and the run would report green while verifying
  nothing against anything but excise itself.

  Install what is missing, or run the oracle subsets on a machine that has it.
MSG
    exit 1
fi
echo "all reference oracles resolved"
