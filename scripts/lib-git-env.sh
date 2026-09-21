#!/usr/bin/env bash
# Sourced library: start a script that builds or mutates a SCRATCH git repository
# from a clean git environment (#1673). Sourced, never executed.
#
# WHY THIS EXISTS. Git exports GIT_DIR (and can export GIT_WORK_TREE,
# GIT_INDEX_FILE, GIT_OBJECT_DIRECTORY, GIT_COMMON_DIR, GIT_CONFIG_* ...) to
# every hook it runs, and GIT_DIR BEATS THE WORKING DIRECTORY. The pre-push hook
# runs t0, t0 runs the selftests, so a selftest that did `cd "$scratch"; git init`
# re-initialised the REAL repository: core.bare flipped to true and a fake
# `Selftest <selftest@example.com>` identity landed in the shared config, which
# broke the main checkout and every linked worktree at once.
#
# `git -C <dir>` and explicit paths are not enough on their own: GIT_DIR still
# wins for `init` and `config`. The variables have to be removed.
#
# USE. Source this file and call scrub_inherited_git_env BEFORE the first `git init`
# in the script. scripts/test-check-gate-asymmetry.sh enforces that for every
# script under scripts/ (the "#1673 sweep" case), including the ORDER, and plants
# an offender of each shape to prove the enforcement can fail.

# Unset every GIT_* variable in the current shell. The whole family, not the
# names that happened to bite: a synthetic repository has no use for any value
# an outer process injected, and an unswept sibling is how the second bug starts.
scrub_inherited_git_env() {
    local _v
    for _v in $(env | sed -n 's/^\(GIT_[A-Za-z0-9_]*\)=.*/\1/p'); do
        unset "$_v"
    done
}
