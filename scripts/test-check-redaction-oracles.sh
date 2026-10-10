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

# #1778: the gate also scans test files OUTSIDE the name glob that call a
# redaction entry point, and refuses to run when that scan finds none (a
# broken REDACT_CALL pattern must not read as a clean run). Seed one
# corroborated, workflow-named file so the baseline stages below are green.
mkdir -p "$WORK/Excise.App.Tests/UI"
WORKFLOW="$WORK/Excise.App.Tests/UI/OpenEditSaveWorkflowTests.cs"
cat > "$WORKFLOW" <<'EOF'
using Xunit;

public sealed class OpenEditSaveWorkflowTests
{
    [Fact]
    public void Workflow_RedactsAndIsCorroborated()
    {
        doc.RedactText("SECRET", RedactionOptions.Default);
        reopened.GetPage(1).Text.Should().NotContain("SECRET");
        SavedPdfLeakScanner.FindTerm(savedBytes, "SECRET").Should().BeEmpty();
    }
}
EOF

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

# ── #1778: content scope, and token presence is not use ─────────────────────
#
# The name-scoped population missed ScriptedGuiTests.cs and GoldenPathTests.cs
# (named after the workflow, not the feature), and a method passed because it
# MENTIONED SavedPdfLeakScanner inside a loop that ran zero times. Each stage
# rewrites the workflow-named file, which no name glob matches, and asserts
# the verdict. The #1786 stage-3 file (corroborated) stays as the name-scoped
# population, which has its own zero-file floor.

expect_red() {
  local log="$WORK/$1.log"
  if "$WORK/scripts/check-redaction-oracles.sh" >"$log" 2>&1; then
    cat "$log" >&2; cat "$WORKFLOW" >&2
    fail "#1778 $1: $3 -- the gate stayed green"
  fi
  grep -qF "Excise.App.Tests/UI/OpenEditSaveWorkflowTests.cs::$2" "$log" \
    || { cat "$log" >&2; fail "#1778 $1: the failure did not name $2"; }
}
expect_green() {
  local log="$WORK/$1.log"
  if ! "$WORK/scripts/check-redaction-oracles.sh" >"$log" 2>&1; then
    cat "$log" >&2; cat "$WORKFLOW" >&2
    fail "#1778 $1: $2 -- the gate went red"
  fi
}
write_workflow() {
  { echo 'using Xunit;'; echo; echo 'public sealed class OpenEditSaveWorkflowTests'; echo '{'
    echo '    [Fact]'; echo '    public void Workflow_Redacts()'; echo '    {'
    cat
    echo '    }'; echo '}'; } > "$WORKFLOW"
}

# (a) a workflow-named file that redacts and checks removal only with excise.
write_workflow <<'EOF'
        doc.RedactText("SECRET", RedactionOptions.Default);
        reopened.GetPage(1).Text.Should().NotContain("SECRET");
EOF
expect_red content-scope Workflow_Redacts \
  "a file outside the name glob that redacts and asserts removal only through excise must fail"

# (b) the only independent mention is inside a loop over a collection the
# gate cannot see is non-empty (the RedactionMouseWorkflowTests shape) ...
write_workflow <<'EOF'
        doc.RedactText("SECRET", RedactionOptions.Default);
        reopened.GetPage(1).Text.Should().NotContain("SECRET");
        foreach (var term in scenario.SavedBytesMustNotContain)
        {
            SavedPdfLeakScanner.FindTerm(savedBytes, term).Should().BeEmpty();
        }
EOF
expect_red loop-only Workflow_Redacts \
  "an independent oracle that runs only inside a possibly zero-trip foreach must not corroborate"

# ... a brace-less loop body is the same loop ...
write_workflow <<'EOF'
        doc.RedactText("SECRET", RedactionOptions.Default);
        reopened.GetPage(1).Text.Should().NotContain("SECRET");
        foreach (var term in scenario.SavedBytesMustNotContain)
            SavedPdfLeakScanner.FindTerm(savedBytes, term).Should().BeEmpty();
EOF
expect_red loop-only-braceless Workflow_Redacts "a brace-less foreach body is still a loop body"

# ... while the same call outside the loop corroborates.
write_workflow <<'EOF'
        doc.RedactText("SECRET", RedactionOptions.Default);
        reopened.GetPage(1).Text.Should().NotContain("SECRET");
        foreach (var term in scenario.SavedBytesMustNotContain)
        {
            Log(term);
        }
        SavedPdfLeakScanner.FindTerm(savedBytes, "SECRET").Should().BeEmpty();
EOF
expect_green loop-then-direct "an unlooped independent assertion after a loop corroborates"

# (c) an independent oracle named only in a comment, or only in a string.
write_workflow <<'EOF'
        doc.RedactText("SECRET", RedactionOptions.Default);
        // MutoolTextExtractor would catch a leak here
        reopened.GetPage(1).Text.Should().NotContain("SECRET");
EOF
expect_red comment-only Workflow_Redacts "an oracle named only in a comment is not an oracle"

write_workflow <<'EOF'
        doc.RedactText("SECRET", RedactionOptions.Default);
        reopened.GetPage(1).Text.Should().NotContain("SECRET", "MutoolTextExtractor agrees");
EOF
expect_red string-only Workflow_Redacts "an oracle named only in a string literal is not an oracle"

# (d) a loop over an inline, visibly non-empty literal does run its body.
write_workflow <<'EOF'
        foreach (var options in new[] { RedactionOptions.Default, RedactionOptions.Maximum })
        {
            doc.RedactText("SECRET", options);
            reopened.GetPage(1).Text.Should().NotContain("SECRET");
            SavedPdfLeakScanner.FindTerm(savedBytes, "SECRET").Should().BeEmpty();
        }
EOF
expect_green literal-loop "a foreach over an inline non-empty array literal runs its oracle"

# (e) a content-scoped method that redacts but makes no absence claim (a
# pre-redaction extraction check) is out of scope by rule, not by allowlist.
write_workflow <<'EOF'
        doc.GetPage(1).Text.Should().Be("Name SECRET");
        doc.RedactText("SECRET", RedactionOptions.Default).VerifiedRemovals.Should().BeGreaterThan(0);
EOF
expect_green no-absence-claim "a content-scoped method with no leak-shaped assertion has nothing to corroborate"

# (e2) the GUI entry points count as redaction calls: the three #1769
# ScriptedGuiTests methods redacted through RedactTextCommand, not RedactText.
write_workflow <<'EOF'
        await viewModel.RedactTextCommand("SECRET");
        reopened.GetPage(1).Text.Should().NotContain("SECRET");
EOF
expect_red gui-entry-point Workflow_Redacts \
  "a method redacting through the view-model commands and asserting only through excise must fail"

# (f) the content scan cannot silently shrink to nothing.
rm -f "$WORKFLOW"
OUT_F="$WORK/content-empty.log"
if "$WORK/scripts/check-redaction-oracles.sh" >"$OUT_F" 2>&1; then
  cat "$OUT_F" >&2
  fail "#1778: zero content-scoped files must fail, not read as a clean run"
fi
grep -qF 'ZERO test files outside the name glob' "$OUT_F" \
  || { cat "$OUT_F" >&2; fail "#1778: the empty content scan failed for the wrong reason"; }

echo "PASS: workflow-named files are scanned; looped, comment-only and string-only oracle" \
     "mentions do not corroborate; literal loops and no-claim methods stay green (#1778)"
