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

# ── #1786: the local-variable shape ─────────────────────────────────────────
#
# check-redaction-oracles.sh's method gate used to require the self-oracle
# READ and the leak ASSERTION on the very same line (`.Text.Should(...)`).
# RedactionRoundTripTests.cs's original self-oracle theory assigned the
# extraction to a local (`remaining`), derived a bool from it (`stillContains
# Target`), and asserted on THAT a few lines later -- neither line alone
# matched the chained pattern, so the method was invisible to the gate: not
# allow-listed, not failing, unseen. Three stages, same method body evolving:
# local-variable shape -> red; rewritten as the old chained shape -> still
# red (the widening must not have narrowed the original detection); a mutool
# assertion added -> green.
rm -f "$WORK/Excise.Core.Tests/MethodOracleRedactionTests.cs"

cat > "$WORK/Excise.Core.Tests/LocalVariableOracleRedactionTests.cs" <<'EOF'
using Xunit;

public sealed class LocalVariableOracleRedactionTests
{
    [Fact]
    public void LeakAssertion_ThroughALocalVariable()
    {
        var remaining = reopened.GetPage(1).Text;
        var stillContainsTarget = remaining.Contains("SECRET");
        stillContainsTarget.Should().BeFalse("SECURITY: redacted text leaked");
    }
}
EOF

STAGE1="$WORK/stage1-local-variable.log"
if "$WORK/scripts/check-redaction-oracles.sh" >"$STAGE1" 2>&1; then
  cat "$STAGE1" >&2
  fail "#1786: a self-oracle read assigned to a local, asserted on several lines later, " \
       "with no independent oracle anywhere in the method, must fail -- it did not"
fi
grep -qF \
  'Excise.Core.Tests/LocalVariableOracleRedactionTests.cs::LeakAssertion_ThroughALocalVariable' \
  "$STAGE1" || fail "#1786: the local-variable self-oracle method was not named in the failure"

# Stage 2: the SAME assertion, written the old chained way. Must still fail --
# proves the widened detector did not accidentally narrow the original one.
cat > "$WORK/Excise.Core.Tests/LocalVariableOracleRedactionTests.cs" <<'EOF'
using Xunit;

public sealed class LocalVariableOracleRedactionTests
{
    [Fact]
    public void LeakAssertion_ThroughALocalVariable()
    {
        reopened.GetPage(1).Text.Should().NotContain("SECRET");
    }
}
EOF

STAGE2="$WORK/stage2-chained.log"
if "$WORK/scripts/check-redaction-oracles.sh" >"$STAGE2" 2>&1; then
  cat "$STAGE2" >&2
  fail "#1786: the chained self-oracle shape (pre-existing detection) regressed"
fi
grep -qF \
  'Excise.Core.Tests/LocalVariableOracleRedactionTests.cs::LeakAssertion_ThroughALocalVariable' \
  "$STAGE2" || fail "#1786: the chained self-oracle method was not named in the failure"

# Stage 3: add a mutool assertion in the SAME method. Must go green.
cat > "$WORK/Excise.Core.Tests/LocalVariableOracleRedactionTests.cs" <<'EOF'
using Xunit;

public sealed class LocalVariableOracleRedactionTests
{
    [Fact]
    public void LeakAssertion_ThroughALocalVariable()
    {
        var remaining = reopened.GetPage(1).Text;
        var stillContainsTarget = remaining.Contains("SECRET");
        stillContainsTarget.Should().BeFalse("SECURITY: redacted text leaked");
        MutoolTextExtractor.ExtractPage("output.pdf", 1).Should().NotContain("SECRET");
    }
}
EOF

STAGE3="$WORK/stage3-with-mutool.log"
if ! "$WORK/scripts/check-redaction-oracles.sh" >"$STAGE3" 2>&1; then
  cat "$STAGE3" >&2
  fail "#1786: a local-variable self-oracle read WITH a mutool assertion in the same " \
       "method must pass -- an independent oracle anywhere in the method corroborates it"
fi

echo "PASS: local-variable self-oracle assertions are caught, the pre-existing chained " \
     "shape is not regressed, and a corroborated method still passes (#1786)"
