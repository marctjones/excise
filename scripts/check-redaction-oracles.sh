#!/usr/bin/env bash
#
# Redaction leak assertions must not be excise refereeing excise (#1029).
#
# CLAUDE.md's central rule: "A tool must not be its own oracle for the property
# it exists to guarantee. excise confirming that excise removed the text proves
# only that its bugs are self-consistent." Three shipped leaks passed a green
# suite that way.
#
# So every redaction leak assertion must have a path to an assertion that does
# not depend on excise's own reading of the file:
#
#   * an independent extractor or renderer  (mutool, qpdf, ghostscript,
#     pdftocairo, pdftoppm, PDFBox, pdfium)
#   * a saved-bytes scan that decompresses  (SavedPdfLeakScanner, #1049)
#   * an ink differential                   (InkFraction over a region)
#
# A method asserting only on page.Text / ExtractAllText / Letters is testing
# the extractor against the remover — both of which are excise. The original
# file-level gate remains below as the cheap coarse check; #1077 adds the
# method-level gate because one mutool call elsewhere in the file must not
# bless a different, self-oracle-only leak assertion.
#
# ── Why a DETECTOR rather than a review ─────────────────────────────────────
#
# #1029 was scoped as "audit 48 files and write a reason for each". A review
# does not converge and cannot be re-run. This is the same question asked
# mechanically: it produces a list, it takes a second, and it can gate.
#
# It has already earned that: a hand pass over #1049 MISSED
# LineContinuationRedactionLeakTests.cs — a file with "LeakTests" in its name —
# and a six-line detector found it immediately.
#
# Usage: scripts/check-redaction-oracles.sh [--update]
#
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO"
ALLOW="tests/redaction-self-oracle-allowlist.txt"
METHOD_ALLOW="tests/redaction-self-oracle-method-allowlist.txt"
UPDATE="${1:-}"

INDEPENDENT='Mutool|Qpdf|Ghostscript|Pdftocairo|Pdftoppm|Pdftotext|PdfBox|PdfiumNative|SavedPdfLeakScanner|InkFraction|ReferenceRedactor|VeraPdf'
# GetContentStream(Bytes) added by the t0-gates review (2026-09-21): a method
# asserting NotContain over doc.GetPage(N).GetContentStreamBytes() is exactly
# the "excise reads its own removal" shape the other patterns already cover --
# it was missing only because nothing had named it yet
# (RedactCommandTests.cs::RunRedact_RemovesExactMatch_FromContentStream carried
# a comment claiming the pdftotext property while never calling pdftotext).
# "GetContentStream" (no trailing paren -- awk's -v string parsing does not
# reliably round-trip a literal "\(" back into a regex metacharacter) matches
# both GetContentStream() and GetContentStreamBytes() as a substring.
SELF='\.Text\.Should|ExtractAllText|\.Letters|GetLetters|GetContentStream'

# #1786: SELF above only matches a self-oracle read CHAINED directly onto the
# assertion (`.Text.Should(...)`). It went blind the moment a test assigned
# the extraction to a local first -- `var remaining = reopened.GetPage(p).Text;`
# ... `stillContainsTarget.Should().BeFalse(...)` a few lines later -- because
# neither line alone matches the chained pattern. That shape is not rare: it is
# what a multi-phase test naturally does. Two independent signals below, ANDed
# together at the method level, catch it without caring whether the read and
# the assertion share a line: SELF_READ is any self-oracle extraction call
# (the bare form, not chained), LEAK_ASSERT is any leak-shaped assertion
# (a "this text/value is gone" check). A method with both, and no INDEPENDENT
# oracle anywhere in it, is the same defect the chained SELF pattern already
# flags -- just one variable hop removed.
# NOTE: no \b -- this feeds awk's ERE engine below (BWK/"one true" awk on
# macOS does not support \b; it silently never matches rather than erroring,
# which is worse than a compile failure). "([^A-Za-z0-9_]|$)" is the
# \b-free word-boundary-after equivalent: a non-identifier character or
# end of line, so ".Text;" / ".Text)" match but "MutoolTextExtractor" and
# "x.TextValue" do not.
#
# Deliberately narrower than a bare "\.Text": the first cut of this pattern
# matched ANY ".Text" anywhere in a method (UI control text, field names,
# unrelated properties), which combined with LEAK_ASSERT's generic
# NotContain/BeFalse/BeEmpty flagged ~50 methods across the suite that read
# page text for something other than a leak claim, or asserted an unrelated
# boolean in the same method. "GetPage(...).Text" is the ACTUAL shape #1786
# is about (excise re-extracting a PAGE's text), so this stays tight to that.
SELF_READ='GetPage\([^)]*\)\.Text([^A-Za-z0-9_]|$)|ExtractAllText|\.Letters|GetLetters|GetContentStream'
LEAK_ASSERT='NotContain|BeFalse|BeEmpty|DoesNotContain'

