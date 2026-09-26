#!/usr/bin/env bash
# Verify the default Release package keeps heavy optional subsystems out of the
# normal startup path (#341): Roslyn scripting is not shipped, repo-local
# tessdata is not bundled unless requested, and building the view model, opening
# a document and the structural hidden-text reveal do not load Excise.Ocr before
# the user asks for the rasterized scan, which does (#1780).

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PUBLISH_DIR="${1:-/tmp/excise-release-lazy-startup}"

cd "$ROOT"
rm -rf "$PUBLISH_DIR"
mkdir -p "$PUBLISH_DIR"

echo "==> Publishing default Release GUI to $PUBLISH_DIR"
dotnet publish Excise.App/Excise.App.csproj \
    -c Release \
    -o "$PUBLISH_DIR" \
    -p:DebugType=None \
    -p:DebugSymbols=false

# Both checks below assert an ABSENCE, and an absence is trivially true of an
# empty directory. If the publish ever lands somewhere other than $PUBLISH_DIR,
# every "is X absent?" check passes having inspected nothing (#941). Prove the
# output is really here before concluding anything from what is not.
if [[ ! -f "$PUBLISH_DIR/Excise.App.dll" ]]; then
    echo "ERROR: no Excise.App.dll in $PUBLISH_DIR — the publish produced nothing here," >&2
    echo "       so the absence checks below would pass over an empty directory." >&2
    exit 1
fi

echo "==> Checking Roslyn scripting is absent from default Release output"
if find "$PUBLISH_DIR" -name 'Microsoft.CodeAnalysis*.dll' -print -quit | grep -q .; then
    echo "ERROR: Microsoft.CodeAnalysis assemblies were published unexpectedly" >&2
    find "$PUBLISH_DIR" -name 'Microsoft.CodeAnalysis*.dll' >&2
    exit 1
fi

deps="$PUBLISH_DIR/Excise.App.deps.json"
if [[ -f "$deps" ]] && grep -q 'Microsoft.CodeAnalysis.CSharp.Scripting' "$deps"; then
    echo "ERROR: Excise.App.deps.json contains Microsoft.CodeAnalysis.CSharp.Scripting" >&2
    exit 1
fi

echo "==> Checking tessdata is absent from default Release output"
if find "$PUBLISH_DIR" \( -path '*/tessdata/*' -o -name '*.traineddata' \) -print -quit | grep -q .; then
    echo "ERROR: tessdata/traineddata files were published unexpectedly" >&2
    find "$PUBLISH_DIR" \( -path '*/tessdata/*' -o -name '*.traineddata' \) >&2
    exit 1
fi

echo "==> Checking startup and the structural reveal do not load Excise.Ocr, and the rasterized scan does"
# `dotnet test --filter` EXITS 0 WHEN IT MATCHES NOTHING, and when the test
# skips. Renaming this one test would print "OK: lazy-startup verification
# passed" having asserted nothing about OCR assembly loading (#941). The test
# skips when Excise.Ocr is already loaded in its process, which alone in a fresh
# process means startup itself loaded it: a skip is red here, not a pass.
# `tee`, not capture-and-echo: Excise.App.Tests is serial and slow to start, so
# a silent gate reads as a hung one.
RUN_LOG="$(mktemp)"
trap 'rm -f "$RUN_LOG"' EXIT
set +e
dotnet test Excise.App.Tests/Excise.App.Tests.csproj \
    --filter "FullyQualifiedName~OcrStack_IsNotLoadedByStartupOrStructuralReveal_ButIsByTheRasterizedScan" \
    --logger "console;verbosity=minimal" 2>&1 | tee "$RUN_LOG"
run_status=${PIPESTATUS[0]}
set -e
run_output="$(cat "$RUN_LOG")"

if grep -q "No test matches the given testcase filter" <<<"$run_output"; then
    echo
    echo "ERROR: the filter matched NO tests — OcrStack_IsNotLoadedByStartupOrStructuralReveal_" >&2
    echo "       ButIsByTheRasterizedScan was renamed, moved, or removed (#1780). This" >&2
    echo "       gate would otherwise report green having verified nothing." >&2
    exit 1
fi
[[ $run_status -eq 0 ]] || exit $run_status
if ! grep -Eq "Passed: +1, Skipped: +0, Total: +1" <<<"$run_output"; then
    echo
    echo "ERROR: the OCR lazy-load test did not run and pass exactly once (skipped, or the" >&2
    echo "       summary format changed). Alone in a fresh process a skip means Excise.Ocr was" >&2
    echo "       already loaded before the test began (#1780)." >&2
    exit 1
fi

echo "OK: lazy-startup verification passed"
