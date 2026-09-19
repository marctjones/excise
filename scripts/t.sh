#!/usr/bin/env bash
# Build-then-test wrapper for ad-hoc runs (#950).
#
# Usage:
#   scripts/t.sh [project-or-solution ...] [dotnet test args...]
#   scripts/t.sh Excise.Core.Tests/Excise.Core.Tests.csproj --filter Redaction
#
# Non-option arguments before the first option are treated as project/solution
# targets and are tested one at a time. This avoids `dotnet build a b` style
# command lines that look like they built multiple projects but did not.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

CONFIG="Debug"
TARGETS=()
TEST_ARGS=()
SEEN_OPTION=0

while [[ $# -gt 0 ]]; do
    case "$1" in
        -c|--configuration)
            # #1539: captured, NOT forwarded. Both the build and the test
            # invocation below pass -c "$CONFIG" themselves, so appending it
            # here handed `dotnet test` the option twice. dotnet test rejects
            # that by printing its whole help text and exiting non-zero — while
            # the build half had already succeeded — so `scripts/t.sh -c
            # Release` produced a real Release build, ran NO tests, and looked
            # like the user had mistyped something.
            CONFIG="${2:-Debug}"
            shift 2
            SEEN_OPTION=1
            ;;
        --)
            shift
            TEST_ARGS+=("$@")
            break
            ;;
        -*)
            SEEN_OPTION=1
            TEST_ARGS+=("$1")
            shift
            ;;
        *)
            if [[ "$SEEN_OPTION" == "0" ]]; then
                TARGETS+=("$1")
            else
                TEST_ARGS+=("$1")
            fi
            shift
            ;;
    esac
done

if [[ "${#TARGETS[@]}" -eq 0 ]]; then
    TARGETS=("excise.sln")
fi

for target in "${TARGETS[@]}"; do
    echo "==> building $target ($CONFIG)"
    dotnet build "$target" -c "$CONFIG"
    echo "==> testing $target ($CONFIG, --no-build)"
    # ${a[@]+"${a[@]}"} — an EMPTY array is "unbound" under set -u in bash 3.2
    # (macOS /bin/bash), so a plain "${TEST_ARGS[@]}" aborts the run whenever
    # t.sh is called with no test arguments at all, i.e. exactly when you want
    # the whole project. Nothing caught it because every existing caller passes
    # a --filter.
    dotnet test "$target" --no-build -c "$CONFIG" ${TEST_ARGS[@]+"${TEST_ARGS[@]}"}
done
