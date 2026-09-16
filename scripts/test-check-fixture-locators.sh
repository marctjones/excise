#!/usr/bin/env bash
#
# Regression test for the #1527 fixture-locator guard.
#
# Same standalone-reproduction convention as scripts/test-check-skip-budget.sh:
# copy the real script into a synthetic repo under a temp directory and run it
# against hand-built cases, so the gate's behaviour is pinned without a build
# and without the real repo's contents.
#
# The cases are the three things that had to be true for #1527 to ship:
# a bounded upward walk, '..'-counting from the working directory, and a
# corpus-gated theory class with no declared collected-row floor.
#
# Usage: scripts/test-check-fixture-locators.sh
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

FAIL=0

# Build a minimal synthetic repo: the shared locator, the floor registry, and
# one test project. $1 is the sandbox directory to create.
make_repo() {
  local repo="$1"
  mkdir -p "$repo/scripts" \
           "$repo/Excise.Core.Tests/TestSupport" \
           "$repo/Excise.Rendering.Tests/Differential"
  cp "$HERE/check-fixture-locators.sh" "$repo/scripts/check-fixture-locators.sh"
  chmod +x "$repo/scripts/check-fixture-locators.sh"

  cat > "$repo/Excise.Core.Tests/TestSupport/TestRepoLayout.cs" <<'EOF'
namespace Excise.TestSupport;
internal static class TestRepoLayout { }
EOF

  cat > "$repo/Excise.Rendering.Tests/Differential/CorpusRowFloorGateTests.cs" <<'EOF'
using Excise.TestSupport;
namespace Excise.Rendering.Tests.Differential;
public class CorpusRowFloorGateTests
{
    // registry: "RegisteredGateTests"
    // resolves via TestRepoLayout
}
EOF
}

# A clean, correctly-written corpus-gated gate: registered above, and resolving
# through the shared locator rather than a walk of its own.
write_registered_gate() {
  cat > "$1/Excise.Rendering.Tests/Differential/RegisteredGateTests.cs" <<'EOF'
using Excise.TestSupport;
namespace Excise.Rendering.Tests.Differential;
public class RegisteredGateTests
{
    public static TheoryData<string> Fixtures()
    {
        var data = new TheoryData<string>();
        var dir = TestRepoLayout.FindDirectory("test-pdfs/smoke");
        if (dir != null)
            foreach (var f in Directory.EnumerateFiles(dir, "*.pdf")) data.Add(f);
        return data;
    }
}
EOF
}

run_gate() { # run_gate <repo> <outfile>  -> sets RC
  RC=0
  "$1/scripts/check-fixture-locators.sh" >"$2" 2>&1 || RC=$?
}

# ---------------------------------------------------------------------------
# 1. A clean repo passes.
# ---------------------------------------------------------------------------
CLEAN="$WORK/clean"
make_repo "$CLEAN"
write_registered_gate "$CLEAN"
run_gate "$CLEAN" "$WORK/clean.log"
[[ "$RC" -eq 0 ]] || { echo "FAIL: gate rejected a clean repo"; cat "$WORK/clean.log"; FAIL=1; }
grep -qF "fixture-locator guard OK" "$WORK/clean.log" || { echo "FAIL: clean repo did not report OK"; cat "$WORK/clean.log"; FAIL=1; }
grep -qF "1 corpus-gated theory class(es) found" "$WORK/clean.log" || { echo "FAIL: gate miscounted the corpus-gated classes"; cat "$WORK/clean.log"; FAIL=1; }

# ---------------------------------------------------------------------------
# 2. A BOUNDED upward walk fails and is named. Both shipped shapes: the
#    bound-8 "i < 8 && dir != null" family and the bound-6 "up < 6" trio.
# ---------------------------------------------------------------------------
for shape in 'for (var i = 0; i < 8 && dir != null; i++)' 'for (var up = 0; up < 6; up++)'; do
  BOUND="$WORK/bounded-$RANDOM"
  make_repo "$BOUND"
  write_registered_gate "$BOUND"
  cat > "$BOUND/Excise.Rendering.Tests/Differential/HandRolledTests.cs" <<EOF
