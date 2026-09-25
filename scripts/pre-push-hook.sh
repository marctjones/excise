#!/usr/bin/env bash
#
# The pre-push gate (#646), as a TRACKED script (#1600).
#
# ONE job: run t0 before every push, over the range actually being pushed.
#
# It earns that. On the day it was installed it blocked two pushes carrying
# unreviewed public API changes and one carrying a broken Excise.Avalonia test —
# each a real defect, caught before it left the machine.
#
# WHY THIS IS A FILE AND NOT A HEREDOC
#
# `scripts/test-tier.sh --install-hook` used to write the whole hook body into
# .git/hooks/pre-push. Two consequences: the logic was untestable (nothing can
# run a heredoc inside an installer without installing it), and every clone ran
# whatever body it happened to have installed months ago. The installed hook is
# now two lines that exec THIS file, so the logic is tracked, reviewable, and
# pinned by scripts/test-check-gate-asymmetry.sh.
#
# THE PUSH RANGE. git feeds "<local ref> <local sha> <remote ref> <remote sha>"
# per pushed ref on stdin.
#
#   * The REMOTE sha is the base the gate-asymmetry gate is defined over
#     ("two pushes, not two commits", #618) — exported as GATE_ASYMMETRY_BASE.
#     An all-zero remote sha (a new remote branch) falls through to the runner's
#     own base selection (LOCAL_GATES.md).
#   * The LOCAL sha is the HEAD of that range — exported as GATE_ASYMMETRY_HEAD
#     (#1600). Until this existed, check-gate-asymmetry.sh always evaluated
#     base...HEAD, so `git push origin <sha>:develop` from a checkout that had
#     moved on checked the WRONG RANGE and reported green over commits nobody
#     asked about. Splitting a push into reviewable ranges — which that gate's
#     own failure message instructs you to do — was impossible to do correctly.
#
# WHAT IS REFUSED, AND WHY IT IS NOT "ANY SHA THAT IS NOT HEAD"
#
# t0 BUILDS AND TESTS THE WORKING TREE. It cannot test a different commit
# without a second worktree and a cold build there, so the pushed sha and the
# tested tree are two different things whenever they differ, and this hook is
# explicit about which is which rather than quietly conflating them:
#
#   * pushed sha == HEAD           — the normal case. Range and tree agree.
#   * pushed sha is an ANCESTOR of HEAD — a stepped push, the workflow #1600 was
#     filed from. The gate-asymmetry range is exact (that is what
#     GATE_ASYMMETRY_HEAD is for) and the test rows run over the working tree,
#     which is a LATER commit. Allowed, with that difference printed. Refusing
#     it outright would have closed the only workflow the issue asked for.
#   * anything else (another branch's tip, a rewritten history, two pushed refs
#     at different commits) — REFUSED. Neither the range nor the tests would
#     describe what is being pushed, and a gate that reports on the wrong tree
#     is worse than no gate.
#
# PUSHES TO main
#
# `main` is the stable release pointer and nothing else (docs/RELEASE_CHECKLIST.md,
# "Release"): it only ever advances to a release tag, by fast-forward. So a push
# to refs/heads/main is judged on exactly that, and NOT on the gate-asymmetry
# range. That range would be remote-main..pushed, which after a release is every
# commit since the last one (2,462 of them for v3.6.0..v3.12.0) and mixes perf
# changes with test changes by construction; those commits each went through
# their own develop push, so re-judging them together says nothing and used to
# refuse the release. For main:
#   * the pushed commit must carry a v* tag, else REFUSED;
#   * the push must be a fast-forward of the remote main, else REFUSED (make
#     main an ancestor first: `git merge -s ours origin/main` on develop is the
#     no-content-change way, see the checklist);
#   * deleting main is REFUSED;
#   * no GATE_ASYMMETRY_BASE/HEAD is exported for it, and t0 still runs over the
#     working tree, so the pushed commit must still be HEAD or an ancestor.
# A push that carries develop AND main keeps the develop range for develop.
#
# There is deliberately no env override. The supported route for the refused
# case is printed in full: run t0 in a temporary worktree at that sha, then push
# with --no-verify. That keeps the work visible instead of letting a variable
# make the gate lie about what it tested.
#
# WHAT THIS HOOK USED TO ALSO DO, AND WHY IT NO LONGER DOES
#
# It refused any `v*` tag that was lightweight or lacked a Release-Evidence
# trailer, to force release tags through scripts/tag-release.sh. Removed because
# it guarded a path nothing had ever taken: v3.6.0, v3.7.0 and v3.8.0 all have
# ZERO Release-Evidence trailers, no v* push was ever attempted while the clause
# was installed, and the script it redirected to had a happy path that had never
# run (#968, closed as won't-do). scripts/tag-release.sh has since been deleted
# along with those trailers. Tag by hand: `git tag -a vX.Y.Z`.
#
# It DOES check one thing about a `v*` tag, added in #1627: that the tag names
# the version the tree declares. That is not evidence-gathering, it is a string
# comparison against Directory.Build.props, and it closes a failure that
# actually happened — a whole release cycle shipped with the About window
# reading "version 1.0.0" because a tagged build stamps the assemblies from the
# TREE, and nothing compared the two. Cost: one `sed`, no build, no run.
set -uo pipefail

ROOT="$(git rev-parse --show-toplevel)" || exit 1
cd "$ROOT" || exit 1

head_sha="$(git rev-parse --verify HEAD 2>/dev/null || true)"
base=""
pushed=""
range_pushed=""
tags=""

