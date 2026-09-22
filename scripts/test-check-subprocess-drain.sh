#!/usr/bin/env bash
#
# Regression test for the #1068/#1516/#1773 subprocess-drain guard.
#
# Same standalone-reproduction convention as scripts/test-check-fixture-locators.sh:
# copy the real script into a synthetic repo under a temp directory, PLANT the
# violation the gate exists to catch, and watch it go RED; remove the plant and
# watch it go GREEN. A gate that has never been seen red is not accepted (#1012).
#
# Usage: scripts/test-check-subprocess-drain.sh
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

FAIL=0
RC=0

make_repo() { # make_repo <repo>
  local repo="$1"
  mkdir -p "$repo/scripts" "$repo/Excise.App.Tests/Unit"
  cp "$HERE/check-subprocess-drain.sh" "$repo/scripts/check-subprocess-drain.sh"
  chmod +x "$repo/scripts/check-subprocess-drain.sh"
  # A correctly drained helper: ReadToEndAsync on both pipes, bounded wait.
  cat > "$repo/Excise.App.Tests/Unit/DrainedTests.cs" <<'CS'
namespace Excise.App.Tests.Unit;
public class DrainedTests
{
    static string Run(System.Diagnostics.Process p)
    {
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        p.WaitForExit(30_000);
        return o.Result + e.Result;
    }
}
CS
}

run_gate() { # run_gate <repo> <log>  -> sets RC
  RC=0
  "$1/scripts/check-subprocess-drain.sh" >"$2" 2>&1 || RC=$?
}

expect_green() { # expect_green <label> <repo> <log>
  run_gate "$2" "$3"
  if [[ "$RC" -ne 0 ]]; then echo "FAIL: $1: expected GREEN, gate exited $RC"; cat "$3"; FAIL=1; fi
}

expect_red() { # expect_red <label> <repo> <log> <needle-in-output>
  run_gate "$2" "$3"
  if [[ "$RC" -eq 0 ]]; then echo "FAIL: $1: expected RED, gate accepted the planted violation"; cat "$3"; FAIL=1; return; fi
  grep -qF -- "$4" "$3" || { echo "FAIL: $1: gate went red but did not name '$4'"; cat "$3"; FAIL=1; }
}

# ---------------------------------------------------------------------------
# 1. A clean repo is GREEN (and ReadToEndAsync is not mistaken for ReadToEnd).
# ---------------------------------------------------------------------------
R="$WORK/repo"
make_repo "$R"
expect_green "clean repo" "$R" "$WORK/1.log"
grep -qF "subprocess-drain guard OK" "$WORK/1.log" || { echo "FAIL: clean repo did not report OK"; cat "$WORK/1.log"; FAIL=1; }

# ---------------------------------------------------------------------------
# 2. Plant each shape, see RED naming file:line, remove it, see GREEN.
# ---------------------------------------------------------------------------
PLANT="$R/Excise.App.Tests/Unit/PlantedTests.cs"
i=0
for shape in 'var t = proc.StandardOutput.ReadToEnd();' \
             'var e = proc.StandardError.ReadToEnd();' \
             'var w = proc.StandardOutput . ReadToEnd ( );'; do
  i=$((i + 1))
  printf 'namespace Excise.App.Tests.Unit;\npublic class PlantedTests\n{\n    void M(System.Diagnostics.Process proc)\n    {\n        %s\n    }\n}\n' "$shape" > "$PLANT"
  expect_red "planted shape $i" "$R" "$WORK/red$i.log" "Excise.App.Tests/Unit/PlantedTests.cs:6"
  rm "$PLANT"
  expect_green "after removing planted shape $i" "$R" "$WORK/green$i.log"
done

# ---------------------------------------------------------------------------
# 3. Build output is not source: a hit under obj/ or bin/ is ignored.
# ---------------------------------------------------------------------------
mkdir -p "$R/Excise.App.Tests/obj/Debug"
echo 'var t = proc.StandardOutput.ReadToEnd();' > "$R/Excise.App.Tests/obj/Debug/Generated.cs"
expect_green "a hit under obj/" "$R" "$WORK/obj.log"
rm -r "$R/Excise.App.Tests/obj"

# ---------------------------------------------------------------------------
# 4. A vacuous scan is a failure: no project, or a project with no sources.
# ---------------------------------------------------------------------------
E="$WORK/empty"
make_repo "$E"
rm "$E/Excise.App.Tests/Unit/DrainedTests.cs"
expect_red "a project with no sources" "$E" "$WORK/empty.log" "found no C# sources"

N="$WORK/noproject"
make_repo "$N"
rm -r "$N/Excise.App.Tests"
expect_red "a missing project" "$N" "$WORK/noproject.log" "does not exist"

if [[ "$FAIL" -ne 0 ]]; then
  echo "test-check-subprocess-drain.sh: FAILED"
  exit 1
fi
echo "test-check-subprocess-drain.sh: OK (3 planted shapes RED then GREEN; obj/ ignored; vacuous scans refused)"
