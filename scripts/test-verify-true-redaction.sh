#!/usr/bin/env bash
#
# Selftest for scripts/verify-true-redaction.sh (#1012).
#
# This is the redaction-architecture gate — the one t0 runs on every push
# because "you are your own third party". It is also the gate with the worst
# track record of guarding nothing: the #941 audit measured TWO of its checks
# passing over the exact downgrade they exist to block, because a `///` comment
# mentioning `RedactArea` satisfied a naive grep while the code drew a black box
# and removed no glyphs.
#
# So this drives the REAL script over a mirror of the REAL pipeline files, and
# applies the REAL downgrades to the mirror. The script resolves its inputs
# relative to the working directory, so running it from the mirror needs no copy
# of the script and no change to it. The working tree is never touched.
#
# Sub-second. Runs in t0.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SCRIPT="$ROOT/scripts/verify-true-redaction.sh"

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
fail() { echo "FAIL: $*" >&2; exit 1; }

# The files the gate inspects, derived FROM the gate rather than duplicated here.
pipeline_files() {
    grep -E '^[A-Z_]+="[^"]+\.cs"' "$SCRIPT" | sed -E 's/^[A-Z_]+="([^"]+)".*/\1/' | sort -u
}

P="$TMP/pristine"
count=0
while IFS= read -r f; do
    [[ -z "$f" ]] && continue
    [[ -f "$ROOT/$f" ]] || fail "verify-true-redaction.sh inspects $f, which does not exist"
    mkdir -p "$P/$(dirname "$f")"
    cp "$ROOT/$f" "$P/$f"
    count=$((count + 1))
done < <(pipeline_files)
[[ "$count" -ge 8 ]] || fail "only $count pipeline files found — the extraction above stopped working"

R="$TMP/root"
reset_tree() { rm -rf "$R"; cp -R "$P" "$R"; }
run() {
    set +e
    OUT="$(cd "$R" && "$SCRIPT" 2>&1)"
    RC=$?
    set -e
}
# replace_token <file> <from> <to> — and prove the mutation landed.
replace_token() {
    sed -i.bak "s/$2/$3/g" "$R/$1"
    rm -f "$R/$1.bak"
    if grep -q "$2" "$R/$1"; then fail "mutation did not reach: '$2' is still in $1"; fi
}

echo "==> selftest: verify-true-redaction.sh ($count pipeline files)"

# ── 1. The unmutated mirror passes ───────────────────────────────────────────
reset_tree
run
[[ "$RC" -eq 0 ]] || fail "the unmutated pipeline must pass, or the downgrades below prove nothing (exit $RC)
$OUT"
echo "    unmutated pipeline                   exit 0"

# ── 2. THE DOWNGRADE: the GUI stops calling the glyph engine ─────────────────
# Replace the page.RedactArea/RedactAreas delegation with a bare black box.
# This is #941's measured false pass: the check stayed green, carried entirely
# by one `//` note, while redaction became visual-only.
reset_tree
replace_token Excise.App/Services/RedactionService.cs 'page\.RedactArea' 'AppendBlackRectangle'
run
[[ "$RC" -ne 0 ]] || fail "the GUI drawing a black box instead of calling page.RedactArea MUST fail this gate
$OUT"
grep -q "downgraded to visual-only" <<<"$OUT" || fail "expected the visual-only-downgrade verdict:
$OUT"
echo "    GUI downgraded to a black box        exit $RC"

# ── 3. The document-level entry stops routing through the glyph path ─────────
reset_tree
replace_token Excise.Core/Text/Segmentation/PdfDocumentRedactionExtensions.cs \
    'page\.RedactArea' 'AppendBlackRectangle'
run
[[ "$RC" -ne 0 ]] || fail "doc.RedactText bypassing the glyph-level path MUST fail this gate
$OUT"
echo "    doc.RedactText bypasses the engine   exit $RC"

# ── 4. RedactArea stops invoking GlyphRemover ────────────────────────────────
reset_tree
replace_token Excise.Core/Text/Segmentation/PdfPageRedactionExtensions.cs 'GlyphRemover' 'Xyzzy'
run
[[ "$RC" -ne 0 ]] || fail "RedactArea that no longer invokes GlyphRemover MUST fail this gate
$OUT"
echo "    GlyphRemover unwired from RedactArea exit $RC"

# ── 5. A pipeline file is deleted outright ───────────────────────────────────
reset_tree
rm -f "$R/Excise.Core/Text/Segmentation/GlyphRemover.cs"
run
[[ "$RC" -ne 0 ]] || fail "deleting GlyphRemover.cs MUST fail this gate
$OUT"
echo "    GlyphRemover.cs deleted              exit $RC"

# ── 6. The end-to-end security assertion is weakened ─────────────────────────
reset_tree
replace_token Excise.Core.Tests/Text/Segmentation/PdfPageRedactionEndToEndTests.cs \
    '\.Should()\.NotContain(' '.Should().Contain('
run
[[ "$RC" -ne 0 ]] || fail "an end-to-end test that no longer asserts REMOVAL MUST fail this gate
$OUT"
echo "    removal assertion weakened           exit $RC"

# ── 7. A comment announcing a planned downgrade ──────────────────────────────
reset_tree
printf '\n// HACK: just draw black box here for now\n' \
    >> "$R/Excise.Core/Text/Segmentation/GlyphRemover.cs"
run
[[ "$RC" -ne 0 ]] || fail "a comment announcing a visual-only downgrade must at least fail loudly
$OUT"
echo "    downgrade comment planted            exit $RC"

echo "==> verify-true-redaction.sh selftest OK"