namespace Excise.Rendering.Tests.Differential;
public class HandRolledTests
{
    private static string? Resolve(string rel)
    {
        var dir = AppContext.BaseDirectory;
        $shape
        {
            var c = Path.Combine(dir, rel);
            if (File.Exists(c)) return c;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
}
EOF
  run_gate "$BOUND" "$WORK/bounded.log"
  [[ "$RC" -ne 0 ]] || { echo "FAIL: gate accepted a bounded upward walk ($shape)"; cat "$WORK/bounded.log"; FAIL=1; }
  grep -qF "HandRolledTests.cs" "$WORK/bounded.log" || { echo "FAIL: gate did not name the file with the bounded walk ($shape)"; cat "$WORK/bounded.log"; FAIL=1; }
  grep -qF "BOUNDED upward directory walk" "$WORK/bounded.log" || { echo "FAIL: gate did not diagnose the bounded walk ($shape)"; cat "$WORK/bounded.log"; FAIL=1; }
done

# ---------------------------------------------------------------------------
# 3. '..'-counting from the working directory fails and is named. This is the
#    anchor the bound-6 trio used, and using a DIFFERENT anchor from the
#    bound-8 family is why the two groups needed different depths and why
#    nobody reviewing one saw the other.
# ---------------------------------------------------------------------------
DOTDOT="$WORK/dotdot"
make_repo "$DOTDOT"
write_registered_gate "$DOTDOT"
cat > "$DOTDOT/Excise.Rendering.Tests/Differential/DotDotTests.cs" <<'EOF'
namespace Excise.Rendering.Tests.Differential;
public class DotDotTests
{
    private static string? Resolve(string rel)
    {
        var p = Path.GetFullPath(Path.Combine(Enumerable.Repeat("..", 4).DefaultIfEmpty(".").Aggregate(Path.Combine), rel));
        return Directory.Exists(p) ? p : null;
    }
}
EOF
run_gate "$DOTDOT" "$WORK/dotdot.log"
[[ "$RC" -ne 0 ]] || { echo "FAIL: gate accepted hand-rolled '..' counting"; cat "$WORK/dotdot.log"; FAIL=1; }
grep -qF "DotDotTests.cs" "$WORK/dotdot.log" || { echo "FAIL: gate did not name the '..'-counting file"; cat "$WORK/dotdot.log"; FAIL=1; }

# ---------------------------------------------------------------------------
# 4. THE #1527 CASE: a corpus-gated theory class with no declared
#    collected-row floor. It has no bounded walk and no '..' counting — it is
#    written correctly — and it still fails, because nothing would notice if
#    its rows stopped materialising. The population is DERIVED here rather
#    than listed, which is what turned up the fourth real gate
#    (ReferenceRedactorComparisonTests) that the first hand-written registry
#    missed.
# ---------------------------------------------------------------------------
UNREG="$WORK/unregistered"
make_repo "$UNREG"
write_registered_gate "$UNREG"
cat > "$UNREG/Excise.Rendering.Tests/Differential/ForgottenGateTests.cs" <<'EOF'
using Excise.TestSupport;
namespace Excise.Rendering.Tests.Differential;
public class ForgottenGateTests
{
    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        var dir = TestRepoLayout.FindDirectory("test-pdfs/federal");
        if (dir != null)
            foreach (var f in Directory.EnumerateFiles(dir, "*.pdf")) data.Add(f);
        return data;
    }
}
EOF
run_gate "$UNREG" "$WORK/unreg.log"
[[ "$RC" -ne 0 ]] || { echo "FAIL: gate accepted a corpus-gated theory class with no row floor"; cat "$WORK/unreg.log"; FAIL=1; }
grep -qF "+ ForgottenGateTests" "$WORK/unreg.log" || { echo "FAIL: gate did not name the unregistered corpus-gated class"; cat "$WORK/unreg.log"; FAIL=1; }
grep -qF "never COLLECTED" "$WORK/unreg.log" || { echo "FAIL: gate did not explain the never-collected failure mode"; cat "$WORK/unreg.log"; FAIL=1; }
if grep -qF "+ RegisteredGateTests" "$WORK/unreg.log"; then
  echo "FAIL: gate wrongly flagged the class that IS registered"; FAIL=1
fi

# ---------------------------------------------------------------------------
# 5. A theory class over a TRACKED fixture directory is not corpus-gated and
#    needs no floor — test-pdfs/sample-pdfs is in git, so it is present in
#    every checkout and its rows cannot vanish with a corpus. Over-reach here
#    would push people to register everything, which is how a registry stops
#    meaning anything.
# ---------------------------------------------------------------------------
TRACKED="$WORK/tracked"
make_repo "$TRACKED"
write_registered_gate "$TRACKED"
cat > "$TRACKED/Excise.Rendering.Tests/Differential/TrackedFixtureTests.cs" <<'EOF'
using Excise.TestSupport;
namespace Excise.Rendering.Tests.Differential;
public class TrackedFixtureTests
{
    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        var dir = TestRepoLayout.FindDirectory("test-pdfs/sample-pdfs");
        if (dir != null)
            foreach (var f in Directory.EnumerateFiles(dir, "*.pdf")) data.Add(f);
        return data;
    }
}
EOF
run_gate "$TRACKED" "$WORK/tracked.log"
[[ "$RC" -eq 0 ]] || { echo "FAIL: gate demanded a row floor for a TRACKED fixture directory"; cat "$WORK/tracked.log"; FAIL=1; }

