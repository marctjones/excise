#!/usr/bin/env bash
# Selftest for scripts/check-gate-asymmetry.sh and scripts/pre-push-hook.sh
# (#1600, #618).
#
# WHAT IT IS FOR. #1600 was a gate reporting on the WRONG RANGE: the hook read
# git's push range off stdin, exported the remote sha as the base, threw the
# local sha away, and check-gate-asymmetry.sh evaluated `base...HEAD`. A
# `git push origin <sha>:develop` from a checkout that had moved on therefore
# checked commits nobody was pushing — and reported green. Nothing could have
# noticed: the gate's own output printed only the base.
#
# So this pins BOTH halves, and the shape of each case is "plant the violation,
# watch the exit code":
#
#   * the checker: the range actually honours a head that is not HEAD, an
#     unresolvable head FAILS instead of quietly evaluating something else, and
#     — the baseline that keeps the rest honest — a genuine asymmetry still
#     fails (a checker that cannot fail proves nothing about the ranges it
#     accepts).
#   * the hook: which push shapes it runs on and which it refuses, including
#     the two that a naive `[ "$lsha" = "$(git rev-parse HEAD)" ]` gets wrong —
#     an annotated tag (local sha is the TAG OBJECT, so a raw compare refuses
#     every `git tag -a` push) and a ref deletion (all-zero local sha).
#
# Hermetic: a synthetic git repo in a temp dir, the two real scripts copied in,
# and a STUB scripts/test-tier.sh that records the environment it was exec'd
# with. No dotnet, no corpus, no network. ~1 s.
#
# ⚠️ It must never run `test-tier.sh --install-hook`: hooks live in the SHARED
# git dir, so from a linked worktree that writes the main checkout's
# .git/hooks/pre-push. The stub repo is the whole point.
set -euo pipefail

# ⚠️ Every case below judges a range in the SYNTHETIC repo, so an inherited
# GATE_ASYMMETRY_* value is always wrong here — and silently so. The pre-push
# hook exports both, then runs the tier runner, which runs this selftest: with
# GATE_ASYMMETRY_HEAD inherited, case 1's `check-gate-asymmetry.sh $BASE_SHA`
# resolved its head to a sha from the REAL repo, judged an empty range and
# exited 0, so the baseline "the gate can fail" case failed. #1600 unset them
# for the hook sub-case only; they have to be unset for all of them.
unset GATE_ASYMMETRY_BASE GATE_ASYMMETRY_HEAD GATE_ASYMMETRY_ALLOW_NO_BASE

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

CHECKS=0
fail() { echo "FAIL: $*" >&2; exit 1; }
ok() { CHECKS=$((CHECKS + 1)); }

REPO="$WORK/repo"
mkdir -p "$REPO/scripts"
cp "$ROOT/scripts/check-gate-asymmetry.sh" "$REPO/scripts/"
cp "$ROOT/scripts/pre-push-hook.sh" "$REPO/scripts/"

# The stub stands in for the real tier runner: it records what the hook handed
# it and exits 0, so a hook case tests the hook's decision, not t0.
cat > "$REPO/scripts/test-tier.sh" <<'STUB'
#!/usr/bin/env bash
root="$(git rev-parse --show-toplevel)"
{
    echo "args=$*"
    echo "base=${GATE_ASYMMETRY_BASE-<unset>}"
    echo "head=${GATE_ASYMMETRY_HEAD-<unset>}"
} > "$root/.tier-invocation"
exit 0
STUB
cp "$ROOT/scripts/check-version-consistency.sh" "$REPO/scripts/"
chmod +x "$REPO/scripts/"*.sh

cd "$REPO"
git init -q -b develop .
git config user.email selftest@example.com
git config user.name Selftest
git config commit.gpgsign false

# Every pathspec check-gate-asymmetry.sh declares must match a TRACKED file or
# its own preflight fails the run (a pathspec matching nothing is silent, #941).
mkdir -p Excise.Rendering Excise.Avalonia/Controls Excise.Core/Content \
         Excise.Core/Fonts tools/Excise.RenderTools Excise.Benchmarks Demo.Tests
echo "// hot path" > Excise.Rendering/Renderer.cs

# #1627: the hook refuses a v* tag that disagrees with the tree, so the
# synthetic repo needs the two files that declare the version. v9.9.9 below
# agrees; the mismatch case is exercised separately.
printf '<Project><PropertyGroup><VersionPrefix>9.9.9</VersionPrefix></PropertyGroup></Project>\n' > Directory.Build.props
printf '# Changelog\n\n## [Unreleased]\n\n## [9.9.9] - 2026-01-01\n' > CHANGELOG.md
echo "// viewer" > Excise.Avalonia/Controls/PdfViewerControl.cs
echo "// parser" > Excise.Core/Content/ContentStreamParser.cs
echo "// fonts" > Excise.Core/Fonts/Cff.cs
echo "// tools" > tools/Excise.RenderTools/Program.cs
echo "// bench" > Excise.Benchmarks/Bench.cs
cat > Demo.Tests/TileTests.cs <<'EOF'
public class TileTests
{
    public void TileSize_IsQuantized() => Actual().Should().Be(1280);
}
EOF
git add -A
git commit -qm "base"
BASE_SHA="$(git rev-parse HEAD)"

