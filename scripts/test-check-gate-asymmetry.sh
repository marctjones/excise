#!/usr/bin/env bash
#
# Selftest for scripts/check-gate-asymmetry.sh (#618, selftested per #1012).
#
# The gate flags a commit range that BOTH touches a performance-sensitive path
# AND rewrites the expected values of a correctness assertion — the shape of
# 8a8e661, where a perf optimization silently redefined what a correctness test
# considered correct. Two of its own pathspecs were dead for months (a bare
# prefix does not match `Foo.cs`), so it watched files it did not watch; that is
# why the preflight case below is here.
#
# Driven inside a synthetic git repo: the script derives its ROOT from its own
# location, so a copy of it in $TMP/scripts makes $TMP the repo under test. The
# real repository and its history are never involved.
#
# Sub-second. Runs in t0.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SCRIPT="$ROOT/scripts/check-gate-asymmetry.sh"

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
fail() { echo "FAIL: $*" >&2; exit 1; }

export GIT_AUTHOR_NAME=selftest GIT_AUTHOR_EMAIL=selftest@example.invalid
export GIT_COMMITTER_NAME=selftest GIT_COMMITTER_EMAIL=selftest@example.invalid

R="$TMP/repo"
mkdir -p "$R/scripts"
cp "$SCRIPT" "$R/scripts/check-gate-asymmetry.sh"

g() { git -C "$R" "$@"; }

# Every pathspec the gate declares must match a TRACKED file or its preflight
# fails — so the synthetic repo has to carry one file per declared path. Derived
# from the gate, not hard-coded, so a new perf path does not silently make this
# selftest test a different thing than the gate does.
seed_perf_paths() {
    while IFS= read -r p; do
        [[ -z "$p" ]] && continue
        local f="${p%\*}"
        case "$f" in
            */) f="${f}seed.cs" ;;
            *)  f="${f}.cs" ;;
        esac
        mkdir -p "$R/$(dirname "$f")"
        printf 'class Seed { void M() { } }\n' > "$R/$f"
    done < <(sed -n "/^PERF_PATHS='$/,/^'$/p" "$SCRIPT" | sed '1d;$d')
}

g init -q
seed_perf_paths
mkdir -p "$R/Fake.Tests"
cat > "$R/Fake.Tests/WidgetTests.cs" <<'CS'
public class WidgetTests
{
    public void TileRect_IsQuantized()
    {
        Tile(400, 600).Width.Should().Be(1280);
    }
}
CS
g add -A
g commit -qm "base"
g branch base

run() {   # run [base-ref]
    set +e
    OUT="$("$R/scripts/check-gate-asymmetry.sh" "$@" 2>&1)"
    RC=$?
    set -e
}

echo "==> selftest: check-gate-asymmetry.sh"

# ── 1. Nothing changed -> pass ───────────────────────────────────────────────
run base
[[ "$RC" -eq 0 ]] || fail "an empty range must pass (exit $RC)
$OUT"
grep -q "no performance-sensitive paths touched" <<<"$OUT" || fail "unexpected verdict:
$OUT"
echo "    empty range                          exit 0"

# ── 2. A perf path alone -> pass ─────────────────────────────────────────────
printf 'class Seed { void M() { /* faster */ } }\n' > "$R/Excise.Rendering/seed.cs"
g add -A; g commit -qm "perf: tweak the renderer"
run base
[[ "$RC" -eq 0 ]] || fail "touching a perf path alone is not an asymmetry (exit $RC)
$OUT"
grep -q "no correctness expectations rewritten" <<<"$OUT" || fail "unexpected verdict:
$OUT"
echo "    perf path only                       exit 0"

# ── 3. THE GUARDED PROPERTY: perf path + rewritten expectation -> FAIL ───────
sed -i.bak 's/Be(1280)/Be(2560)/' "$R/Fake.Tests/WidgetTests.cs"; rm -f "$R/Fake.Tests/WidgetTests.cs.bak"
grep -q "Be(2560)" "$R/Fake.Tests/WidgetTests.cs" || fail "mutation did not reach the test file"
printf 'class Seed { void M() { /* faster still */ } }\n' > "$R/Excise.Rendering/seed.cs"
g add -A; g commit -qm "perf: coalesce tile renders"
run base
[[ "$RC" -ne 0 ]] || fail "a perf change that rewrote a correctness expectation MUST fail — that is the gate
$OUT"
grep -q "Fake.Tests/WidgetTests.cs" <<<"$OUT" || fail "the failure must name the rewritten test:
$OUT"
echo "    perf path + rewritten expectation    exit $RC"

# ── 4. The same, ACKNOWLEDGED in the commit message -> pass ──────────────────
# The gate forbids doing it quietly, not doing it. If this branch did not work
# the gate would be unusable and would get switched off, which is the same
# outcome as having no gate.
g commit -q --allow-empty -m "docs: say so out loud

Correctness-Expectations-Changed: the tile contract genuinely changed (#617)"
run base
[[ "$RC" -eq 0 ]] || fail "an acknowledged expectation change must pass (exit $RC)
$OUT"
grep -q "ACKNOWLEDGED" <<<"$OUT" || fail "expected the acknowledgement verdict:
$OUT"
echo "    same, acknowledged in the message    exit 0"

# ── 5. A pathspec that matches no tracked file -> FAIL ───────────────────────
# A gate that quietly stops watching still reads as green. This is the #941
# finding: two dead pathspecs, silently skipped.
g rm -q -r Excise.Rendering
g commit -qm "chore: move the renderer away"
run base
[[ "$RC" -ne 0 ]] || fail "a declared perf path matching NO tracked file must fail — the gate stopped watching
$OUT"
grep -q "match NO" <<<"$OUT" || fail "expected the dead-pathspec verdict:
$OUT"
echo "    dead pathspec                        exit $RC"

# ── 6. A missing base ref is a failure, not a silent skip ────────────────────
run refs/heads/does-not-exist
[[ "$RC" -ne 0 ]] || fail "without a base ref this gate can only pretend to pass
$OUT"
echo "    missing base ref                     exit $RC"

set +e
OUT="$(GATE_ASYMMETRY_ALLOW_NO_BASE=1 "$R/scripts/check-gate-asymmetry.sh" refs/heads/does-not-exist 2>&1)"
RC=$?
set -e
[[ "$RC" -eq 0 ]] || fail "the documented opt-out must still work (exit $RC)
$OUT"
echo "    missing base ref, explicit opt-out   exit 0"

echo "==> check-gate-asymmetry.sh selftest OK"