refuse_main() {
    echo ""
    echo "pre-push: REFUSED — a push to main must be a fast-forward to a release tag."
    echo ""
    echo "  $1"
    echo ""
    echo "  main is the stable release pointer (docs/RELEASE_CHECKLIST.md, \"Release\"):"
    echo "    git fetch origin"
    echo "    git push origin vX.Y.Z^{commit}:main"
    echo "  If main is not an ancestor of the release, make it one without changing any"
    echo "  file:  git merge -s ours origin/main   (on develop, before tagging)."
    echo ""
    exit 1
}

if [ ! -t 0 ]; then
    while read -r _lref lsha _rref rsha; do
        [ -n "${lsha:-}" ] || continue
        # An all-zero LOCAL sha is a ref deletion: there is no tree to test and
        # no range to check, so it contributes nothing. (Pushing a deletion
        # alongside a branch is how the base for the branch still gets read.)
        case "$lsha" in
            *[!0]*) ;;
            *)
                [ "${_rref:-}" != "refs/heads/main" ] || refuse_main "deleting main is not allowed."
                continue ;;
        esac

        # Peel to a commit. An annotated tag's local sha is the TAG OBJECT, so
        # comparing it raw to HEAD would refuse every `git tag -a` push.
        lcommit="$(git rev-parse --verify --quiet "${lsha}^{commit}" 2>/dev/null || true)"
        [ -n "$lcommit" ] || lcommit="$lsha"

        case " $pushed " in
            *" $lcommit "*) ;;
            *) pushed="${pushed:+$pushed }$lcommit" ;;
        esac

        is_main=0
        if [ "${_rref:-}" = "refs/heads/main" ]; then
            is_main=1
            git describe --exact-match --tags --match 'v*' "$lcommit" >/dev/null 2>&1 \
                || refuse_main "$lcommit is not the commit of a v* release tag."
            case "$rsha" in
                *[!0]*)
                    git merge-base --is-ancestor "$rsha" "$lcommit" 2>/dev/null \
                        || refuse_main "remote main ($rsha) is not an ancestor of $lcommit (or is not fetched), so this is not a fast-forward."
                    ;;
            esac
        else
            # The gate-asymmetry range belongs to branches under review, not to the
            # release pointer (see "PUSHES TO main" in the header).
            case "$rsha" in *[!0]*) base="$rsha" ;; esac
            case " $range_pushed " in
                *" $lcommit "*) ;;
                *) range_pushed="${range_pushed:+$range_pushed }$lcommit" ;;
            esac
        fi

        # #1627: remember the release tags being pushed, checked below.
        case "${_rref:-}" in
            refs/tags/v*) tags="${tags:+$tags }${_rref#refs/tags/}" ;;
        esac
    done
fi

# A `v*` tag must name the version the tree declares, or the build it produces
# reports a different version than the release is called.
for _tag in ${tags:-}; do
    if ! scripts/check-version-consistency.sh --tag "$_tag"; then
        echo ""
        echo "pre-push: REFUSED — the tag and the tree declare different versions (#1627)."
        echo ""
        echo "  Fix the tree, re-tag, and push again:"
        echo "    scripts/set-version.sh ${_tag#v}"
        echo "    git commit -am \"chore: ${_tag#v}\""
        echo "    git tag -f -a $_tag -m \"excise $_tag\""
        echo ""
        exit 1
    fi
done

pushed_count=0
for _sha in ${pushed:-}; do
    pushed_count=$((pushed_count + 1))
done

refuse() {
    echo ""
    echo "pre-push: REFUSED — t0 would not test what you are pushing (#1600)."
    echo ""
    echo "  $1"
    echo ""
    echo "  t0 builds and tests the WORKING TREE, so its verdict describes"
    echo "  HEAD ($head_sha), not the commit(s) above. Pushing on that verdict"
    echo "  would gate a tree nobody ran."
    echo ""
    echo "  Supported routes:"
    echo ""
    echo "    1. Push what you have tested:"
    echo "         git push origin HEAD:<branch>"
    echo ""
    echo "    2. Test the commit you mean to push, in its own worktree:"
    echo "         git worktree add /tmp/excise-push <sha>"
    echo "         (cd /tmp/excise-push && scripts/test-tier.sh t0)"
    echo "         git push --no-verify origin <sha>:<branch>"
    echo "         git worktree remove /tmp/excise-push"
    echo ""
    exit 1
}

if [ "$pushed_count" -gt 1 ]; then
    refuse "several refs at different commits in one push: $pushed"
fi

if [ "$pushed_count" -eq 1 ] && [ -n "$head_sha" ] && [ "$pushed" != "$head_sha" ]; then
    if git merge-base --is-ancestor "$pushed" "$head_sha" 2>/dev/null; then
        echo "pre-push: stepped push — the gate-asymmetry range ends at the pushed"
        echo "          commit, but the build and tests run over the working tree:"
        echo "            pushed: $pushed $(git log -1 --format=%s "$pushed" 2>/dev/null)"
        echo "            tested: $head_sha $(git log -1 --format=%s "$head_sha" 2>/dev/null)"
    else
        refuse "pushed commit $pushed is not this checkout's HEAD, and not an ancestor of it"
    fi
fi

[ -n "$base" ] && export GATE_ASYMMETRY_BASE="$base"
[ -n "$range_pushed" ] && export GATE_ASYMMETRY_HEAD="$range_pushed"

# git spawns hooks with the invoking process's environment, not a login shell, so a PATH
# fixup living only in ~/.zprofile/~/.zshrc is invisible here. Prepend the official SDK
# explicitly (global.json pins it; CLAUDE.md "Build Failures" explains why the Homebrew
# formula must not be picked up instead) so this hook is correct regardless of what shell
# or tool invoked `git push`.
[ -d "$HOME/.dotnet" ] && export PATH="$HOME/.dotnet:$PATH"

exec "$ROOT/scripts/test-tier.sh" t0