# B: a performance-sensitive path alone. Legal on its own.
echo "// faster" >> Excise.Rendering/Renderer.cs
git commit -qam "perf: coalesce tile renders"
PERF_SHA="$(git rev-parse HEAD)"

# C: rewrite the expected value of a correctness assertion. Legal on its own,
# and #618's whole subject in the SAME RANGE as B.
sed -i '' 's/Be(1280)/Be(2560)/' Demo.Tests/TileTests.cs
git commit -qam "test: tile size is 2560"
REWRITE_SHA="$(git rev-parse HEAD)"

# D: a commit that is NOT an ancestor of HEAD, for the hook's refusal case.
git checkout -q -b side "$BASE_SHA"
echo "// side" >> Excise.Rendering/Renderer.cs
git commit -qam "side: unrelated work"
SIDE_SHA="$(git rev-parse HEAD)"
git checkout -q develop

git tag -a v9.9.9 -m "release 9.9.9"
TAG_SHA="$(git rev-parse v9.9.9)"   # the TAG OBJECT, not the commit
[ "$TAG_SHA" != "$REWRITE_SHA" ] || fail "expected an annotated tag object distinct from the commit"

# ───────────────────────────── the checker ──────────────────────────────────

# 1. BASELINE: the gate can fail. Without this, every "passes" case below could
#    be a gate that passes everything.
out="$(scripts/check-gate-asymmetry.sh "$BASE_SHA" 2>&1)" && rc=0 || rc=$?
[ "$rc" -eq 1 ] || fail "base...HEAD spans both a perf path and a rewritten expectation; expected exit 1, got $rc"
case "$out" in
    *"Excise.Rendering/Renderer.cs"*) ok ;;
    *) fail "the failure must name the perf-sensitive file it saw: $out" ;;
esac
case "$out" in
    *"Demo.Tests/TileTests.cs"*) ok ;;
    *) fail "the failure must name the test whose expectation changed: $out" ;;
esac

# 2. THE #1600 FIX: the same base, with the head set to the intermediate commit,
#    is the range that was actually pushed — and it is clean. Before the fix the
#    head was hard-coded to HEAD and this reported the failure above.
out="$(GATE_ASYMMETRY_HEAD="$PERF_SHA" scripts/check-gate-asymmetry.sh "$BASE_SHA" 2>&1)" \
    || fail "base...perf touches a perf path and rewrites nothing; expected pass. Got: $out"
case "$out" in
    *"head=$PERF_SHA"*) ok ;;
    *) fail "the resolved head must be printed, so a reader can see which range was judged: $out" ;;
esac
case "$out" in
    *"base=$BASE_SHA"*) ok ;;
    *) fail "the resolved base must still be printed: $out" ;;
esac

# 3. The same head as a positional argument.
GATE_ASYMMETRY_HEAD="" scripts/check-gate-asymmetry.sh "$BASE_SHA" "$PERF_SHA" >/dev/null 2>&1 \
    || fail "a positional head-ref must work like GATE_ASYMMETRY_HEAD"
ok

# 4. An unresolvable head is a HARD FAILURE, never a silent fall back to HEAD —
#    that fall back is exactly the #1600 defect. And the missing-base escape
#    hatch must not launder it into a SKIP (77).
out="$(GATE_ASYMMETRY_HEAD=no-such-ref scripts/check-gate-asymmetry.sh "$BASE_SHA" 2>&1)" && rc=0 || rc=$?
[ "$rc" -eq 1 ] || fail "an unresolvable head must exit 1, got $rc"
case "$out" in
    *"head ref 'no-such-ref' not found"*) ok ;;
    *) fail "the message must name the ref it could not resolve: $out" ;;
esac
out="$(GATE_ASYMMETRY_ALLOW_NO_BASE=1 GATE_ASYMMETRY_HEAD=no-such-ref \
        scripts/check-gate-asymmetry.sh "$BASE_SHA" 2>&1)" && rc=0 || rc=$?
[ "$rc" -eq 1 ] || fail "ALLOW_NO_BASE is about an unfetchable base, not a bad head; expected exit 1, got $rc"
ok

