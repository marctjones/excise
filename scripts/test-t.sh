#!/usr/bin/env bash
# Selftest for scripts/t.sh (#1539).
#
# WHAT IT IS FOR. t.sh is the ad-hoc dev loop: build a project, then test it
# with --no-build. Its failure mode was silent in the worst way — `-c Release`
# was captured AND forwarded, so `dotnet test` got `-c Release` twice, rejected
# it by printing its whole help text, and exited non-zero. The BUILD half had
# already succeeded, so the run produced a real Release build, ran zero tests,
# and read like a usage mistake rather than a tooling bug.
#
# A helper that runs no tests while looking busy is the same class of defect as
# a gate that cannot fail (#1012/#1527), so it gets the same treatment: prove
# the command it would actually run.
#
# Hermetic: t.sh is invoked with a stub `dotnet` on PATH that records its argv.
# No build, no test host, no network. <1 s.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

CHECKS=0
fail() { echo "FAIL: $*" >&2; exit 1; }
ok() { CHECKS=$((CHECKS + 1)); }

# A `dotnet` that records every invocation instead of running one.
mkdir -p "$WORK/bin"
cat > "$WORK/bin/dotnet" <<'STUB'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$RECORD"
exit 0
STUB
chmod +x "$WORK/bin/dotnet"

run_t() {
    RECORD="$WORK/argv.txt"
    : > "$RECORD"
    RECORD="$RECORD" PATH="$WORK/bin:$PATH" bash "$ROOT/scripts/t.sh" "$@" >"$WORK/out.txt" 2>&1 || true
    TEST_LINE="$(grep '^test ' "$RECORD" | head -1 || true)"
    BUILD_LINE="$(grep '^build ' "$RECORD" | head -1 || true)"
}

# 1. THE #1539 CASE. -c Release must reach dotnet test exactly once.
run_t Excise.Avalonia.Tests -c Release --filter "FullyQualifiedName~Nothing"
[ -n "$TEST_LINE" ] || fail "no 'dotnet test' invocation at all: $(cat "$WORK/argv.txt")"
count="$(grep -o -- '-c Release' <<<"$TEST_LINE" | wc -l | tr -d ' ')"
[ "$count" = "1" ] || fail "-c Release reached dotnet test $count time(s), expected 1: $TEST_LINE"
ok
case "$BUILD_LINE" in
    *"-c Release"*) ok ;;
    *) fail "the build must use the requested configuration: $BUILD_LINE" ;;
esac
# The user's own arguments still get through.
case "$TEST_LINE" in
    *"--filter FullyQualifiedName~Nothing"*) ok ;;
    *) fail "the caller's args must be forwarded: $TEST_LINE" ;;
esac

# 2. The long form behaves the same.
run_t Excise.Avalonia.Tests --configuration Release
count="$(grep -o -- '-c Release' <<<"$TEST_LINE" | wc -l | tr -d ' ')"
[ "$count" = "1" ] || fail "--configuration Release reached dotnet test $count time(s): $TEST_LINE"
ok

# 3. The default is Debug, and it is still passed exactly once.
run_t Excise.Avalonia.Tests
count="$(grep -o -- '-c Debug' <<<"$TEST_LINE" | wc -l | tr -d ' ')"
[ "$count" = "1" ] || fail "the default config reached dotnet test $count time(s): $TEST_LINE"
ok

# 4. --no-build is what makes the test half cheap; losing it would silently
#    rebuild and could mask a stale binary.
case "$TEST_LINE" in
    *--no-build*) ok ;;
    *) fail "the test invocation must keep --no-build: $TEST_LINE" ;;
esac

echo "test-t: OK ($CHECKS checks)"