# ---------------------------------------------------------------------------
# 6. A missing shared locator is a hard failure — the fix is one locator, not
#    a larger bound, so the gate must not pass a repo that has no locator to
#    point people at.
# ---------------------------------------------------------------------------
NOLAYOUT="$WORK/no-layout"
make_repo "$NOLAYOUT"
write_registered_gate "$NOLAYOUT"
rm "$NOLAYOUT/Excise.Core.Tests/TestSupport/TestRepoLayout.cs"
run_gate "$NOLAYOUT" "$WORK/nolayout.log"
[[ "$RC" -ne 0 ]] || { echo "FAIL: gate passed with the shared locator missing"; cat "$WORK/nolayout.log"; FAIL=1; }
grep -qF "shared fixture locator is missing" "$WORK/nolayout.log" || { echo "FAIL: missing-locator failure was not diagnosed"; cat "$WORK/nolayout.log"; FAIL=1; }

# ---------------------------------------------------------------------------
# 7. A registry that does not go through the shared locator fails. The floor
#    gate's whole value is that it answers "is the corpus reachable"
#    INDEPENDENTLY of the classes it is checking; a registry that re-derived
#    the answer could inherit their bug and agree with it.
# ---------------------------------------------------------------------------
BADREG="$WORK/bad-registry"
make_repo "$BADREG"
write_registered_gate "$BADREG"
cat > "$BADREG/Excise.Rendering.Tests/Differential/CorpusRowFloorGateTests.cs" <<'EOF'
namespace Excise.Rendering.Tests.Differential;
public class CorpusRowFloorGateTests
{
    // registry: "RegisteredGateTests" — but resolves nothing through the shared locator
}
EOF
run_gate "$BADREG" "$WORK/badreg.log"
[[ "$RC" -ne 0 ]] || { echo "FAIL: gate accepted a registry that does not use TestRepoLayout"; cat "$WORK/badreg.log"; FAIL=1; }

if [[ $FAIL -ne 0 ]]; then
  exit 1
fi

echo "PASS: check-fixture-locators.sh (#1527) fails on a bounded upward walk, on"
echo "      hand-rolled '..' counting, on a corpus-gated theory class with no"
echo "      declared collected-row floor, on a missing shared locator and on a"
echo "      registry that resolves corpora itself — and passes a clean repo and a"
echo "      theory over a TRACKED fixture directory."
