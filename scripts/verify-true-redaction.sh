#!/bin/bash
# Build-time guard for TRUE content-level redaction.
#
# Ensures the glyph-level redaction pipeline hasn't been accidentally
# simplified or removed (which would silently downgrade redaction to
# visual-only black boxes — a security vulnerability).
#
# As of v2.0 the redaction engine is pure-.NET and owned by Excise.Core
# (Excise.Core/Text/Segmentation/*, Excise.Core/Content/*). The GUI's
# RedactionService is a thin orchestrator that calls page.RedactArea(...).
# This script verifies that architecture is intact.
#
# Usage: ./scripts/verify-true-redaction.sh
# Returns: 0 if verification passes, 1 if a security regression is detected.

set -uo pipefail

echo "🔍 Verifying TRUE content-level redaction implementation..."

# --- Files that make up the TRUE-redaction pipeline ---
REDACTION_SERVICE="Excise.App/Services/RedactionService.cs"     # GUI orchestrator
# The DOCUMENT-level entry point (doc.RedactText). Until #928 this pointed at
# Excise.Core/Operations/PdfRedaction.cs — an operator-level path that was
# unreachable from production and whose docstring claimed glyph-level removal.
# So two checks in the gate guarding the redaction guarantee were pinned to a
# file redaction never went through, and would have passed with the real
# document-level API deleted.
CORE_API="Excise.Core/Text/Segmentation/PdfDocumentRedactionExtensions.cs"
CORE_EXT="Excise.Core/Text/Segmentation/PdfPageRedactionExtensions.cs"  # page.RedactArea entry
CORE_GLYPH="Excise.Core/Text/Segmentation/GlyphRemover.cs"       # glyph-level removal
CORE_IMAGE="Excise.Core/Text/Segmentation/ImageRedactor.cs"      # image removal
CORE_RECON="Excise.Core/Text/Segmentation/OperationReconstructor.cs"  # rebuild BT/Tf/Tj
CORE_PARSER="Excise.Core/Content/ContentStreamParser.cs"         # parse operators
CORE_WRITER="Excise.Core/Content/ContentStreamWriter.cs"         # serialize operators
SECURITY_TEST="Excise.Core.Tests/Text/Segmentation/PdfPageRedactionEndToEndTests.cs"

RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; NC='\033[0m'
ERRORS=0

# fail <message...>
fail() {
    echo -e "${RED}FAILED${NC}"
    for line in "$@"; do echo "    ❌ $line"; done
    ERRORS=$((ERRORS + 1))
}
pass() { echo -e "${GREEN}PASS${NC}"; }

# require_file <label> <path>
require_file() {
    echo -n "  ✓ $1... "
    if [ -f "$2" ]; then pass; else
        fail "SECURITY: $2 is missing — the glyph-level pipeline is incomplete."
    fi
}

# require_grep <label> <pattern> <file> <error...>
#
# ⚠️ Matches CODE ONLY — comment lines are stripped before the match.
#
# This is not fastidiousness, it is the #941 audit finding. A prose mention of
# the very construct the gate is hunting for SATISFIES a naive grep, so the
# gate greens over a falsified property. Measured on this script, 2026-08-15:
#
#   * replacing the real `page.RedactAreas(contentAreas, ...)` delegation in
#     PdfDocumentRedactionExtensions.cs with a visual-only black box left
#     "doc.RedactText delegates to glyph-level RedactArea" at PASS — five
#     `///` doc comments in that file mention RedactArea.
#   * replacing `page.RedactArea(coreRect, ...)` in RedactionService.cs with a
#     bare AppendBlackRectangle left "GUI delegates to glyph-level
#     page.RedactArea" at PASS — carried entirely by ONE `//` note on line 60.
#
# Both mutations are precisely the downgrade this gate exists to block, and
# both shipped a green gate. That is #941's class recurring inside #941's own
# fix: the assertion named the right file and still guarded nothing.
#
# Stripping is deliberately crude (drop everything after `//`, drop block-comment
# continuation lines). It can only ever REMOVE text, so it cannot manufacture a
# false pass — the failure direction is a false alarm, which is the safe one.
require_grep() {
    local label="$1" pat="$2" file="$3"; shift 3
    echo -n "  ✓ $label... "
    # NOT grep -q: under `set -o pipefail`, -q exits at the first match and
    # closes the pipe while sed is still writing. GNU sed's smaller stdio
    # buffer then takes SIGPIPE (exit 141) and pipefail turns a SUCCESSFUL
    # match into a FAIL — which is exactly how this check went red on Linux
    # CI while passing on macOS (BSD sed flushes the whole file in one
    # write). Reading to EOF removes the race on both.
    if [ -f "$file" ] && sed -e 's://.*::' -e '/^[[:space:]]*\*/d' "$file" 2>/dev/null \
         | grep -E "$pat" >/dev/null; then pass; else
        fail "$@"
    fi
}