# 5. A missing BASE still fails (and still has its documented 77 escape).
scripts/check-gate-asymmetry.sh no-such-base >/dev/null 2>&1 && rc=0 || rc=$?
[ "$rc" -eq 1 ] || fail "a missing base must exit 1, got $rc"
GATE_ASYMMETRY_ALLOW_NO_BASE=1 scripts/check-gate-asymmetry.sh no-such-base >/dev/null 2>&1 && rc=0 || rc=$?
[ "$rc" -eq 77 ] || fail "GATE_ASYMMETRY_ALLOW_NO_BASE=1 must SKIP (77), got $rc"
ok

# ─────────────────────────────── the hook ───────────────────────────────────

# Runs the hook with one push line per argument, and answers with the recorded
# invocation (or "<not run>" when the hook refused before exec'ing the runner).
run_hook() {
    rm -f "$REPO/.tier-invocation"
    local lines="" line
    for line in "$@"; do lines+="$line"$'\n'; done
    # The hook must be judged on what IT exports, so strip anything the
    # surrounding runner already put in the environment: a t1 run exports
    # GATE_ASYMMETRY_BASE for its own gate row, and an inherited value made the
    # all-zero-remote case read base=<that> instead of base=<unset>.
    HOOK_OUT="$(printf '%s' "$lines" \
        | env -u GATE_ASYMMETRY_BASE -u GATE_ASYMMETRY_HEAD scripts/pre-push-hook.sh 2>&1)" \
        && HOOK_RC=0 || HOOK_RC=$?
    if [ -f "$REPO/.tier-invocation" ]; then
        HOOK_RAN="$(cat "$REPO/.tier-invocation")"
    else
        HOOK_RAN="<not run>"
    fi
}

ZERO=0000000000000000000000000000000000000000

# 6. The normal push: HEAD to a remote that is at the base commit.
run_hook "refs/heads/develop $REWRITE_SHA refs/heads/develop $BASE_SHA"
[ "$HOOK_RC" -eq 0 ] || fail "a push of HEAD must run t0, got rc=$HOOK_RC: $HOOK_OUT"
case "$HOOK_RAN" in
    *"args=t0"*) ok ;;
    *) fail "the hook must exec the tier runner with t0: $HOOK_RAN" ;;
esac
case "$HOOK_RAN" in
    *"base=$BASE_SHA"*) ok ;;
    *) fail "the remote sha must be exported as GATE_ASYMMETRY_BASE: $HOOK_RAN" ;;
esac
case "$HOOK_RAN" in
    *"head=$REWRITE_SHA"*) ok ;;
    *) fail "#1600: the PUSHED sha must be exported as GATE_ASYMMETRY_HEAD: $HOOK_RAN" ;;
esac

# 7. A stepped push — an earlier commit of this same branch, which is the
#    workflow #1600 was filed from. Allowed, with the range ending at the pushed
#    commit, and it must SAY that the tested tree is a later commit.
run_hook "refs/heads/develop $PERF_SHA refs/heads/develop $BASE_SHA"
[ "$HOOK_RC" -eq 0 ] || fail "a stepped push must be allowed, got rc=$HOOK_RC: $HOOK_OUT"
case "$HOOK_RAN" in
    *"head=$PERF_SHA"*) ok ;;
    *) fail "the range must end at the pushed commit, not HEAD: $HOOK_RAN" ;;
esac
case "$HOOK_OUT" in
    *"stepped push"*) ok ;;
    *) fail "the hook must say the tested tree differs from the pushed commit: $HOOK_OUT" ;;
esac

# 8. A commit that is not an ancestor of HEAD: neither the range nor the tests
#    would describe it, so the hook refuses and the runner never starts.
run_hook "refs/heads/side $SIDE_SHA refs/heads/side $BASE_SHA"
[ "$HOOK_RC" -eq 1 ] || fail "pushing a non-ancestor commit must be refused, got rc=$HOOK_RC"
[ "$HOOK_RAN" = "<not run>" ] || fail "a refused push must not run the tier: $HOOK_RAN"
case "$HOOK_OUT" in
    *REFUSED*worktree*) ok ;;
    *) fail "the refusal must name the supported route: $HOOK_OUT" ;;
esac

# 9. An ANNOTATED TAG at HEAD. Its local sha is the tag object, so a raw compare
#    against HEAD would refuse every `git tag -a` push.
run_hook "refs/tags/v9.9.9 $TAG_SHA refs/tags/v9.9.9 $ZERO"
[ "$HOOK_RC" -eq 0 ] || fail "an annotated tag at HEAD must not be refused, got rc=$HOOK_RC: $HOOK_OUT"
case "$HOOK_RAN" in
    *"head=$REWRITE_SHA"*) ok ;;
    *) fail "the tag must be peeled to its commit: $HOOK_RAN" ;;
esac

