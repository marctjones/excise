#!/usr/bin/env bash
#
# Mutation self-test for the #1077 per-method extension of
# check-redaction-oracles.sh. A file-level detector passes when a file has one
# independent oracle anywhere; this proves that removing the oracle from ONE
# method still fails even while a sibling method retains one.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

fail() { echo "FAIL: $*" >&2; exit 1; }

mkdir -p "$WORK/scripts" "$WORK/tests" "$WORK/Excise.Core.Tests"
cp "$HERE/check-redaction-oracles.sh" "$WORK/scripts/check-redaction-oracles.sh"
chmod +x "$WORK/scripts/check-redaction-oracles.sh"

cat > "$WORK/Excise.Core.Tests/MethodOracleRedactionTests.cs" <<'EOF'
using Xunit;

public sealed class MethodOracleRedactionTests
{
    [Fact]
    public void FirstLeakAssertion_HasItsOwnIndependentOracle()
    {
        page.Text.Should().NotContain("SECRET");
        MutoolTextExtractor.ExtractPage("output.pdf", 1).Should().NotContain("SECRET");
    }

    [Fact]
    public void SecondLeakAssertion_StillHasAnIndependentOracle()
    {
        page.Text.Should().NotContain("SECRET");
        MutoolTextExtractor.ExtractPage("output.pdf", 1).Should().NotContain("SECRET");
    }
}
EOF

# Both methods are corroborated, so the generated file and method allowlists
# must be empty apart from their comments and the gate must pass.
"$WORK/scripts/check-redaction-oracles.sh" --update >/dev/null
INITIAL="$WORK/initial.log"
if ! "$WORK/scripts/check-redaction-oracles.sh" >"$INITIAL" 2>&1; then
  cat "$INITIAL" >&2
  fail "a file with two independently checked methods should pass"
fi

# MUTATION: only the first method loses mutool. The second keeps it, so the
# original file-level gate still passes; the #1077 method gate MUST be red.
sed -i.bak '1,/MutoolTextExtractor/s/MutoolTextExtractor/InternalTextExtractor/' \
  "$WORK/Excise.Core.Tests/MethodOracleRedactionTests.cs"
rm -f "$WORK/Excise.Core.Tests/MethodOracleRedactionTests.cs.bak"

OUT="$WORK/mutation.log"
if "$WORK/scripts/check-redaction-oracles.sh" >"$OUT" 2>&1; then
  cat "$OUT" >&2
  cat "$WORK/Excise.Core.Tests/MethodOracleRedactionTests.cs" >&2
  fail "stripping one method's oracle passed despite a sibling retaining mutool"
fi

grep -qF \
  'Excise.Core.Tests/MethodOracleRedactionTests.cs::FirstLeakAssertion_HasItsOwnIndependentOracle' \
  "$OUT" || fail "the method-level failure did not name the mutated method"

if grep -qF 'redaction test file(s) with NO independent oracle' "$OUT"; then
  fail "the mutation tripped only the old file-level gate, not #1077's method gate"
fi

echo "PASS: per-method oracle gate detects one mutated assertion in a still-corroborated file (#1077)"
