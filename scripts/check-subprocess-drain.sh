#!/usr/bin/env bash
#
# Subprocess-drain discipline guard (#1068, #1516, #1773).
#
# #1068 closed a hang where a test shelled out to `dotnet list package`: MSBuild
# left node-reuse workers alive holding the inherited stdout handle, so the pipe
# never reached EOF. WaitForExit(60_000) returned TRUE - the child really had
# exited - and the ReadToEnd on the next line blocked forever. The dangerous
# half of that pattern is the READ, not the wait: a synchronous
# StandardOutput.ReadToEnd() / StandardError.ReadToEnd() on a redirected child
# stream can block with no bound, and xUnit's Timeout cannot abort a thread
# blocked in a synchronous native read.
#
# Why this belongs in t0 and not in the Excise.App.Tests host: those helpers run
# from [FixedAvaloniaFact] bodies, i.e. on the single Avalonia headless
# dispatcher thread, so ONE blocked read wedges every remaining Avalonia test in
# the process - a 0%-CPU stall, not one failing test. The check used to be a
# test inside that 8 GB, ~19-minute host, so it only ran at t1 (#1773). It reads
# source text and needs no build, no Avalonia and no document.
#
# Scope is deliberately Excise.App.Tests only, exactly what the test covered.
# The remaining synchronous reads live in Excise.Core.Tests, Excise.Rendering.Tests,
# Excise.Rendering/Differential, Excise.Cli.Tests and tools/Excise.Reachability
# and are enumerated in #1516; this repo keeps no external violation allowlist
# (#1172 removed the last one), so the gate covers what is actually green and
# grows as those are fixed.
#
# Usage: scripts/check-subprocess-drain.sh
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

PROJECT="Excise.App.Tests"

if [ ! -d "$PROJECT" ]; then
  echo "FAIL: $PROJECT does not exist; an empty scan is a vacuous pass"
  exit 1
fi

SOURCES=()
while IFS= read -r f; do SOURCES+=("$f"); done < <(
  find "$PROJECT" -type f -name '*.cs' -not -path '*/obj/*' -not -path '*/bin/*' | LC_ALL=C sort
)
if [ ${#SOURCES[@]} -eq 0 ]; then
  echo "FAIL: found no C# sources under $PROJECT to scan"
  exit 1
fi

# Whitespace-tolerant: `StandardOutput . ReadToEnd ( )` is the same call.
HITS=$(grep -nE 'Standard(Output|Error)[[:space:]]*\.[[:space:]]*ReadToEnd[[:space:]]*\([[:space:]]*\)' \
  "${SOURCES[@]}" || true)

if [ -n "$HITS" ]; then
  echo
  echo "FAIL: a synchronous ReadToEnd() on a redirected child-process stream (#1068, #1516)."
  echo "      It can block forever, and xUnit's Timeout cannot abort it. Drain BOTH pipes"
  echo "      concurrently and bound the wait instead: start ReadToEndAsync() on stdout AND"
  echo "      stderr, then WaitForExit(ms), then Kill(entireProcessTree: true) if it did not"
  echo "      exit. Working examples: Unit/CopyReadingOrderTests.RunPdftotext and"
  echo "      Unit/SignatureApplicationServiceTests. Offending lines:"
  echo
  echo "$HITS" | sed 's/^/  /'
  exit 1
fi

echo "==> subprocess-drain guard OK (${#SOURCES[@]} sources under $PROJECT, no synchronous stream read)"