# 9b. #1627: a v* tag whose version disagrees with the tree is REFUSED, before
#     the tier runs. A tagged build stamps the assemblies from the TREE, so a
#     mismatch ships a binary whose About window names a different release —
#     which is exactly what happened for a whole cycle.
git tag -a v8.8.8 -m "wrong version"
MISTAG_SHA="$(git rev-parse v8.8.8)"
run_hook "refs/tags/v8.8.8 $MISTAG_SHA refs/tags/v8.8.8 $ZERO"
[ "$HOOK_RC" -eq 1 ] || fail "a tag disagreeing with VersionPrefix must be refused, got rc=$HOOK_RC: $HOOK_OUT"
[ "$HOOK_RAN" = "<not run>" ] || fail "a refused tag must not run the tier: $HOOK_RAN"
case "$HOOK_OUT" in
    *"set-version.sh 8.8.8"*) ok ;;
    *) fail "the refusal must name the fix: $HOOK_OUT" ;;
esac
git tag -d v8.8.8 >/dev/null

# 10. A new remote branch: the all-zero REMOTE sha is no base, so the runner
#     falls through to its own base selection rather than being handed nonsense.
run_hook "refs/heads/feature $REWRITE_SHA refs/heads/feature $ZERO"
[ "$HOOK_RC" -eq 0 ] || fail "a new remote branch must run t0, got rc=$HOOK_RC: $HOOK_OUT"
case "$HOOK_RAN" in
    *"base=<unset>"*) ok ;;
    *) fail "an all-zero remote sha must not become a base: $HOOK_RAN" ;;
esac

# 11. A ref DELETION: the all-zero LOCAL sha is no tree and no range, so it
#     contributes neither a head nor a base — and must not be mistaken for a
#     commit that differs from HEAD and refused.
run_hook "(delete) $ZERO refs/heads/stale $PERF_SHA"
[ "$HOOK_RC" -eq 0 ] || fail "a branch deletion must not be refused, got rc=$HOOK_RC: $HOOK_OUT"
case "$HOOK_RAN" in
    *"head=<unset>"*) ok ;;
    *) fail "a deletion has no pushed tree; the head must stay unset: $HOOK_RAN" ;;
esac
case "$HOOK_RAN" in
    *"base=<unset>"*) ok ;;
    *) fail "a deletion's remote sha is not a base for anything: $HOOK_RAN" ;;
esac

# 12. Two refs at different commits in one push: one t0 run cannot describe two
#     trees.
run_hook "refs/heads/develop $REWRITE_SHA refs/heads/develop $BASE_SHA" \
         "refs/heads/side $SIDE_SHA refs/heads/side $ZERO"
[ "$HOOK_RC" -eq 1 ] || fail "two refs at different commits must be refused, got rc=$HOOK_RC"
[ "$HOOK_RAN" = "<not run>" ] || fail "a refused push must not run the tier: $HOOK_RAN"
ok

# 13. The same commit under two refs (branch + tag) is ONE tree: allowed.
run_hook "refs/heads/develop $REWRITE_SHA refs/heads/develop $BASE_SHA" \
         "refs/tags/v9.9.9 $TAG_SHA refs/tags/v9.9.9 $ZERO"
[ "$HOOK_RC" -eq 0 ] || fail "one commit under two refs must be allowed, got rc=$HOOK_RC: $HOOK_OUT"
ok

# 14. The installed hook is a STUB that execs the tracked script, so a fix lands
#     without re-installing. Read out of test-tier.sh rather than installed,
#     because hooks live in the shared git dir (see the header).
grep -q 'hook="\$(git rev-parse --show-toplevel)/scripts/pre-push-hook.sh"' "$ROOT/scripts/test-tier.sh" \
    || fail "test-tier.sh --install-hook must install a stub that execs scripts/pre-push-hook.sh"
grep -q 'exec "\$hook" "\$@"' "$ROOT/scripts/test-tier.sh" \
    || fail "the stub must exec the tracked script, passing stdin and arguments through"
# The stub serves EVERY worktree through the shared git dir, including a branch
# with no scripts/pre-push-hook.sh. Missing must refuse, never skip.
grep -q 'if \[ ! -x "\$hook" \]; then' "$ROOT/scripts/test-tier.sh" \
    || fail "the stub must handle a branch that predates the tracked hook script"
awk '/if \[ ! -x "\$hook" \]; then/,/^fi$/' "$ROOT/scripts/test-tier.sh" | grep -q 'exit 1' \
    || fail "a missing hook script must REFUSE the push (exit 1), not fall through to no gate"
grep -q 'git rev-parse --git-path hooks/pre-push' "$ROOT/scripts/test-tier.sh" \
    || fail "the hook path must come from git, not \$ROOT/.git (a worktree's .git is a FILE)"
ok

echo "test-check-gate-asymmetry: OK ($CHECKS checks)"