# Check 1: the public redaction API/result still live in Excise.Core.
require_file "Core redaction API exists" "$CORE_API"
# Was: "Core RedactionResult model present". That type existed ONLY in the
# deleted operator-level path, so the check could not fail for any reason that
# mattered. What matters is that the document-level entry delegates to the
# glyph-level engine rather than removing whole operators or drawing a box.
# The pattern pins the CALL form (`page.RedactArea(` / `page.RedactAreas(`), not
# the bare name: a bare "RedactArea" is satisfied by any `<see cref=...>` in the
# file's own documentation, which is how this check passed over a visual-only
# downgrade (see the note on require_grep).
require_grep "doc.RedactText delegates to glyph-level RedactArea" \
    "page\.RedactAreas?\(" "$CORE_API" \
    "SECURITY: $CORE_API no longer routes through the glyph-level RedactArea path."

# Check 3: the GUI must delegate to the glyph-level engine (page.RedactArea),
# NOT draw a black box only.
require_grep "GUI delegates to glyph-level page.RedactArea" \
    "page\.RedactAreas?\(" "$REDACTION_SERVICE" \
    "SECURITY: RedactionService no longer calls page.RedactArea — redaction" \
    "may have been downgraded to visual-only (black box without glyph removal)."

# Check 4: the core glyph-removal + reconstruction stages must exist.
require_file "GlyphRemover (glyph-level removal) exists" "$CORE_GLYPH"
require_file "ImageRedactor (image removal) exists" "$CORE_IMAGE"
require_file "OperationReconstructor (rebuild text blocks) exists" "$CORE_RECON"

# Check 5: the parse → rebuild content-stream pipeline must exist.
require_file "ContentStreamParser (parse operators) exists" "$CORE_PARSER"
require_file "ContentStreamWriter (rebuild operators) exists" "$CORE_WRITER"

# Check 6: the RedactArea entry point must wire the glyph + image passes.
require_grep "RedactArea wires GlyphRemover" "GlyphRemover" "$CORE_EXT" \
    "SECURITY: $CORE_EXT no longer invokes GlyphRemover — text glyphs would" \
    "not be removed from the content stream."
require_grep "RedactArea wires ImageRedactor" "ImageRedactor" "$CORE_EXT" \
    "SECURITY: $CORE_EXT no longer invokes ImageRedactor — images overlapping" \
    "the redaction area would not be removed."

# Check 7: a regression test must assert redacted text is ABSENT from the
# saved content-stream bytes (the actual security guarantee).
# The assertion FORM, not the bare word: "NotContain" alone matches a comment,
# an identifier, or a disabled line. `.Should().NotContain(` is an executed
# assertion or nothing.
require_grep "End-to-end security test asserts removal" \
    "\.Should\(\)\.NotContain\(" "$SECURITY_TEST" \
    "SECURITY: $SECURITY_TEST does not assert redacted text is removed from" \
    "the content stream. The core security guarantee is untested."

# Check 8: scan the pipeline files for comments hinting at a visual-only downgrade.
echo -n "  ✓ Checking for suspicious simplification comments... "
SUSPICIOUS='TODO.*visual.*only|HACK.*just.*draw.*black|simplif.*redaction|skip.*content.*removal|visual.?only redaction'
if grep -RniE "$SUSPICIOUS" "$REDACTION_SERVICE" "$CORE_GLYPH" "$CORE_EXT" 2>/dev/null; then
    echo -e "${YELLOW}WARNING${NC}"
    echo "    ⚠️  Found comments that may indicate a planned redaction downgrade."
    ERRORS=$((ERRORS + 1))
else
    pass
fi

echo ""
if [ "$ERRORS" -eq 0 ]; then
    echo -e "${GREEN}✅ TRUE redaction verification PASSED${NC}"
    echo "   The glyph-level redaction pipeline is intact."
    exit 0
else
    echo -e "${RED}❌ TRUE redaction verification FAILED with $ERRORS error(s)${NC}"
    echo ""
    echo "CRITICAL: the redaction implementation may have been changed in a way"
    echo "that compromises TRUE content-level removal (text still extractable)."
    echo "Review the pipeline (Excise.Core/Text/Segmentation + Excise.Core/Content)"
    echo "and ensure glyph-level removal is maintained. DO NOT DEPLOY this build."
    exit 1
fi
