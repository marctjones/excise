#!/usr/bin/env bash
# TOOLING — not a gate (tests/gates-tooling.txt): the comparison runs in t0 inside Excise.Cli.Tests (#977); this prints the whole list for humans
#
# Do the rendering-quality contracts and the corpus expectation manifests say
# the same thing about the same page? (#977)
#
# test-pdfs/rendering-contracts/** pins ExpectedRawStatus per page and
# render-quality-scan grades against it; tests/corpus-expectations*.tsv pins the
# same status for page 1 and the corpus scan grades against that. Nothing
# compared the two until this, so a page could be green in one and years stale
# in the other — three of the annotation pages #932 re-pinned had contracts
# stuck at PASS_ONE while the manifest said MISSING_CONTENT.
#
# This is a file comparison. No corpus, no renderer, no network: both inputs are
# checked in, and it reuses the two production loaders rather than re-parsing
# either format.
#
# The same comparison runs in t0 as
# CorpusScanClassificationTests.Contracts_AgreeWithTheCorpusExpectationManifests
# (Excise.Cli.Tests). This script is the human-facing entry point: it prints
# EVERY disagreement, so a list can be worked through rather than fixed one row
# per run.
#
# Usage:
#   scripts/check-contract-manifest-agreement.sh [--contracts DIR] [--repo-root DIR]
#
# The two options exist so the comparison can be pointed at synthetic inputs —
# The comparison itself is pinned in t0 by Excise.Cli.Tests
# (Contracts_AgreeWithTheCorpusExpectationManifests, #977); this script prints
# the whole list for humans. It drives a two-page fixture
# through it, one page agreeing and one disagreeing, which is the only way to
# see this gate fail without editing checked-in expectations. They default to
# the real trees, so every existing caller is unaffected.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

CONTRACTS="$ROOT/test-pdfs/rendering-contracts"
REPO_ROOT="$ROOT"
while [[ $# -gt 0 ]]; do
    case "$1" in
        --contracts) CONTRACTS="$2"; shift 2 ;;
        --repo-root) REPO_ROOT="$2"; shift 2 ;;
        *) echo "usage: scripts/check-contract-manifest-agreement.sh [--contracts DIR] [--repo-root DIR]" >&2; exit 2 ;;
    esac
done

exec dotnet run --project tools/Excise.RenderTools -- contract-manifest-agreement \
    --contracts "$CONTRACTS" \
    --repo-root "$REPO_ROOT"