offenders=""
method_offenders=""
# #1122: the pattern used to be "*Redaction*Tests.cs" ONLY, which meant
# HiddenTextDetectorTests.cs -- the tests for the detector whose entire job is
# finding bad redactions -- was invisible to this gate. Not allow-listed,
# not failing: unseen. The rule this script enforces is that a tool must not
# be its own oracle for the property it exists to guarantee, and the detector
# for that property was exempt by filename.
#
# RedactCommandTests.cs added explicitly (t0-gates review, 2026-09-21): its
# name contains "Redact", not "Redaction", so "*Redaction*Tests.cs" never
# matched it -- the CLI's own `excise redact` leak assertions were invisible
# to this gate. A broader "*Command*Tests.cs"/"*Service*Tests.cs" sweep was
# considered and rejected here: simulating it pulled in ~40 unrelated files
# and ~60 new method-level entries, most of them low-level geometry/workflow
# tests (e.g. RedactionBoundsTests.cs's TextBounds_*/PathBounds_* cases) with
# nothing for an oracle to corroborate. That wider population-by-content
# rework (matching any file that calls a redaction entry point) is tracked
# separately rather than done piecemeal here.
population="$(find Excise.*.Tests \
                \( -name "*Redaction*Tests.cs" -o -name "*HiddenText*Tests.cs" -o -name "*Audit*Tests.cs" -o -name "RedactCommandTests.cs" \) \
                -not -path '*/obj/*' -not -path '*/bin/*' | sort)"
# #1778: the name-scoped population above missed every #1769 self-oracle --
# ScriptedGuiTests.cs and GoldenPathTests.cs redact and assert removal, but are
# named after the WORKFLOW, not the feature. So the method gate also scans every
# test file that CALLS a redaction entry point, whatever its name. For those
# content-scoped files a method is in scope only if its own body calls the entry
# point (REDACT_CALL below): a file that happens to redact in one test must not
# drag its unrelated Letters/Text assertions into a redaction gate. The
# name-scoped files keep the rule they always had (no REDACT_CALL requirement),
# so this only ever ADDS methods to the gate.
REDACT_CALL='\.(RedactText|RedactTextAsync|RedactArea|RedactAreaAsync|RedactAreas|RedactAreaWithReport|RedactAreasWithReport)\(|ApplyAllRedactions|ApplyRedactionsCommand|ApplyRedactionCommand|RedactTextCommand|RedactSelectionCommand|FlattenOcrRedactCommand'
content_population="$(grep -rlE "$REDACT_CALL" Excise.*.Tests --include='*.cs' 2>/dev/null \
                        | grep -vE '/(bin|obj)/' | sort -u \
                        | comm -23 - <(printf '%s\n' "$population" | sort -u) || true)"
# A floor, not just the per-item checks below: at 0 scanned files this script
# used to exit 0 silently — a glob typo, a directory rename, or the population
# collapsing some other way would read as a clean run (t0-gates review,
# 2026-09-21, "weird things" #6). The gate exists to be a net; a net that can
# quietly shrink to nothing is not one.
population_count="$(printf '%s\n' "$population" | grep -c . || true)"
if [[ "$population_count" -eq 0 ]]; then
  echo "❌ ZERO files matched the redaction-test population glob — that is not a clean run," >&2
  echo "   it is the glob (or the directory layout) having broken. Check the find pattern above." >&2
  exit 1
