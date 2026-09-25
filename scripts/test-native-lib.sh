#!/usr/bin/env bash
#
# Build the Excise.Native shared library, then drive it from OUTSIDE .NET: a C program
# (Excise.Native/tests/api_test.c, dlopen) and a Python ctypes suite
# (Excise.Native/tests/test_ctypes.py). Fails if either runs zero tests or the corpus
# PDF is missing (a vacuous pass is a failure). POSIX only (dlopen).
#
# Usage: scripts/test-native-lib.sh [--rid RID] [--no-build]
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

BUILD_ARGS=()
DO_BUILD=1
while [ $# -gt 0 ]; do
  case "$1" in
    --rid) BUILD_ARGS+=(--rid "${2:?--rid needs a value}"); shift 2 ;;
    --no-build) DO_BUILD=0; shift ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

if [ "$DO_BUILD" = 1 ]; then
  LIB="$(scripts/build-native-lib.sh ${BUILD_ARGS[@]+"${BUILD_ARGS[@]}"} | tee /dev/stderr | tail -1)"
else
  LIB="$(ls dist/native/*/libexcise_native.* | head -1)"
fi
[ -f "$LIB" ] || { echo "FAIL: library not found: $LIB" >&2; exit 1; }
LIB="$(cd "$(dirname "$LIB")" && pwd)/$(basename "$LIB")"

CORPUS="${EXCISE_TEST_PDFS:-$ROOT/test-pdfs/smoke}"
PDF="$CORPUS/irs-w9.pdf"
if [ ! -f "$PDF" ]; then
  echo "FAIL: $PDF is missing. Fetch the smoke corpus (scripts/check-test-prereqs.sh shows what exists)" >&2
  echo "      or set EXCISE_TEST_PDFS to a directory containing irs-w9.pdf." >&2
  exit 1
fi

SCRATCH="$(mktemp -d "${TMPDIR:-/tmp}/excise-native-test.XXXXXX")"
trap 'rm -rf "$SCRATCH"' EXIT

echo "== C: api_test =="
clang -std=c11 -Wall -Wextra -Werror -Wno-unused-parameter -o "$SCRATCH/api_test" Excise.Native/tests/api_test.c -ldl
C_OUT="$("$SCRATCH/api_test" "$LIB" "$PDF" Taxpayer "$SCRATCH")"
echo "$C_OUT"
C_N="$(printf '%s\n' "$C_OUT" | sed -n 's/^PASSED \([0-9][0-9]*\)$/\1/p')"
[ -n "$C_N" ] && [ "$C_N" -gt 0 ] || { echo "FAIL: the C test ran zero checks" >&2; exit 1; }

echo "== Python: test_ctypes =="
PY_LOG="$SCRATCH/py.log"
set +e
EXCISE_NATIVE_LIB="$LIB" EXCISE_TEST_PDFS="$CORPUS" python3 -m unittest discover -v \
  -s Excise.Native/tests -p 'test_ctypes.py' >"$PY_LOG" 2>&1
PY_RC=$?
set -e
cat "$PY_LOG"
[ "$PY_RC" = 0 ] || { echo "FAIL: python tests failed" >&2; exit 1; }
PY_RAN="$(sed -n 's/^Ran \([0-9][0-9]*\) tests\{0,1\} in.*/\1/p' "$PY_LOG")"
PY_SKIPPED="$(sed -n 's/.*(skipped=\([0-9][0-9]*\)).*/\1/p' "$PY_LOG")"
PY_SKIPPED="${PY_SKIPPED:-0}"
[ -n "$PY_RAN" ] && [ "$PY_RAN" -gt "$PY_SKIPPED" ] || { echo "FAIL: python ran zero tests (ran=$PY_RAN skipped=$PY_SKIPPED)" >&2; exit 1; }
[ "$PY_SKIPPED" = 0 ] || echo "WARNING: $PY_SKIPPED python test(s) SKIPPED (see reasons above); the redaction oracle may not have run" >&2

echo "native-lib: OK (C $C_N checks, python $((PY_RAN - PY_SKIPPED)) tests, $PY_SKIPPED skipped)"