fi
content_count="$(printf '%s\n' "$content_population" | grep -c . || true)"
if [[ "$content_count" -eq 0 ]]; then
  echo "❌ ZERO test files outside the name glob call a redaction entry point (REDACT_CALL)." >&2
  echo "   ~170 did when #1778 added this scan; zero means the pattern or the layout broke," >&2
  echo "   not that the suite stopped redacting." >&2
  exit 1
fi

for f in $population; do
  # Only files that actually assert about leaks are in scope; a pure geometry
  # or workflow test has nothing for an oracle to corroborate.
  grep -qE "$SELF" "$f" || continue
  grep -qE "$INDEPENDENT" "$f" && continue
  offenders="${offenders}${f}"$'\n'
done
offenders=$(printf '%s' "$offenders" | grep . || true)

# #1077: classify individual [Fact]/[Theory] methods. This is deliberately a
# small lexical detector, not a C# parser: it is fast enough to run as a gate,
# and catches the real blind spot where one file has both a mutool-backed test
# and a separate page.Text-only leak assertion. Helpers/call graphs remain a
# documented future Roslyn upgrade; keep helper-backed methods explicitly
# allow-listed until that exists rather than pretending a regex proved them.
while IFS=$'\t' read -r f need_redact; do
  [[ -n "$f" ]] || continue
  # #1786: has_self (the CHAINED pattern, e.g. `.Text.Should(...)`) OR
  # (has_read AND has_assert) -- a self-oracle READ (SELF_READ) and a
  # leak-shaped ASSERTION (LEAK_ASSERT) ANYWHERE in the same method, whether
  # or not they share a line. That second arm is what catches a self-oracle
  # extraction assigned to a local variable and asserted on several lines
  # later -- the exact shape the chained-only pattern went blind to.
  #
  # ENVIRON, not -v: `awk -v x="$SHELL_VAR"` re-parses the value as an awk
  # STRING LITERAL, which silently eats a backslash before any character it
  # does not recognize as an escape -- `\(` and `\)` survive as bare `(`/`)`,
  # turning "GetPage\([^)]*\)\.Text" from a literal match into a regex
  # GROUPING construct that no longer means what it says (found while adding
  # SELF_READ's GetPage(...) pattern here: it silently matched nothing).
  # `\.` happened to survive this undetected because an unescaped `.` still
  # matches a literal dot, just also over-matches other characters -- ENVIRON
  # values are not string-literal-parsed, so this stays correct for every
  # pattern here, not by accident.
  #
  # #1778 "token presence is not use": INDEPENDENT is matched against CODE only
  # (string literals and // comments stripped), so a comment that says
  # "mutool would catch this" no longer corroborates anything; and a mention
  # that sits only inside a `foreach` body does not count either --
  # RedactionMouseWorkflowTests passed this gate with its SavedPdfLeakScanner
  # check inside a loop over a collection that was EMPTY for three of four
  # scenarios. A loop's oracle may be real, but a lexical gate cannot see the
  # collection is non-empty, so the method must also carry one unlooped
  # independent assertion (or be declared in the allowlist with a reason).
  # self/read/assert are still matched on the raw line, exactly as before, so
  # neither rule can drop a method the gate used to flag.
  method_offenders+="$(SELF="$SELF" SELF_READ="$SELF_READ" LEAK_ASSERT="$LEAK_ASSERT" \
      INDEPENDENT="$INDEPENDENT" REDACT_CALL="$REDACT_CALL" NEED_REDACT="$need_redact" awk '
    BEGIN {
      self = ENVIRON["SELF"]
      selfread = ENVIRON["SELF_READ"]
      leakassert = ENVIRON["LEAK_ASSERT"]
      independent = ENVIRON["INDEPENDENT"]
      redactcall = ENVIRON["REDACT_CALL"]
      need_redact = ENVIRON["NEED_REDACT"] + 0
    }
    function flush() {
      if (!in_test) return
      offends = has_self || (has_read && has_assert)
      # Content-scoped (#1778): the method redacts AND asserts absence -- a
      # chained `.Text.Should().Be(...)` read BEFORE the redaction is not a
      # leak claim on its own.
      if (need_redact && !(has_redact && has_assert)) offends = 0
      if (!offends || has_independent) return
      if (method == "") method = "<unresolved-method>"
      print FILENAME "::" method
    }
    /^[[:space:]]*\[(Fact|Theory)(\(|\])/ {
      flush()
      in_test = 1
      has_self = 0
      has_read = 0
      has_assert = 0
      has_independent = 0
      has_redact = 0
      loop_state = 0
      method = ""
      looking_for_method = 1
    }
    {
      if (!in_test) next
      if ($0 ~ self) has_self = 1
      if ($0 ~ selfread) has_read = 1
      if ($0 ~ leakassert) has_assert = 1
      code = $0
      gsub(/"([^"\\]|\\.)*"/, "\"\"", code)
      gsub(/\047([^\047\\]|\\.)\047/, "\047\047", code)
      sub(/\/\/.*/, "", code)
      if (code ~ redactcall) has_redact = 1
      # A loop over an inline, non-empty literal (`in new[] { A, B }`,
      # `in [A, B]`) visibly runs its body; only a loop over a collection
      # the line does not spell out is treated as possibly zero-trip.
      if (!loop_state && code ~ /(^|[^A-Za-z0-9_.])foreach[[:space:]]*\(/ \
          && code !~ /[[:space:]]in[[:space:]]+(new[^({;]*\{[[:space:]]*[^[:space:]}]|\[[[:space:]]*[^[:space:]\]])/) {
        loop_state = 1
        loop_depth = depth
        loop_braced = 0
      }
      line_in_loop = loop_state
      opens = gsub(/\{/, "{", code)
      closes = gsub(/\}/, "}", code)
      depth += opens - closes
      if (code ~ independent && !line_in_loop) has_independent = 1
      if (loop_state) {
        if (depth > loop_depth) loop_braced = 1
        if (loop_braced && depth <= loop_depth) loop_state = 0
        else if (!loop_braced && code ~ /;/) loop_state = 0
      }
      if (looking_for_method && $0 ~ /^[[:space:]]*(public|protected|internal|private)[[:space:]].*\(/) {
        signature = $0
        sub(/[[:space:]]*\(.*/, "", signature)
        count = split(signature, words, /[[:space:]]+/)
        method = words[count]
        looking_for_method = 0
      }
    }
    END { flush() }
  ' "$f")"$'\n'
done < <(printf '%s\n' "$population" | awk 'NF { print $0 "\t0" }'; printf '%s\n' "$content_population" | awk 'NF { print $0 "\t1" }')
method_offenders=$(printf '%s' "$method_offenders" | grep . || true)

if [[ "$UPDATE" == "--update" ]]; then
  {
    echo "# Redaction test files whose leak assertions rely on excise's own reading."
    echo "# Generated by scripts/check-redaction-oracles.sh --update; reasons are hand-written."
    echo "# Removing a line is how corroboration is claimed back. Each entry is a file"
    echo "# where a leak could pass unnoticed if the extractor and the remover share a bug."
    while IFS= read -r f; do
      [[ -n "$f" ]] || continue
      reason=$( { grep -F "$f" "$ALLOW" 2>/dev/null || true; } | sed 's/^[^#]*#//' | head -1 )
      printf '%s  #%s\n' "$f" "${reason:- no independent oracle yet}"
    done <<< "$offenders"
  } > "$ALLOW.new"
  mv "$ALLOW.new" "$ALLOW"
  {
    echo "# Redaction test methods whose leak assertions rely on excise's own reading."
    echo "# Generated by scripts/check-redaction-oracles.sh --update; reasons are hand-written."
    echo "# Format: path/to/Test.cs::MethodName  # reason"
    while IFS= read -r entry; do
      [[ -n "$entry" ]] || continue
      reason=$( { grep -F "$entry" "$METHOD_ALLOW" 2>/dev/null || true; } | sed 's/^[^#]*#//' | head -1 )
      if [[ -z "$reason" ]]; then
        file="${entry%%::*}"
        reason=$( { grep -F "$file" "$ALLOW" 2>/dev/null || true; } | sed 's/^[^#]*#//' | head -1 )
      fi
      printf '%s  #%s\n' "$entry" "${reason:- no independent oracle yet}"
    done <<< "$method_offenders"
  } > "$METHOD_ALLOW.new"
  mv "$METHOD_ALLOW.new" "$METHOD_ALLOW"
  echo "wrote $ALLOW and $METHOD_ALLOW"
  exit 0
fi

[[ -f "$ALLOW" ]] || { echo "❌ no allowlist at $ALLOW — create it with --update" >&2; exit 1; }
[[ -f "$METHOD_ALLOW" ]] || { echo "❌ no method allowlist at $METHOD_ALLOW — create it with --update" >&2; exit 1; }

declared=$(grep -v '^#' "$ALLOW" | awk '{print $1}' | grep . | sort -u || true)
current=$(printf '%s' "$offenders" | sort -u)
declared_methods=$(grep -v '^#' "$METHOD_ALLOW" | awk '{print $1}' | grep . | sort -u || true)
current_methods=$(printf '%s' "$method_offenders" | sort -u)

new=$(comm -23 <(printf '%s\n' $current) <(printf '%s\n' $declared) | grep . || true)
fixed=$(comm -13 <(printf '%s\n' $current) <(printf '%s\n' $declared) | grep . || true)
new_methods=$(comm -23 <(printf '%s\n' "$current_methods") <(printf '%s\n' "$declared_methods") | grep . || true)
fixed_methods=$(comm -13 <(printf '%s\n' "$current_methods") <(printf '%s\n' "$declared_methods") | grep . || true)

status=0
if [[ -n "$new" ]]; then
  echo "❌ redaction test file(s) with NO independent oracle and no declared reason:" >&2
  printf '%s\n' "$new" | sed 's/^/     /' >&2
  echo "   Add a mutool/qpdf/ink-differential or SavedPdfLeakScanner assertion," >&2
  echo "   or declare it: scripts/check-redaction-oracles.sh --update" >&2
  status=1
fi
if [[ -n "$fixed" ]]; then
  echo "❌ file(s) listed as self-oracle-only now HAVE an independent oracle:" >&2
  printf '%s\n' "$fixed" | sed 's/^/     /' >&2
  echo "   Remove them from $ALLOW — corroboration coming back must be recorded," >&2
  echo "   or the list becomes a blanket excuse." >&2
  status=1
fi
if [[ -n "$new_methods" ]]; then
  echo "❌ redaction test method(s) with NO independent oracle and no declared reason:" >&2
  printf '%s\n' "$new_methods" | sed 's/^/     /' >&2
  echo "   Add a non-excise oracle in that method, or declare it with a reason:" >&2
  echo "   scripts/check-redaction-oracles.sh --update" >&2
  status=1
fi
if [[ -n "$fixed_methods" ]]; then
  echo "❌ method(s) listed as self-oracle-only now HAVE an independent oracle:" >&2
  printf '%s\n' "$fixed_methods" | sed 's/^/     /' >&2
  echo "   Remove them from $METHOD_ALLOW — corroboration coming back must be recorded." >&2
  status=1
fi

if (( status == 0 )); then
  declared_file_count=$(printf '%s\n' "$declared" | awk 'NF { count++ } END { print count + 0 }')
  declared_method_count=$(printf '%s\n' "$declared_methods" | awk 'NF { count++ } END { print count + 0 }')
  echo "✅ every redaction test file and leak-asserting method is either corroborated"
  echo "   by a non-excise oracle or a declared, reasoned exception"
  echo "   (${declared_file_count} files; ${declared_method_count} methods)."
fi
exit $status
